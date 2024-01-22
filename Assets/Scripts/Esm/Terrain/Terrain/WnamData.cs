﻿using UnityEngine;

// Color? 9x9 grid, color palette? 
public class WnamData
{
	private readonly byte[] data;

	public WnamData(System.IO.BinaryReader reader, int size)
	{
		data = reader.ReadBytes(size);
	}
}