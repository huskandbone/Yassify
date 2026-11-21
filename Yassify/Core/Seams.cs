// Yassify engine (shared with Tailor) - seams with your other pieces: where the edge of the piece being
// edited meets the edge of another piece you're wearing (top and bottom at the
// waist, arms and hands at the wrist, legs and feet at the ankle), its points
// take that piece's weights - eased in over a few cm - so the seam stays shut
// even when the two pieces' jiggle sliders differ. The piece edited last
// follows the other one. Only EDGES count (0.1.11): a piece that just lies over
// another (a top over your legs) has no seam there, so its own sliders apply -
// 0.1.9 / 0.1.10 took every point within 2 mm of another piece as a seam, which
// put the legs' jiggle back on a top's butt with Butt at 0%.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Yassify.Core;

/// <summary>Your other pieces' points with their weights (normalised), to find seams.</summary>
public sealed class SeamGuide
{
    /// <summary>Points this close are the same spot: a seam.</summary>
    public const float Touch = 0.002f;
    /// <summary>The weights ease into the seam's over this far.</summary>
    public const float Ease = 0.04f;

    readonly List<Vector3> _p = new();
    readonly List<Dictionary<string, float>> _w = new();
    readonly List<int> _from = new();
    readonly List<bool> _yas = new();
    readonly Dictionary<(int, int, int), List<int>> _grid = new();
    public readonly List<string> Labels = new();

    public bool Empty => _p.Count == 0;
    public Dictionary<string, float> Weights(int i) => _w[i];
    public Vector3 Point(int i) => _p[i];
    public string LabelOf(int i) => Labels[_from[i]];
    /// <summary>The other piece has YAS weights at this point.</summary>
    public bool HasYas(int i) => _yas[i];

    public void Add(MdlFile m, string label)
    {
        int src = Labels.Count;
        Labels.Add(label);
        var edges = SeamSpots.Edges(m);                 // only the other piece's edges can be a seam
        foreach (int mi in m.LodMeshes(0))
        {
            var me = m.Meshes[mi];
            var pe = m.Element(mi, VUsage.Position);
            if (pe == null || me.VertexCount == 0) continue;
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
            if (table == null) continue;
            for (int v = 0; v < me.VertexCount; v++)
            {
                if (!edges.Contains((mi, v))) continue;
                var d = new Dictionary<string, float>();
                foreach (var (sl, w) in m.ReadWeights(mi, v))
                {
                    if (w == 0 || sl >= table.Count || table[sl] >= m.BoneNames.Count) continue;
                    var bn = m.BoneNames[table[sl]];
                    d[bn] = d.GetValueOrDefault(bn) + w / 255f;
                }
                float t = d.Values.Sum();
                if (t <= 1e-6f) continue;
                foreach (var k in d.Keys.ToList()) d[k] /= t;
                var p4 = m.ReadVec(mi, pe, v);
                int i = _p.Count;
                _p.Add(new Vector3(p4.X, p4.Y, p4.Z));
                _w.Add(d);
                _from.Add(src);
                _yas.Add(d.Keys.Any(GearEditor.IsYas));
                var c = Cell(_p[i], Touch);
                if (!_grid.TryGetValue(c, out var l)) _grid[c] = l = new List<int>();
                l.Add(i);
            }
        }
    }

    internal static (int, int, int) Cell(Vector3 p, float size) =>
        ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));

    /// <summary>The other pieces' point at this spot (within Touch), or -1.</summary>
    public int At(Vector3 q)
    {
        var (x, y, z) = Cell(q, Touch);
        int best = -1;
        float bd = Touch * Touch;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                        foreach (int i in l)
                        {
                            float d = (_p[i] - q).LengthSquared();
                            if (d <= bd) { bd = d; best = i; }
                        }
        return best;
    }
}

/// <summary>The edited piece's own seam points (where it sits on another piece), for the easing.</summary>
public sealed class SeamSpots
{
    readonly List<(Vector3 p, int other)> _spots = new();
    readonly Dictionary<(int, int, int), List<int>> _grid = new();
    public int Count => _spots.Count;

