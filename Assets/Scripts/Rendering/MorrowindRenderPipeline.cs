using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Arycama.CustomRenderPipeline;
using UnityEngine.Experimental.Rendering;
using CommandBufferPool = Arycama.CustomRenderPipeline.CommandBufferPool;

public class MorrowindRenderPipeline : CustomRenderPipeline
{
    private static readonly IndexedString blueNoise1DIds = new("STBN/Scalar/stbn_scalar_2Dx1Dx1D_128x128x64x1_");
    private static readonly IndexedString blueNoise2DIds = new("STBN/Vec2/stbn_vec2_2Dx1D_128x128x64_");

    private readonly MorrowindRenderPipelineAsset renderPipelineAsset;

    private readonly DynamicResolution dynamicResolution;
    private CustomSampler frameTimeSampler;

    private readonly LightingSetup lightingSetup;
    private readonly ClusteredLightCulling clusteredLightCulling;
    private readonly VolumetricLighting volumetricLighting;
    private readonly ObjectRenderer opaqueObjectRenderer;
    private readonly ObjectRenderer motionVectorsRenderer;
    private readonly ObjectRenderer skyRenderer;
    private readonly CameraMotionVectors cameraMotionVectors;
    private readonly AmbientOcclusion ambientOcclusion;
    private readonly ObjectRenderer transparentObjectRenderer;
    private readonly DepthOfField depthOfField;
    private readonly AutoExposure autoExposure;
    private readonly TemporalAA temporalAA;
    private readonly Bloom bloom;
    private readonly Tonemapping tonemapping;

    private readonly Material skyClearMaterial;

    private readonly RenderGraph renderGraph = new();
    private readonly RTHandleSystem rtHandleSystem = new();

    public MorrowindRenderPipeline(MorrowindRenderPipelineAsset renderPipelineAsset)
    {
        this.renderPipelineAsset = renderPipelineAsset;

        GraphicsSettings.useScriptableRenderPipelineBatching = renderPipelineAsset.EnableSrpBatcher;
        GraphicsSettings.lightsUseLinearIntensity = true;
        GraphicsSettings.lightsUseColorTemperature = true;
        GraphicsSettings.disableBuiltinCustomRenderTextureUpdate = true;
        GraphicsSettings.realtimeDirectRectangularAreaLights = true;

        lightingSetup = new(renderPipelineAsset.ShadowSettings, renderGraph);
        clusteredLightCulling = new(renderPipelineAsset.ClusteredLightingSettings, renderGraph);
        volumetricLighting = new(renderPipelineAsset.VolumetricLightingSettings, renderGraph);
        opaqueObjectRenderer = new(RenderQueueRange.opaque, SortingCriteria.CommonOpaque, true, PerObjectData.None, "SRPDefaultUnlit", renderGraph);
        motionVectorsRenderer = new(RenderQueueRange.opaque, SortingCriteria.CommonOpaque, false, PerObjectData.MotionVectors, "MotionVectors", renderGraph);

        skyRenderer = new(RenderQueueRange.all, SortingCriteria.None, false, PerObjectData.None, "Sky", renderGraph);

        cameraMotionVectors = new(renderGraph);
        ambientOcclusion = new(renderPipelineAsset.AmbientOcclusionSettings, renderGraph);
        transparentObjectRenderer = new(RenderQueueRange.transparent, SortingCriteria.CommonTransparent, false, PerObjectData.None, "SRPDefaultUnlit", renderGraph);
        depthOfField = new(renderPipelineAsset.DepthOfFieldSettings, renderPipelineAsset.LensSettings, renderGraph);
        autoExposure = new AutoExposure(renderPipelineAsset.AutoExposureSettings, renderPipelineAsset.LensSettings, renderGraph);
        temporalAA = new(renderPipelineAsset.TemporalAASettings, renderGraph);
        bloom = new(renderPipelineAsset.BloomSettings, renderGraph);
        tonemapping = new(renderPipelineAsset.TonemappingSettings, renderPipelineAsset.BloomSettings, renderPipelineAsset.LensSettings, renderGraph);

        SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
        {
            defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.None,
            editableMaterialRenderQueue = false,
            enlighten = false,
            lightmapBakeTypes = LightmapBakeType.Realtime,
            lightmapsModes = LightmapsMode.NonDirectional,
            lightProbeProxyVolumes = false,
            mixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.None,
            motionVectors = true,
            overridesEnvironmentLighting = false,
            overridesFog = false,
            overrideShadowmaskMessage = null,
            overridesLODBias = false,
            overridesMaximumLODLevel = false,
            overridesOtherLightingSettings = true,
            overridesRealtimeReflectionProbes = true,
            overridesShadowmask = true,
            particleSystemInstancing = true,
            receiveShadows = true,
            reflectionProbeModes = SupportedRenderingFeatures.ReflectionProbeModes.None,
            reflectionProbes = false,
            rendererPriority = false,
            rendererProbes = false,
            rendersUIOverlay = false,
            autoAmbientProbeBaking = false,
            autoDefaultReflectionProbeBaking = false,
            enlightenLightmapper = false,
            reflectionProbesBlendDistance = false,
        };

        dynamicResolution = new(renderPipelineAsset.DynamicResolutionSettings);
        frameTimeSampler = CustomSampler.Create("Frame Time", true);

        skyClearMaterial = new Material(Shader.Find("Hidden/SkyClear")) { hideFlags = HideFlags.HideAndDontSave };
    }

