using ProcGenSys.WFC.Bundle;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using ProcGenSys.Common.Tile;
using ProcGenSys.Common.Enum;
using System.Text;

namespace ProcGenSys.WFC.Learner
{
    public class Accumulator
    {
        private readonly Dictionary<string, int> tileIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> patterns2x2 = new();
        private readonly List<string> tiles = new List<string>();

        private readonly Dictionary<Direction, Dictionary<(int A, int B), int>> adj = new Dictionary<Direction, Dictionary<(int A, int B), int>>()
        {
            {Direction.North,new()},
            {Direction.East,new()},
            {Direction.South,new()},
            {Direction.West,new()},
            {Direction.NorthEast,new()},
            {Direction.NorthWest, new()},
            {Direction.SouthEast, new()},
            {Direction.SouthWest,new()},
        };

        private class P
        {
            public int cellCount, edgeHCount, edgeVCount, cornerCount, surfaceCount;
            public readonly Dictionary<string, int> cellOnTile = new Dictionary<string, int>();
            public readonly Dictionary<string, int> edgeH = new Dictionary<string, int>();
            public readonly Dictionary<string, int> edgeV = new Dictionary<string, int>();

            public Vector2 cellUVSum;
            public int cellUVN;
            public float edgeHTSum;
            public int edgeHTN;
            public float edgeVTSum;
            public int edgeVTN;

            public int rotBins = 0;
            public int[] rotHist = Array.Empty<int>();

            public bool? uniformScale;
            public Vector3 scaleSum, scaleSqSum; public int scaleN;

            public readonly Dictionary<(AnchorType, string), int> footprints = new Dictionary<(AnchorType, string), int>();

            public readonly List<float> spacing = new List<float>();
        }

        private readonly Dictionary<string, P> prefabs = new Dictionary<string, P>();

        public void TouchTile(string name)
        {
            if (!tileIndex.ContainsKey(name))
            {
                tileIndex[name] = tiles.Count;
                tiles.Add(name);
            }
        }

        /*
         * for a given cell, store the cell and adjacent cells including diagonals
         * remember the relationships between the cells also
         */
        public void CountAdj(TileTypeSO[,] tiles, int row, int col)
        {
            var tileA = tiles[row, col];

            TouchTile(tileA.Key);

            foreach (var (dir, tileB) in tiles.GetAdjacentNeighborsWithDiagonals(row, col))
            {
                if (tileB == null) continue;

                TouchTile(tileB.Key);

                int A = tileIndex[tileA.Key];
                int B = tileIndex[tileB.Key];

                var map = adj[dir];
                map[(A, B)] = map.TryGetValue((A, B), out var v) ? v + 1 : 1;
            }
        }

        /*
         * Relative to cell, store the 2x2
         */
        public void Count2x2(TileTypeSO[,] tiles, int row, int col)
        {

            var keyBuilder = new StringBuilder();

            foreach (var (dir, tileB) in tiles.Get2x2(row, col))
            {
                keyBuilder.Append(tileB != null ? tileB.Key : "None").Append("_");
            }

            var key = keyBuilder.ToString();

            patterns2x2[key] = patterns2x2.TryGetValue(key, out var v) ? v + 1 : 1;
        }


        private P GetP(string pid) => prefabs.TryGetValue(pid, out var p) ? p : (prefabs[pid] = new P());

        public void CountPrefabCell(string pid, string centerTile)
        {
            var p = GetP(pid); p.cellCount++;
            p.cellOnTile[centerTile] = p.cellOnTile.TryGetValue(centerTile, out var v) ? v + 1 : 1;
        }

        public void CountPrefabEdge(string pid, bool horizontal, string a, string b)
        {
            var p = GetP(pid);
            var key = $"{a}|{b}";
            if (horizontal) { p.edgeHCount++; p.edgeH[key] = p.edgeH.TryGetValue(key, out var v) ? v + 1 : 1; }
            else { p.edgeVCount++; p.edgeV[key] = p.edgeV.TryGetValue(key, out var v) ? v + 1 : 1; }
        }

        public void CountPrefabCorner(string pid) { GetP(pid).cornerCount++; }

        public void CountPrefabSurface(string pid) { GetP(pid).surfaceCount++; }

        public void AddCellOffset(string pid, Vector2 uv) { var p = GetP(pid); p.cellUVSum += uv; p.cellUVN++; }

        public void AddEdgeT(string pid, bool horizontal, float t)
        {
            var p = GetP(pid);
            if (horizontal) { p.edgeHTSum += t; p.edgeHTN++; } else { p.edgeVTSum += t; p.edgeVTN++; }
        }

        public void AddRotationSample(string pid, int bins, int bin)
        {
            var p = GetP(pid);
            if (p.rotBins != bins || p.rotHist.Length != bins)
            {
                int newBins = Math.Max(p.rotBins, bins);
                var newHist = new int[newBins];
                if (p.rotHist.Length > 0)
                {
                    for (int i = 0; i < p.rotHist.Length; i++)
                    {
                        int mapped = Mathf.FloorToInt(i * (newBins / (float)p.rotHist.Length));
                        mapped = Mathf.Clamp(mapped, 0, newBins - 1);
                        newHist[mapped] += p.rotHist[i];
                    }
                }
                p.rotBins = newBins;
                p.rotHist = newHist;
            }
            bin = Mathf.Clamp(bin, 0, p.rotBins - 1);
            p.rotHist[bin]++;
        }