    /// <summary>The piece's LOD 0 points on its edges that sit on one of the guide's (other pieces' edges; original
    /// positions).</summary>
    public static SeamSpots Find(MdlFile m, SeamGuide g)
    {
        var s = new SeamSpots();
        var edges = Edges(m);
        foreach (int mi in m.LodMeshes(0))
        {
            var pe = m.Element(mi, VUsage.Position);
            if (pe == null) continue;
            for (int v = 0; v < m.Meshes[mi].VertexCount; v++)
            {
                if (!edges.Contains((mi, v))) continue;
                var p4 = m.ReadVec(mi, pe, v);
                var p = new Vector3(p4.X, p4.Y, p4.Z);
                int k = g.At(p);
                if (k < 0) continue;
                int i = s._spots.Count;
                s._spots.Add((p, k));
                var c = SeamGuide.Cell(p, SeamGuide.Ease);
                if (!s._grid.TryGetValue(c, out var l)) s._grid[c] = l = new List<int>();
                l.Add(i);
            }
        }
        return s;
    }

    /// <summary>A piece's LOD 0 points on its open edges: edges only one triangle uses. Points at the same spot count as
    /// one first, so parts split by UVs, normals or materials join up and their split lines aren't edges.</summary>
    public static HashSet<(int mesh, int v)> Edges(MdlFile m)
    {
        var spot = new Dictionary<(long, long, long), int>();
        var idOf = new Dictionary<int, int[]>();
        foreach (int mi in m.LodMeshes(0))
        {
            var pe = m.Element(mi, VUsage.Position);
            int n = m.Meshes[mi].VertexCount;
            if (pe == null || n == 0) continue;
            var a = new int[n];
            for (int v = 0; v < n; v++)
            {
                var p = m.ReadVec(mi, pe, v);
                var key = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));   // 0.01 mm
                if (!spot.TryGetValue(key, out int id)) spot[key] = id = spot.Count;
                a[v] = id;
            }
            idOf[mi] = a;
        }
        var uses = new Dictionary<(int, int), int>();
        void Use(int p, int q) { var e = p < q ? (p, q) : (q, p); uses[e] = uses.GetValueOrDefault(e) + 1; }
        foreach (var (mi, a) in idOf)
        {
            var idx = m.ReadIndices(mi);
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                int i0 = idx[k], i1 = idx[k + 1], i2 = idx[k + 2];
                if (i0 >= a.Length || i1 >= a.Length || i2 >= a.Length) continue;
                int x = a[i0], y = a[i1], z = a[i2];
                if (x == y || y == z || x == z) continue;          // a sliver has no edges
                Use(x, y); Use(y, z); Use(z, x);
            }
        }
        var open = new HashSet<int>();
        foreach (var (e, c) in uses) if (c == 1) { open.Add(e.Item1); open.Add(e.Item2); }
        var res = new HashSet<(int, int)>();
        foreach (var (mi, a) in idOf)
            for (int v = 0; v < a.Length; v++) if (open.Contains(a[v])) res.Add((mi, v));
        return res;
    }

    /// <summary>How much a point (original position) eases into the nearest seam - 1 on it, 0 from
    /// Ease away - and the other piece's point there (-1: no seam near).</summary>
    public (float f, int other) Near(Vector3 q)
    {
        if (_spots.Count == 0) return (0, -1);
        var (x, y, z) = SeamGuide.Cell(q, SeamGuide.Ease);
        int best = -1;
        float bd = SeamGuide.Ease * SeamGuide.Ease;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                        foreach (int i in l)
                        {
                            float d = (_spots[i].p - q).LengthSquared();
                            if (d < bd) { bd = d; best = i; }
                        }
        if (best < 0) return (0, -1);
        float t = 1f - MathF.Sqrt(bd) / SeamGuide.Ease;              // 1 on the seam .. 0 at Ease
        float f = t * t * t * (t * (t * 6f - 15f) + 10f);            // smootherstep: no kink where it starts
        return (f, _spots[best].other);
    }

    /// <summary>a eased toward b by f (both normalised; the result too).</summary>
    public static Dictionary<string, float> Mix(Dictionary<string, float> a, Dictionary<string, float> b, float f)
    {
        float ta = a.Values.Sum(), tb = b.Values.Sum();
        var r = new Dictionary<string, float>();
        if (ta > 1e-6f) foreach (var kv in a) r[kv.Key] = kv.Value / ta * (1 - f);
        if (tb > 1e-6f) foreach (var kv in b) r[kv.Key] = r.GetValueOrDefault(kv.Key) + kv.Value / tb * f;
        foreach (var k in r.Where(kv => kv.Value <= 1e-7f).Select(kv => kv.Key).ToList()) r.Remove(k);
        return r;
    }
}
