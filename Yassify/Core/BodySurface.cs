// Yassify engine (shared with Tailor) - the body the gear is fitted to: its triangles and bone weights,
// with a grid for "nearest spot on the skin" lookups.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Yassify.Core;

public sealed class BodySurface
{
    public readonly List<Vector3> P = new();
    public readonly List<Dictionary<string, float>> W = new();
    public readonly List<Vector3> N = new();               // outward normals per point (from the model; worked out if missing)
    public readonly List<(int a, int b, int c)> Tris = new();
    public readonly List<string> Sources = new();
    public readonly List<int> TriSrc = new();               // per triangle: its model (index into Sources)
    /// <summary>The body piece each model is ("Chest", "Legs", "Hands", "Feet"), when known.</summary>
    public readonly Dictionary<int, string> SourceSlot = new();

    /// <summary>The body slot a body model's file name says (c0201e0000_top.mdl -> Chest), "" if it doesn't.</summary>
    public static string SlotOfName(string path)
    {
        var f = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();
        return f.EndsWith("_top") ? "Chest" : f.EndsWith("_dwn") ? "Legs" : f.EndsWith("_glv") ? "Hands" : f.EndsWith("_sho") ? "Feet" : "";
    }
    HashSet<string>? _boneNames;
    /// <summary>Any point on a bone whose name starts with this (e.g. "iv_kyokin": pecs).</summary>
    public bool HasBone(string prefix)
    {
        _boneNames ??= W.SelectMany(d => d.Keys).ToHashSet(StringComparer.Ordinal);
        return _boneNames.Any(n => n.StartsWith(prefix, StringComparison.Ordinal));
    }

    float _cell = 0.02f;
    Vector3 _min, _max;
    int _nx, _ny, _nz;
    Dictionary<long, List<int>> _grid = new();

    /// <summary>Whether a body mesh counts as skin by default: everything but
    /// small extras (nails, pubes, jewellery...). A big mesh always counts -
    /// some bodies give the skin an odd material name (Rue+ calls it piercings).</summary>
    public static bool IsSkinByDefault(string mat, int vertexCount)
    {
        if (vertexCount >= 5000) return true;
        var m = mat.ToLowerInvariant();
        foreach (var bad in new[] { "nail", "pube", "bpf", "piercing", "undies", "hair", "penis", "dermal", "tongue", "eye" })
            if (m.Contains(bad)) return false;
        return true;
    }

