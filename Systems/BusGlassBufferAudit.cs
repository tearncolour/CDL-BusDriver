using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>Read-only comparison of imported packed streams and each index-only glass split.</summary>
    internal static class BusGlassBufferAudit
    {
        internal static object Compare(Mesh source, Mesh split, string vehicle, int lod, int surface, int part)
        {
            var original = source.GetVertexAttributes();
            var retained = split.GetVertexAttributes();
            if (source.vertexCount != split.vertexCount || !original.SequenceEqual(retained))
                throw new InvalidOperationException("Glass packed layout differs: " + part);
            var streams = new List<object>();
            using (var a = Mesh.AcquireReadOnlyMeshData(source))
            using (var b = Mesh.AcquireReadOnlyMeshData(split))
            {
                if (a[0].vertexBufferCount != b[0].vertexBufferCount)
                    throw new InvalidOperationException("Glass stream count differs: " + part);
                for (int stream = 0; stream < a[0].vertexBufferCount; stream++)
                {
                    byte[] before = a[0].GetVertexData<byte>(stream).ToArray();
                    byte[] after = b[0].GetVertexData<byte>(stream).ToArray();
                    int stride = a[0].GetVertexBufferStride(stream);
                    if (stride != b[0].GetVertexBufferStride(stream) || !before.SequenceEqual(after))
                        throw new InvalidOperationException("Glass packed vertex bytes differ: part=" + part + " stream=" + stream);
                    streams.Add(new { stream, stride, bytes = before.Length, source_sha256 = Hash(before), split_sha256 = Hash(after), identical = true });
                }
            }
            return new { vehicle, lod, surface, part, source = source.name, split = split.name, vertices = source.vertexCount,
                attributes = original.Select(attribute => new { name = attribute.attribute.ToString(), format = attribute.format.ToString(),
                    attribute.dimension, attribute.stream, offset = source.GetVertexAttributeOffset(attribute.attribute) }).ToArray(), streams };
        }
        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