        public void AddScaleSample(string pid, bool uniform, Vector3 s)
        {
            var p = GetP(pid);
            p.uniformScale ??= uniform;
            if (p.uniformScale.Value != uniform) p.uniformScale = false;

            if (p.uniformScale.Value)
            {
                float u = (s.x + s.y + s.z) / 3f;
                p.scaleSum.x += u; p.scaleSqSum.x += u * u; p.scaleN++;
            }
            else
            {
                p.scaleSum += s;
                p.scaleSqSum += new Vector3(s.x * s.x, s.y * s.y, s.z * s.z);
                p.scaleN++;
            }
        }

        public void AddFootprintSample(string pid, AnchorType anchor, HashSet<Vector2Int> relCells)
        {
            var p = GetP(pid);

            var sorted = relCells.ToList();
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            string key = string.Join(";", sorted.Select(v => $"{v.x},{v.y}"));
            var k = (anchor, key);
            p.footprints[k] = p.footprints.TryGetValue(k, out var v) ? v + 1 : 1;
        }

        public void AddSpacingSample(string pid, float distCells) => GetP(pid).spacing.Add(distCells);

        public void FillAsset(WFCModelBundle asset)
        {
            asset.TileIds = tiles.ToArray();

            asset.Adjacency = new WFCModelBundle.AdjacencyPerDir[8];
            int N = tiles.Count;
            foreach (var kv in adj)
            {
                var dir = kv.Key;
                var weights = new float[N * N];
                var totals = new int[N];
                foreach (var e in kv.Value) totals[e.Key.A] += e.Value;

                for (int A = 0; A < N; A++)
                {
                    float denom = totals[A] + 0.5f * N;
                    for (int B = 0; B < N; B++)
                    {
                        kv.Value.TryGetValue((A, B), out int cnt);
                        float num = cnt + 0.5f;
                        weights[A * N + B] = denom > 0 ? num / denom : 1f / N;
                    }
                }
                asset.Adjacency[(int)dir] = new WFCModelBundle.AdjacencyPerDir
                {
                    Dir = dir,
                    TileCount = N,
                    Weights = weights
                };
            }

            asset.Prefabs = prefabs.Select(kv =>
            {
                var id = kv.Key; var p = kv.Value;

                Vector3 mean = Vector3.one, std = Vector3.zero;
                if (p.scaleN > 0)
                {
                    mean = p.scaleSum / p.scaleN;
                    var varv = (p.scaleSqSum / p.scaleN) - new Vector3(mean.x * mean.x, mean.y * mean.y, mean.z * mean.z);
                    std = new Vector3(
                        Mathf.Sqrt(Mathf.Max(0f, varv.x)),
                        Mathf.Sqrt(Mathf.Max(0f, varv.y)),
                        Mathf.Sqrt(Mathf.Max(0f, varv.z))
                    );
                }

                var fps = p.footprints.Select(e => new WFCModelBundle.FootprintVariant
                {
                    Anchor = e.Key.Item1,
                    Cells = ParseCells(e.Key.Item2),
                    Count = e.Value
                }).ToArray();

                float p10 = 0f, med = 0f;
                if (p.spacing.Count > 0)
                {
                    var arr = p.spacing.OrderBy(x => x).ToArray();
                    med = Percentile(arr, 50);
                    p10 = Percentile(arr, 10);
                }

                return new WFCModelBundle.PrefabEntry
                {
                    PrefabId = id,
                    CellCount = p.cellCount,
                    EdgeHCount = p.edgeHCount,
                    EdgeVCount = p.edgeVCount,
                    CornerCount = p.cornerCount,
                    SurfaceCount = p.surfaceCount,
                    CellOnTile = p.cellOnTile.Select(x => new WFCModelBundle.KeyWeight { Key = x.Key, Count = x.Value }).ToArray(),
                    EdgeH_AB = p.edgeH.Select(x => new WFCModelBundle.PairWeight { Pair = x.Key, Count = x.Value }).ToArray(),
                    EdgeV_AB = p.edgeV.Select(x => new WFCModelBundle.PairWeight { Pair = x.Key, Count = x.Value }).ToArray(),
                    AvgCellUV = p.cellUVN > 0 ? (p.cellUVSum / p.cellUVN) : Vector2.one * 0.5f,
                    AvgEdgeH_T = p.edgeHTN > 0 ? p.edgeHTSum / p.edgeHTN : 0.5f,
                    AvgEdgeV_T = p.edgeVTN > 0 ? p.edgeVTSum / p.edgeVTN : 0.5f,
                    RotationBins = Math.Max(0, p.rotBins),
                    RotationHist = p.rotHist ?? Array.Empty<int>(),
                    UniformScale = p.uniformScale ?? true,
                    ScaleMean = mean,
                    ScaleStdDev = std,
                    Footprints = fps,
                    SpacingMedian = med,
                    SpacingP10 = p10
                };
            }).ToArray();
        }

        private static Vector2Int[] ParseCells(string key)
        {
            if (string.IsNullOrEmpty(key)) return new[] { Vector2Int.zero };
            var parts = key.Split(';');
            var res = new Vector2Int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var xy = parts[i].Split(',');
                int x = int.Parse(xy[0]); int y = int.Parse(xy[1]);
                res[i] = new Vector2Int(x, y);
            }
            return res;
        }

        private static float Percentile(float[] sorted, int p)
        {
            if (sorted.Length == 0) return 0f;
            float idx = (p / 100f) * (sorted.Length - 1);
            int i0 = Mathf.FloorToInt(idx);
            int i1 = Mathf.Min(i0 + 1, sorted.Length - 1);
            float t = idx - i0;
            return Mathf.Lerp(sorted[i0], sorted[i1], t);
        }
    }
}