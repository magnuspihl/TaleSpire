using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MapGenQA
{
    /// Inverse of SlabEncoder, so a generated slab can be decoded and compared against the
    /// placements it came from. Mirrors the v2 format documented in SlabEncoder.cs.
    public static class SlabDecode
    {
        public static List<(string guid, float x, float y, float z, int rot)> Decode(string base64)
        {
            byte[] raw = Gunzip(Convert.FromBase64String(base64));
            using var ms = new MemoryStream(raw);
            using var br = new BinaryReader(ms);

            uint magic = br.ReadUInt32();
            if (magic != 0xD1CEFACE) throw new InvalidDataException($"bad magic 0x{magic:X8}");
            br.ReadUInt16();                        // version
            int layoutCount = br.ReadUInt16();
            br.ReadUInt16();                        // creature count

            var headers = new List<(string guid, int count)>();
            for (int i = 0; i < layoutCount; i++)
            {
                var guid = new Guid(br.ReadBytes(16)).ToString();
                int count = br.ReadUInt16();
                br.ReadUInt16();                    // padding
                headers.Add((guid, count));
            }

            var result = new List<(string, float, float, float, int)>();
            foreach (var (guid, count) in headers)
            {
                for (int i = 0; i < count; i++)
                {
                    ulong packed = br.ReadUInt64();
                    result.Add((guid,
                        Unpack(packed, 0), Unpack(packed, 18), Unpack(packed, 36),
                        (int)((packed >> 54) & 0x1F)));
                }
            }
            return result;
        }

        // 18-bit fields hold value*100; they are written from a signed int, so the top bit is a sign.
        private static float Unpack(ulong packed, int shift)
        {
            int v = (int)((packed >> shift) & 0x3FFFF);
            if ((v & 0x20000) != 0) v -= 0x40000;
            return v / 100f;
        }

        private static byte[] Gunzip(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }
    }
}
