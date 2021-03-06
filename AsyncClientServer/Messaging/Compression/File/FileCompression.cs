﻿using System.IO;

namespace AsyncClientServer.Messaging.Compression.File
{
	public abstract class FileCompression
	{

		public string Extension => ".FilePART";

		/// <summary>
		/// Compress a file and return the FileInfo of the new file.
		/// <para>Has to return the FileInfo of the compressed file in order to work.</para>
		/// </summary>
		/// <param name="fileToCompress"></param>
		/// <param name="tempPath"></param>
		/// <returns></returns>
		public abstract FileInfo Compress(FileInfo fileToCompress, string tempPath);


		/// <summary>
		/// Decompresses a file
		/// <para>Will have to return the FileInfo object of the decompressed file in order to work.</para>
		/// </summary>
		/// <param name="fileToDecompress"></param>
		public abstract FileInfo Decompress(FileInfo fileToDecompress);


	}
}
