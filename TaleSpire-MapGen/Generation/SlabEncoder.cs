using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace TaleSpireMapGen.Generation
{
    public static class SlabEncoder
    {
        // TaleSpire slab v2 binary format
        // Header: magic(4) + version(2) + layoutCount(2) + creatureCount(2) = 10 bytes
        // Layout: guid(16) + assetCount(2) + padding(2) = 20 bytes
        // Asset: packed ulong(8) — bits [0..17]=scaledX, [18..35]=scaledY, [36..53]=scaledZ, [54..58]=rotStep
        //   where scaledValue = (int)(worldPos * 100)

        private const uint Magic = 0xD1CEFACE;
        private const ushort Version = 0x0002;

        // tilePlacements: (16-byte guid, world x, world y, world z, rotStep 0-23)
        public static string Encode(List<(byte[] guid, float x, float y, float z, int rotStep)> tilePlacements)
        {
            // Group placements by GUID to build layout blocks
            var layouts = new Dictionary<string, (byte[] guidBytes, List<(float x, float y, float z, int rot)> assets)>(
                StringComparer.Ordinal);

            foreach (var (guid, x, y, z, rot) in tilePlacements)
            {
                string key = BitConverter.ToString(guid);
                if (!layouts.TryGetValue(key, out var entry))
                {
                    entry = (guid, new List<(float, float, float, int)>());
                    layouts[key] = entry;
                }
                entry.assets.Add((x, y, z, rot));
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Header
            bw.Write(Magic);                         // 4 bytes
            bw.Write(Version);                       // 2 bytes
            bw.Write((ushort)layouts.Count);         // 2 bytes layout count
            bw.Write((ushort)0);                     // 2 bytes creature count

            // All layout headers first (guid + assetCount + padding per layout)
            foreach (var kv in layouts)
            {
                var (guidBytes, assets) = kv.Value;
                bw.Write(guidBytes);                  // 16 bytes GUID
                bw.Write((ushort)assets.Count);       // 2 bytes asset count
                bw.Write((ushort)0);                  // 2 bytes padding
            }

            // Then all asset data, in the same layout order
            foreach (var kv in layouts)
            {
                foreach (var (ax, ay, az, arot) in kv.Value.assets)
                    bw.Write(PackAsset(ax, ay, az, arot));
            }

            bw.Flush();
            byte[] raw = ms.ToArray();
            byte[] compressed = GzipCompress(raw);
            return Convert.ToBase64String(compressed);
        }

        public static void CopyToClipboard(string slabString)
        {
            GUIUtility.systemCopyBuffer = slabString;
        }

        private static ulong PackAsset(float x, float y, float z, int rotStep)
        {
            // Positions are stored as integer * 100
            ulong sx = (ulong)(uint)(int)(x * 100f) & 0x3FFFF;  // 18 bits
            ulong sy = (ulong)(uint)(int)(y * 100f) & 0x3FFFF;  // 18 bits
            ulong sz = (ulong)(uint)(int)(z * 100f) & 0x3FFFF;  // 18 bits
            ulong rot = (ulong)(rotStep & 0x1F);                 // 5 bits

            return sx | (sy << 18) | (sz << 36) | (rot << 54);
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Optimal))
                gz.Write(data, 0, data.Length);
            return output.ToArray();
        }
    }
}