    static readonly System.Text.RegularExpressions.Regex SkinMat = new(@"/mt_c\d{4}b\d{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>A gear piece's skin (0.1.15): the game draws a material named /mt_cXXXXbYYYY... with your skin - minus the
    /// body's extras that use the same naming (Neolithe's piercings, nails, pubes...).</summary>
    public static bool IsSkinMaterial(string mat)
    {
        if (!SkinMat.IsMatch(mat)) return false;
        var m = mat.ToLowerInvariant();
        foreach (var bad in new[] { "nail", "pube", "bpf", "piercing", "undies", "hair", "penis", "dermal", "tongue", "eye" })
            if (m.Contains(bad)) return false;
        return true;
    }

    /// <summary>Adds a body model's LOD 0 meshes (use(mesh number, material,
    /// point count) picks which; default: the skin).</summary>
    public int Add(MdlFile m, string label, Func<int, string, int, bool>? use = null)
    {
        int added = 0, slot = -1, src = Sources.Count;
        foreach (int mi in m.LodMeshes(0))
        {
            slot++;
            var me = m.Meshes[mi];
            string mat = me.MaterialIndex < m.MaterialNames.Count ? m.MaterialNames[me.MaterialIndex] : "";
            if (!(use ?? ((_, mt, n) => IsSkinByDefault(mt, n)))(slot, mat, me.VertexCount)) continue;
            var pe = m.Element(mi, VUsage.Position);
            if (pe == null) continue;
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : new List<ushort>();
            var ne = m.Element(mi, VUsage.Normal);
            int baseV = P.Count;
            bool normalsOk = N.Count == P.Count;
            for (int v = 0; v < me.VertexCount; v++)
            {
                var p = m.ReadVec(mi, pe, v);
                P.Add(new Vector3(p.X, p.Y, p.Z));
                if (normalsOk)
                {
                    if (ne != null)
                    {
                        var n4 = m.ReadVec(mi, ne, v);
                        var n = ne.Type == 8 ? new Vector3(n4.X, n4.Y, n4.Z) * 2 - Vector3.One : new Vector3(n4.X, n4.Y, n4.Z);
                        N.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY);
                    }
                    else normalsOk = false;
                }
                var d = new Dictionary<string, float>();
                foreach (var (sl, w) in m.ReadWeights(mi, v))
                {
                    if (sl >= table.Count) continue;
                    string bn = m.BoneNames[table[sl]];
                    d[bn] = d.GetValueOrDefault(bn) + w / 255f;
                }
                W.Add(d);
            }
            var idx = m.ReadIndices(mi);
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                if (a == b || b == c || a == c) continue;
                Tris.Add((baseV + a, baseV + b, baseV + c));
                TriSrc.Add(src);
            }
            added++;
        }
        Sources.Add(label);
        return added;
    }

    public bool Empty => Tris.Count == 0;

    public void Build()
    {
        if (Tris.Count == 0) return;
        if (N.Count != P.Count) MakeNormals();
        _min = new Vector3(float.MaxValue); _max = new Vector3(float.MinValue);
        foreach (var p in P) { _min = Vector3.Min(_min, p); _max = Vector3.Max(_max, p); }
        _min -= new Vector3(0.001f); _max += new Vector3(0.001f);
        // cells about three triangles across: few triangles per cell to test
        double edge = 0; int ne = 0;
        for (int t = 0; t < Tris.Count; t += Math.Max(1, Tris.Count / 4000))
        {
            var (a, b, c) = Tris[t];
            edge += (P[a] - P[b]).Length() + (P[b] - P[c]).Length() + (P[c] - P[a]).Length(); ne += 3;
        }
        _cell = Math.Clamp((float)(edge / Math.Max(1, ne)) * 3f, 0.004f, 0.03f);
        var size = _max - _min;
        _nx = Math.Max(1, (int)(size.X / _cell) + 1);
        _ny = Math.Max(1, (int)(size.Y / _cell) + 1);
        _nz = Math.Max(1, (int)(size.Z / _cell) + 1);
        _grid.Clear();
        for (int t = 0; t < Tris.Count; t++)
        {
            var (a, b, c) = Tris[t];
            var lo = Vector3.Min(P[a], Vector3.Min(P[b], P[c]));
            var hi = Vector3.Max(P[a], Vector3.Max(P[b], P[c]));
            var (x0, y0, z0) = CellOf(lo);
            var (x1, y1, z1) = CellOf(hi);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = Key(x, y, z);
                        if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
                        l.Add(t);
                    }
        }
    }

    (int, int, int) CellOf(Vector3 p)
    {
        var q = (p - _min) / _cell;
        return (Math.Clamp((int)q.X, 0, _nx - 1), Math.Clamp((int)q.Y, 0, _ny - 1), Math.Clamp((int)q.Z, 0, _nz - 1));
    }

    long Key(int x, int y, int z) => ((long)x * _ny + y) * _nz + z;

    /// <summary>Normals from the triangles (area-weighted), turned to point away from the middle
    /// if most of them point inward - for surfaces built without the model's own normals.</summary>
    void MakeNormals()
    {
        N.Clear();
        var acc = new Vector3[P.Count];
        foreach (var (a, b, c) in Tris)
        {
            var n = Vector3.Cross(P[b] - P[a], P[c] - P[a]);
            acc[a] += n; acc[b] += n; acc[c] += n;
        }
        var mid = Vector3.Zero;
        foreach (var p in P) mid += p;
        mid /= Math.Max(1, P.Count);
        int outward = 0;
        for (int i = 0; i < acc.Length; i++)
        {
            acc[i] = acc[i].LengthSquared() > 1e-20f ? Vector3.Normalize(acc[i]) : Vector3.UnitY;
            if (Vector3.Dot(acc[i], P[i] - mid) > 0) outward++;
        }
        float flip = outward * 2 >= acc.Length ? 1 : -1;
        foreach (var n in acc) N.Add(n * flip);
    }

    /// <summary>Nearest spot with its outward normal (blended across the triangle).</summary>
    public (float dist, Dictionary<string, float> w, Vector3 at, Vector3 normal) NearestDetail(Vector3 q, float maxDist = 0.5f)
    {
        var (d, w, at, tri, bar) = Find(q, maxDist);
        if (tri < 0 || N.Count != P.Count) return (d, w, at, Vector3.UnitY);
        var (a, b, c) = Tris[tri];
        var n = N[a] * bar.X + N[b] * bar.Y + N[c] * bar.Z;
        return (d, w, at, n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY);
    }

    /// <summary>The nearest spot on the skin: its distance and the bone
    /// weights there (blended across the triangle). Search stops at maxDist.</summary>
    public (float dist, Dictionary<string, float> w, Vector3 at) Nearest(Vector3 q, float maxDist = 0.5f)
    {
        var (d, w, at, _, _) = Find(q, maxDist);
        return (d, w, at);
    }

    /// <summary>The nearest spot on the skin of the models accept lets through (null: any), and which model it's on (-1: none).</summary>
    public (float dist, Dictionary<string, float> w, int src) NearestFrom(Vector3 q, float maxDist, Func<int, bool>? accept = null)
    {
        var (d, w, _, tri, _) = Find(q, maxDist, accept);
        return (d, w, tri >= 0 && tri < TriSrc.Count ? TriSrc[tri] : -1);
    }

    /// <summary>The nearest spot as a triangle and its barycentrics (tri -1: nothing within maxDist).</summary>
    public (float dist, int tri, Vector3 bar) NearestTri(Vector3 q, float maxDist, Func<int, bool>? accept = null)
    {
        var (d, _, _, tri, bar) = Find(q, maxDist, accept, false);
        return (d, tri, bar);
    }

    /// <summary>The weights at a spot (a triangle and barycentrics from NearestTri), from the model's own weights or
    /// another set per point).</summary>
    public Dictionary<string, float> WeightsAt(int tri, Vector3 bar, IReadOnlyList<Dictionary<string, float>>? field = null)
    {
        var res = new Dictionary<string, float>();
        if (tri < 0 || tri >= Tris.Count) return res;
        var src = field ?? W;
        var (a, b, c) = Tris[tri];
        void Acc(int vi, float f)
        {
            if (f <= 0) return;
            foreach (var kv in src[vi]) res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value * f;
        }
        Acc(a, bar.X); Acc(b, bar.Y); Acc(c, bar.Z);
        return res;
    }

    /// <summary>Weights spread over the points around each one (0.1.15, the Soft fit): a bell curve over straight-line
    /// distance, half the radius wide, nothing past the radius. pos / w: the points and their weights; use(i) picks which
    /// points take part (null: all); the rest come back as they were.</summary>
    public static Dictionary<string, float>[] Spread(IReadOnlyList<Vector3> pos, IReadOnlyList<Dictionary<string, float>> w, float radius, Func<int, bool>? use = null)
    {
        int n = pos.Count;
        var res = new Dictionary<string, float>[n];
        for (int i = 0; i < n; i++) res[i] = w[i];
        if (radius <= 1e-4f || n == 0) return res;
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var wi = new (int b, float w)[n][];
        for (int i = 0; i < n; i++)
        {
            wi[i] = Array.Empty<(int, float)>();
            if (use != null && !use(i)) continue;
            float t = w[i].Values.Sum();
            if (t <= 1e-6f) continue;
            var row = new List<(int, float)>();
            foreach (var kv in w[i])
            {
                if (kv.Value <= 0) continue;
                if (!index.TryGetValue(kv.Key, out var b)) { b = names.Count; index[kv.Key] = b; names.Add(kv.Key); }
                row.Add((b, kv.Value / t));
            }
            wi[i] = row.ToArray();
        }
        var lo = new Vector3(float.MaxValue);
        for (int i = 0; i < n; i++) lo = Vector3.Min(lo, pos[i]);
        float cell = radius;
        (int, int, int) C(Vector3 p) { var q = (p - lo) / cell; return ((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z)); }
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (wi[i].Length == 0) continue;
            var c = C(pos[i]);
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
            l.Add(i);
        }
        float r2 = radius * radius, sigma = radius * 0.5f, inv = 1f / (sigma * sigma);
        int nb = names.Count;
        System.Threading.Tasks.Parallel.For(0, n, () => (acc: new float[nb], used: new List<int>()), (i, _, st) =>
        {
            if (wi[i].Length == 0) return st;
            var (acc, used) = st;
            var pi = pos[i];
            var (cx, cy, cz) = C(pi);
            float tot = 0;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                    for (int z = cz - 1; z <= cz + 1; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) continue;
                        foreach (int j in l)
                        {
                            float d2 = (pos[j] - pi).LengthSquared();
                            if (d2 > r2) continue;
                            float g = MathF.Exp(-d2 * inv);
                            tot += g;
                            foreach (var (b, ww) in wi[j]) { if (acc[b] == 0) used.Add(b); acc[b] += g * ww; }
                        }
                    }
            var d = new Dictionary<string, float>(used.Count);
            foreach (int b in used) { if (tot > 0 && acc[b] > 0) d[names[b]] = acc[b] / tot; acc[b] = 0; }
            used.Clear();
            res[i] = d;
            return st;
        }, _ => { });
        return res;
    }

    (float dist, Dictionary<string, float> w, Vector3 at, int tri, Vector3 bar) Find(Vector3 q, float maxDist, Func<int, bool>? accept = null, bool weights = true)
    {
        float bestSq = float.MaxValue; int bt = -1; Vector3 bp = default, bbar = default;
        var (cx, cy, cz) = CellOf(q);
        // a point outside the grid is at least this far from anything in it
        var clamped = Vector3.Clamp(q, _min, _max);
        float outside = (q - clamped).Length();
        int maxRing = (int)(maxDist / _cell) + 2;
        var seen = new HashSet<int>();
        for (int r = 0; r <= maxRing; r++)
        {
            // every cell in ring r is at least (r - 1) cells away
            if (bt >= 0 && MathF.Max((r - 1) * _cell, outside) > MathF.Sqrt(bestSq)) break;
            if (outside > maxDist) break;
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || x >= _nx) continue;
                for (int y = cy - r; y <= cy + r; y++)
                {
                    if (y < 0 || y >= _ny) continue;
                    for (int z = cz - r; z <= cz + r; z++)
                    {
                        if (z < 0 || z >= _nz) continue;
                        if (Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) != r) continue;
                        if (!_grid.TryGetValue(Key(x, y, z), out var l)) continue;
                        foreach (int t in l)
                        {
                            if (!seen.Add(t)) continue;
                            if (accept != null && (t >= TriSrc.Count || !accept(TriSrc[t]))) continue;
                            var (a, b, c) = Tris[t];
                            var (cp, bar) = ClosestOnTri(q, P[a], P[b], P[c]);
                            float d = (cp - q).LengthSquared();
                            if (d < bestSq || (d == bestSq && t < bt)) { bestSq = d; bt = t; bp = cp; bbar = bar; }
                        }
                    }
                }
            }
        }
        float best = bestSq;
        if (bt < 0) return (float.MaxValue, new Dictionary<string, float>(), q, -1, default);
        if (!weights) return (MathF.Sqrt(best), new Dictionary<string, float>(), bp, bt, bbar);
        var (ta, tb, tc) = Tris[bt];
        var res = new Dictionary<string, float>();
        void Acc(int vi, float f)
        {
            if (f <= 0) return;
            foreach (var kv in W[vi]) res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value * f;
        }
        Acc(ta, bbar.X); Acc(tb, bbar.Y); Acc(tc, bbar.Z);
        return (MathF.Sqrt(best), res, bp, bt, bbar);
    }

    // Ericson, Real-Time Collision Detection 5.1.5; returns point and barycentrics
    static (Vector3, Vector3) ClosestOnTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (a, new Vector3(1, 0, 0));
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (b, new Vector3(0, 1, 0));
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return (a + v * ab, new Vector3(1 - v, v, 0)); }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (c, new Vector3(0, 0, 1));
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return (a + w * ac, new Vector3(1 - w, 0, w)); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (b + w * (c - b), new Vector3(0, 1 - w, w)); }
        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return (a + ab * vv + ac * ww, new Vector3(1 - vv - ww, vv, ww));
    }
}