    protected override void Dispose(bool disposing)
    {
        lightingSetup.Release();
        clusteredLightCulling.Release();
        volumetricLighting.Release();
        temporalAA.Release();
        dynamicResolution.Release();
    }

    protected override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        dynamicResolution.Update(frameTimeSampler.GetRecorder().gpuElapsedNanoseconds);

        var command = CommandBufferPool.Get("Render Camera");
        command.BeginSample(frameTimeSampler);

        foreach (var camera in cameras)
            RenderCamera(camera);

        command.EndSample(frameTimeSampler);
        renderGraph.Execute(command, context);
        context.ExecuteCommandBuffer(command);
        CommandBufferPool.Release(command);

        context.Submit();
    }

    private void RenderCamera(Camera camera)
    {
        camera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

        temporalAA.OnPreRender(camera, dynamicResolution.ScaleFactor, out var previousMatrix);

        if (!camera.TryGetCullingParameters(out var cullingParameters))
            return;

        var scaledWidth = (int)(camera.pixelWidth * dynamicResolution.ScaleFactor);
        var scaledHeight = (int)(camera.pixelHeight * dynamicResolution.ScaleFactor);

        rtHandleSystem.SetResolution(scaledWidth, scaledHeight);

        renderGraph.AddRenderPass((command, context) => BeginCameraRendering(context, camera));

        var cullingResults = new CullingResultsHandle();
        renderGraph.AddRenderPass((command, context) =>
        {
            cullingParameters.shadowDistance = renderPipelineAsset.ShadowSettings.ShadowDistance;
            cullingParameters.cullingOptions = CullingOptions.NeedsLighting | CullingOptions.DisablePerObjectCulling | CullingOptions.ShadowCasters;
            cullingResults.CullingResults = context.Cull(ref cullingParameters);
        });

        lightingSetup.Render(cullingResults, camera);

        renderGraph.AddRenderPass((command, context) =>
        {
            command.SetGlobalVector("_AmbientLightColor", RenderSettings.ambientLight.linear);
            command.SetGlobalVector("_FogColor", RenderSettings.fogColor.linear);
            command.SetGlobalFloat("_FogStartDistance", RenderSettings.fogStartDistance);
            command.SetGlobalFloat("_FogEndDistance", RenderSettings.fogEndDistance);
            command.SetGlobalFloat("_FogDensity", RenderSettings.fogDensity);
            command.SetGlobalFloat("_FogMode", (float)RenderSettings.fogMode);
            command.SetGlobalFloat("_FogEnabled", RenderSettings.fog ? 1.0f : 0.0f);
            command.SetGlobalFloat("_AoEnabled", renderPipelineAsset.AmbientOcclusionSettings.Strength > 0.0f ? 1.0f : 0.0f);
            command.SetGlobalFloat("_Scale", dynamicResolution.ScaleFactor);

            command.SetGlobalVector("_WaterAlbedo", renderPipelineAsset.waterAlbedo.linear);
            command.SetGlobalVector("_WaterExtinction", renderPipelineAsset.waterExtinction);

            // More camera setup
            var blueNoise1D = Resources.Load<Texture2D>(blueNoise1DIds.GetString(Time.renderedFrameCount % 64));
            var blueNoise2D = Resources.Load<Texture2D>(blueNoise2DIds.GetString(Time.renderedFrameCount % 64));
            command.SetGlobalTexture("_BlueNoise1D", blueNoise1D);
            command.SetGlobalTexture("_BlueNoise2D", blueNoise2D);
            command.SetGlobalMatrix("_NonJitteredVPMatrix", camera.nonJitteredProjectionMatrix);
            command.SetGlobalMatrix("_PreviousVPMatrix", previousMatrix);
            command.SetGlobalMatrix("_InvVPMatrix", (GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix).inverse);
            command.SetGlobalInt("_FrameCount", Time.renderedFrameCount);

            context.SetupCameraProperties(camera);
        });

        clusteredLightCulling.Render(camera, dynamicResolution.ScaleFactor);
        volumetricLighting.Render(camera, dynamicResolution.ScaleFactor);

        // old
        var cameraTargetId = Shader.PropertyToID("_CameraTarget");
        var cameraDepthId = Shader.PropertyToID("_CameraDepth");

        // new
        //var cameraTargetHandle = rtHandleSystem.GetHandle(scaledWidth, scaledHeight, GraphicsFormat.B10G11R11_UFloatPack32);
        //var cameraDepthHandle = rtHandleSystem.GetHandle(scaledWidth, scaledHeight, GraphicsFormat.D32_SFloat_S8_UInt);

        renderGraph.AddRenderPass((command, context) =>
        {
            command.GetTemporaryRT(cameraTargetId, new RenderTextureDescriptor(scaledWidth, scaledHeight, RenderTextureFormat.RGB111110Float));
            command.GetTemporaryRT(cameraDepthId, new RenderTextureDescriptor(scaledWidth, scaledHeight, RenderTextureFormat.Depth, 32));



            // Base pass
            command.SetRenderTarget(new RenderTargetBinding(cameraTargetId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, cameraDepthId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store));
            command.ClearRenderTarget(true, true, Color.clear);
        });

        opaqueObjectRenderer.Render(cullingResults, camera);

        var motionVectorsId = Shader.PropertyToID("_MotionVectors");
        renderGraph.AddRenderPass((command, context) =>
        {
            // Motion Vectors
            command.GetTemporaryRT(motionVectorsId, new RenderTextureDescriptor(scaledWidth, scaledHeight, RenderTextureFormat.RGHalf));
            command.SetRenderTarget(motionVectorsId);
            command.ClearRenderTarget(false, true, Color.clear);

            command.SetRenderTarget(
                new RenderTargetBinding(new RenderTargetIdentifier[] { cameraTargetId, motionVectorsId },
                new RenderBufferLoadAction[] { RenderBufferLoadAction.Load, RenderBufferLoadAction.DontCare },
                new RenderBufferStoreAction[] { RenderBufferStoreAction.Store, RenderBufferStoreAction.Store },
                cameraDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store));
        });

        motionVectorsRenderer.Render(cullingResults, camera);
        cameraMotionVectors.Render(motionVectorsId, cameraDepthId);
        ambientOcclusion.Render(camera, cameraDepthId, cameraTargetId, dynamicResolution.ScaleFactor);

        renderGraph.AddRenderPass((command, context) =>
        {
            // Render sky
            command.SetRenderTarget(new RenderTargetBinding(cameraTargetId, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare, cameraDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare) { flags = RenderTargetFlags.ReadOnlyDepth });

            command.DrawProcedural(Matrix4x4.identity, skyClearMaterial, 0, MeshTopology.Triangles, 3);
        });

        skyRenderer.Render(cullingResults, camera);

        renderGraph.AddRenderPass((command, context) =>
        {
            // Copy scene texture
            var sceneTextureId = Shader.PropertyToID("_SceneTexture");
            command.GetTemporaryRT(sceneTextureId, new RenderTextureDescriptor(scaledWidth, scaledHeight, RenderTextureFormat.RGB111110Float));
            command.CopyTexture(cameraTargetId, sceneTextureId);
            command.SetGlobalTexture(sceneTextureId, sceneTextureId);
            command.SetGlobalTexture(cameraDepthId, cameraDepthId);

            // Transparent
            command.SetRenderTarget(new RenderTargetBinding(cameraTargetId, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare, cameraDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare) { flags = RenderTargetFlags.ReadOnlyDepthStencil });
        });

        transparentObjectRenderer.Render(cullingResults, camera);
        autoExposure.Render(cameraTargetId, scaledWidth, scaledHeight);

        var dofResult = depthOfField.Render(scaledWidth, scaledHeight, camera.fieldOfView, cameraTargetId, cameraDepthId);

        var taa = temporalAA.Render(camera, dofResult, motionVectorsId, dynamicResolution.ScaleFactor);

        var bloomResult = bloom.Render(camera, taa);

        tonemapping.Render(taa, bloomResult, camera.cameraType == CameraType.SceneView, camera.pixelWidth, camera.pixelHeight);

        renderGraph.AddRenderPass((command, context) =>
        {
            context.ExecuteCommandBuffer(command);
            command.Clear();

            if (UnityEditor.Handles.ShouldRenderGizmos())
            {
                context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
                context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
            }
        });
    }
}