using System;

// The wrap picture work exactly as it was before the cache-friendly column passes (WrapPackage.cs of 19 October, byte for byte): the
// reference the faster code is compared with.
static class WrapReference
{
    const byte AreaNone = 0;

        internal static int OutlineAreas(byte[] rgba, int width, int height, byte[] area, float radius)
        {
            if (radius <= 0.25f || rgba == null || area == null || rgba.Length != width * height * 4 || area.Length != width * height) return 0;
            const float Far = 1e12f;
            int n = width * height;
            bool anyArea = false;
            for (int i = 0; i < n && !anyArea; i++) anyArea = area[i] != AreaNone;
            if (!anyArea) return 0;
            // squared distance to the nearest painted pixel (Felzenszwalb-Huttenlocher, exact), columns then rows
            var d2 = new float[n];
            int longest = Math.Max(width, height);
            var f = new float[longest];
            var g = new float[longest];
            var v = new int[longest];
            var z = new float[longest + 1];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) f[y] = rgba[(y * width + x) * 4 + 3] >= 128 ? 0f : Far;
                DistanceLine(f, height, v, z, g);
                for (int y = 0; y < height; y++) d2[y * width + x] = g[y];
            }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) f[x] = d2[y * width + x];
                DistanceLine(f, width, v, z, g);
                for (int x = 0; x < width; x++) d2[y * width + x] = g[x];
            }
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                if (area[i] == AreaNone || rgba[i * 4 + 3] >= 128) continue;
                // a painted pixel's centre is 0 away, the painted region's edge half a pixel beyond it: the outline reaches radius pixels past the edge
                float cover = Math.Min(1f, Math.Max(0f, radius + 1f - (float)Math.Sqrt(d2[i])));
                if (cover <= 0f) continue;
                float own = rgba[i * 4 + 3] / 255f;              // the picture's own soft edge lies over the white
                float outA = own + cover * (1f - own);
                for (int c = 0; c < 3; c++)
                    rgba[i * 4 + c] = (byte)Math.Round((rgba[i * 4 + c] * own + 255f * cover * (1f - own)) / outA);
                rgba[i * 4 + 3] = (byte)Math.Round(outA * 255f);
                changed++;
            }
            return changed;
        }

        // The squared distance transform of one line (f: 0 on the painted pixels, a large value elsewhere); v and z are scratch.
        private static void DistanceLine(float[] f, int n, int[] v, float[] z, float[] outD)
        {
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                outD[q] = dq * dq + f[v[k]];
            }
        }

        internal static int Decontaminate(byte[] rgba, int width, int height, int sourceAlpha = 192, int clearAlpha = 64)
        {
            if (rgba == null || rgba.Length != width * height * 4 || width <= 0 || height <= 0) return 0;
            const float Far = 1e12f;
            int n = width * height, longest = Math.Max(width, height);
            bool anySource = false, anyClear = false;
            for (int i = 0; i < n; i++)
            {
                int a = rgba[i * 4 + 3];
                anySource |= a >= sourceAlpha;
                anyClear |= a < clearAlpha;
            }
            if (!anySource || !anyClear) return 0;
            var f = new float[longest];
            var outD = new float[longest];
            var outI = new int[longest];
            var v = new int[longest];
            var z = new float[longest + 1];
            var vertical = new float[n];         // squared distance to the nearest source in the pixel's column
            var sourceRow = new int[n];          // that source's row
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) f[y] = rgba[(y * width + x) * 4 + 3] >= sourceAlpha ? 0f : Far;
                DistanceLineNearest(f, height, v, z, outD, outI);
                for (int y = 0; y < height; y++)
                {
                    vertical[y * width + x] = outD[y];
                    sourceRow[y * width + x] = outI[y];
                }
            }
            int changed = 0;
            var pick = new int[n];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) f[x] = vertical[y * width + x];
                DistanceLineNearest(f, width, v, z, outD, outI);
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    pick[i] = -1;
                    if (rgba[i * 4 + 3] >= clearAlpha || outD[x] >= Far * 0.5f) continue;
                    int sx = outI[x];
                    pick[i] = sourceRow[y * width + sx] * width + sx;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (pick[i] < 0) continue;
                int s = pick[i] * 4;
                if (rgba[i * 4] != rgba[s] || rgba[i * 4 + 1] != rgba[s + 1] || rgba[i * 4 + 2] != rgba[s + 2]) changed++;
                rgba[i * 4] = rgba[s]; rgba[i * 4 + 1] = rgba[s + 1]; rgba[i * 4 + 2] = rgba[s + 2];
            }
            return changed;
        }

        // DistanceLine that also reports, for every position, which input position is the nearest one (the argmin of the parabolas).
        private static void DistanceLineNearest(float[] f, int n, int[] v, float[] z, float[] outD, int[] outIndex)
        {
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                outD[q] = dq * dq + f[v[k]];
                outIndex[q] = v[k];
            }
        }
}
