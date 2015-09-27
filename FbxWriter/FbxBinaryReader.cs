﻿using System;
using System.Text;
using System.IO;
using System.IO.Compression;

namespace Fbx
{
	public class FbxBinaryReader : FbxBinary
	{
		private readonly BinaryReader stream;
		private readonly bool strict;

		private delegate object ReadPrimitive(BinaryReader reader);

		public FbxBinaryReader(Stream stream, bool strict = true)
		{
			if(stream == null)
				throw new ArgumentNullException(nameof(stream));
			if(!stream.CanSeek)
				throw new ArgumentException(
					"The stream must support seeking. Try reading the data into a buffer first");
			this.stream = new BinaryReader(stream, Encoding.ASCII);
			this.strict = strict;
		}

		object ReadProperty()
		{
			var dataType = (char) stream.ReadByte();
            switch (dataType)
			{
				case 'Y':
					return stream.ReadInt16();
				case 'C':
					return stream.ReadBoolean();
				case 'I':
					return stream.ReadInt32();
				case 'F':
					return stream.ReadSingle();
				case 'D':
					return stream.ReadDouble();
				case 'L':
					return stream.ReadInt64();
				case 'f':
					return ReadArray(br => br.ReadSingle(), typeof(float));
				case 'd':
					return ReadArray(br => br.ReadDouble(), typeof(double));
				case 'l':
					return ReadArray(br => br.ReadInt64(), typeof(long));
				case 'i':
					return ReadArray(br => br.ReadInt32(), typeof(int));
				case 'b':
					return ReadArray(br => br.ReadBoolean(), typeof(bool));
				case 'S':
					var len = stream.ReadInt32();
                    return len == 0 ? "" : Encoding.ASCII.GetString(stream.ReadBytes(len));
				case 'R':
					return stream.ReadBytes(stream.ReadInt32());
				default:
					throw new FbxException(stream.BaseStream.Position - 1,
						"Invalid property data type `" + dataType + "'");
			}
		}

		Array ReadArray(ReadPrimitive readPrimitive, Type arrayType)
		{
			var len = stream.ReadInt32();
			var encoding = stream.ReadInt32();
			var compressedLen = stream.ReadInt32();
			var ret = Array.CreateInstance(arrayType, len);
			var s = stream;
			var endPos = stream.BaseStream.Position + compressedLen;
			if (encoding != 0)
			{
				if(strict)
				{
					if(encoding != 1)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression encoding (must be 0 or 1)");
					var cmf = stream.ReadByte();
					if((cmf & 0xF) != 8 || (cmf >> 4) > 7)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression format " + cmf);
					var flg = stream.ReadByte();
					if(((cmf << 8) + flg) % 31 != 0)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression FCHECK");
					if((flg & (1 << 5)) != 0)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression flags; dictionary not supported");
                } else
				{
					stream.BaseStream.Position += 2;
				}
				var codec = new DeflateWithChecksum(stream.BaseStream, CompressionMode.Decompress);
				s = new BinaryReader(codec);
			}
			try
			{
				for (int i = 0; i < len; i++)
					ret.SetValue(readPrimitive(s), i);
			}
			catch (InvalidDataException)
			{
				throw new FbxException(stream.BaseStream.Position - 1,
                    "Compressed data was malformed");
			}
			if (encoding != 0)
			{
				if (strict)
				{
					stream.BaseStream.Position = endPos - sizeof(int);
					var checksumBytes = new byte[sizeof(int)];
					stream.BaseStream.Read(checksumBytes, 0, checksumBytes.Length);
					int checksum = 0;
					for (int i = 0; i < checksumBytes.Length; i++)
						checksum = (checksum << 8) + checksumBytes[i];
					if(checksum != ((DeflateWithChecksum)s.BaseStream).Checksum)
						throw new FbxException(stream.BaseStream.Position,
							"Compressed data has invalid checksum");
				}
				else
				{
					stream.BaseStream.Position = endPos;
				}
			}
			return ret;
		}

		FbxNode ReadNode()
		{
			var endOffset = stream.ReadInt32();
			var numProperties = stream.ReadInt32();
			var propertyListLen = stream.ReadInt32();
			var nameLen = stream.ReadByte();
            var name = nameLen == 0 ? "" : Encoding.ASCII.GetString(stream.ReadBytes(nameLen));

			if (endOffset == 0)
			{
				// The end offset should only be 0 in a null node
				if(strict && (numProperties != 0 || propertyListLen != 0 || !string.IsNullOrEmpty(name)))
					throw new FbxException(stream.BaseStream.Position,
						"Invalid node; expected NULL record");
				return null;
			}

			var node = new FbxNode {Name = name};

			var propertyEnd = stream.BaseStream.Position + propertyListLen;
			// Read properties
			for (int i = 0; i < numProperties; i++)
				node.Properties.Add(ReadProperty());

			if(strict && stream.BaseStream.Position != propertyEnd)
				throw new FbxException(stream.BaseStream.Position,
					"Too many bytes in property list, end point is " + propertyEnd);

			// Read nested nodes
			var listLen = endOffset - stream.BaseStream.Position;
			if(name == "References")
				Console.WriteLine(listLen);
			if(listLen < 0)
				throw new Exception();
			if (listLen > 0)
			{
				FbxNode nested;
				do
				{
					nested = ReadNode();
					if(nested != null)
						node.Nodes.Add(nested);
				} while (nested != null);
				if (strict && stream.BaseStream.Position != endOffset)
					throw new FbxException(stream.BaseStream.Position,
						"Too many bytes in node, end point is " + endOffset);
			}
			return node;
		}

		static bool CheckEqual(byte[] a, byte[] b)
		{
			if (a.Length != b.Length)
				return false;
			for(int i = 0; i < a.Length; i++)
				if (a[i] != b[i])
					return false;
			return true;
		}

		public FbxNode Read(out int version)
		{
			if (strict)
			{
                if (!CheckEqual(stream.ReadBytes(headerString.Length), headerString))
					throw new FbxException(stream.BaseStream.Position - headerString.Length,
						"Invalid header string");
				if(stream.ReadInt16() != magic)
					throw new FbxException(stream.BaseStream.Position - sizeof(short),
						"Invalid magic number");
			} else
			{
				stream.BaseStream.Position += headerString.Length + sizeof(short);
			}
			version = stream.ReadInt32();
			var node = new FbxNode();
			FbxNode nested;
			do
			{
				nested = ReadNode();
				if(nested != null)
					node.Nodes.Add(nested);
			} while (nested != null);
			// Account for existence top level node or not
			if (node.Nodes.Count == 1 && node.Properties.Count == 0 && string.IsNullOrEmpty(node.Name))
				node = node.Nodes[0];
			return node;
		}
	}
}
