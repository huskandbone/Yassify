// Yassify engine (shared with Tailor) - refit a piece from one body shape to another (0.1.16). A body mod's shape options (Neolithe's Chest XS - L, Almond / Hazelnut / Macadamia /
// Pushup, Default / Neobelly / Buff; Legs Small - Large) are the same skin in different shapes - the same UVs point for point -
// so how every skin point moves from one shape to another is known exactly, and a piece made for one shape moves with the
// skin around it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Yassify.Core;

/// <summary>One body shape a body mod offers for a slot: the option that puts its own model at the slot's game path.</summary>
public sealed record SizeOption(string Group, string Section, string Name, string RelPath)
{
    /// <summary>"SFW M (Neobelly)", "SFW Hazelnut L (Default Hazelnut)", "Gen A Medium (Default)".</summary>
    public string Label => Section.Length > 0 ? $"{Name} ({Section})" : Name;
}

public static class SizeOptions
{
    static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    static string Title(string s) => string.Join(" ", s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                       .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));

    /// <summary>Every option of the mod that puts a model of its own at gamePath, in the mod's order, with the section header it
    /// sits under (options like "--- NEOBELLY ---" that hold nothing).</summary>
    public static List<SizeOption> For(string modRoot, string gamePath)
    {
        var res = new List<SizeOption>();
        string want = Norm(gamePath);
        foreach (var (group, opts) in Groups(modRoot))
        {
            string section = "";
            foreach (var o in opts)
            {
                string name = (o["Name"]?.GetValue<string>() ?? "").Trim();
                var files = o["Files"] as JsonObject;
                if (files == null || files.Count == 0)
                {
                    if (name.StartsWith("-")) { var t = name.Trim('-', ' '); if (t.Length > 0) section = Title(t); }
                    continue;
                }
                foreach (var kv in files)
                    if (string.Equals(Norm(kv.Key), want, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue v && v.TryGetValue<string>(out var rel))
                    {
                        res.Add(new SizeOption(group, section, name, Norm(rel)));
                        break;
                    }
            }
        }
        return res;
    }

    /// <summary>The mod's options as groups: the one-file layout's "Groups" in meta.json (mod format 4, every mod on a current
    /// Penumbra), else the older group_*.json files in name order; the default option first, as a group of its own (a body
    /// pack that simply is one body keeps it there).</summary>
    static IEnumerable<(string name, List<JsonObject> options)> Groups(string modRoot)
    {
        if (!Directory.Exists(modRoot)) yield break;
        JsonObject? m = null;
        var meta = Path.Combine(modRoot, "meta.json");
        if (File.Exists(meta)) { try { m = Parse(File.ReadAllBytes(meta)) as JsonObject; } catch { } }
        bool oneFile = m?["FileVersion"] is JsonValue fv && fv.TryGetValue<int>(out var ver) && ver >= 4;
        var files = oneFile ? new List<string>()
            : Directory.EnumerateFiles(modRoot, "group_*.json").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        // the default option
        JsonObject? def = null;
        if (oneFile || files.Count == 0) def = m?["DefaultData"] as JsonObject;
        if (def == null && !oneFile)
        {
            var dp = Path.Combine(modRoot, "default_mod.json");
            if (File.Exists(dp)) { try { def = Parse(File.ReadAllBytes(dp)) as JsonObject; } catch { } }
        }
        if (def?["Files"] is JsonObject df && df.Count > 0)
            yield return ("Default", new List<JsonObject> { new JsonObject { ["Name"] = "Default", ["Files"] = df.DeepClone() } });
        if (files.Count > 0)
        {
            foreach (var f in files)
            {
                JsonObject? g = null;
                try { g = Parse(File.ReadAllBytes(f)) as JsonObject; } catch { }
                if (g == null) continue;
                yield return (g["Name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(f), Options(g));
            }
            yield break;
        }
        if (m?["Groups"] is not JsonArray arr) yield break;
        foreach (var n in arr)
            if (n is JsonObject g) yield return (g["Name"]?.GetValue<string>() ?? "", Options(g));
    }

    static List<JsonObject> Options(JsonObject g)
    {
        var res = new List<JsonObject>();
        foreach (var key in new[] { "Options", "Containers" })
            if (g[key] is JsonArray a)
                foreach (var o in a) if (o is JsonObject jo) res.Add(jo);
        return res;
    }

    static JsonNode? Parse(byte[] raw)
    {
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        return JsonNode.Parse(Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0)));
    }
}

/// <summary>How the skin moves from one body shape to another: the first shape's skin as a surface, and per point of it where
/// that point is in the second shape (paired by UV - the shapes are one mesh moved).</summary>
public sealed class RefitField
{
    public readonly BodySurface Source = new();
    public Vector3[] Move = Array.Empty<Vector3>();       // per Source point: second shape minus first
    public Vector3[] ToNormal = Array.Empty<Vector3>();   // per Source point: the second shape's normal there
    public int Paired, Points;
    public string Key = "";
    // the source points in a grid, for "every skin point within r"
    float _cell = 0.02f; Vector3 _lo;
    readonly Dictionary<(int, int, int), List<int>> _grid = new();

    static IEnumerable<int> SkinMeshes(MdlFile m) => m.LodMeshes(0).Where(mi =>
        m.Meshes[mi].VertexCount > 0 && m.Element(mi, VUsage.Position) != null
        && BodySurface.IsSkinMaterial(m.Meshes[mi].MaterialIndex < m.MaterialNames.Count ? m.MaterialNames[m.Meshes[mi].MaterialIndex] : ""));

    static string MatOf(MdlFile m, int mi) => m.Meshes[mi].MaterialIndex < m.MaterialNames.Count ? m.MaterialNames[m.Meshes[mi].MaterialIndex] : "";

    static Vector3 V3(Vector4 v) => new(v.X, v.Y, v.Z);
    static Vector3 Nrm(VElement ne, Vector4 n4)
    {
        var n = ne.Type == 8 ? V3(n4) * 2 - Vector3.One : V3(n4);
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
    }
    static (long, long) UvKey(Vector4 u) => ((long)MathF.Round(u.X * 65536), (long)MathF.Round(u.Y * 65536));

    /// <summary>For each skin point of model a, its partner in model b's skin: the same UV (on the mesh with the same material,
    /// else b's first skin mesh), the nearest of several - mirrored halves share UVs. -1 where there's none.
    /// Lists per skin mesh of a, in its order.</summary>
    public static List<(int meshA, int meshB, int[] pair)> PairByUv(MdlFile a, MdlFile b)
    {
        var res = new List<(int, int, int[])>();
        var bs = SkinMeshes(b).ToList();
        foreach (int ma in SkinMeshes(a))
        {
            int mb = bs.FirstOrDefault(x => string.Equals(MatOf(b, x), MatOf(a, ma), StringComparison.OrdinalIgnoreCase), bs.Count > 0 ? bs[0] : -1);
            var pair = Enumerable.Repeat(-1, a.Meshes[ma].VertexCount).ToArray();
            var ua = a.Element(ma, VUsage.UV); var pa = a.Element(ma, VUsage.Position)!;
            if (mb < 0 || ua == null || b.Element(mb, VUsage.UV) is not { } ub) { res.Add((ma, mb, pair)); continue; }
            var pb = b.Element(mb, VUsage.Position)!;
            var map = new Dictionary<(long, long), List<int>>();
            for (int v = 0; v < b.Meshes[mb].VertexCount; v++)
            {
                var k = UvKey(b.ReadVec(mb, ub, v));
                if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>();
                l.Add(v);
            }
            for (int v = 0; v < pair.Length; v++)
            {
                if (!map.TryGetValue(UvKey(a.ReadVec(ma, ua, v)), out var l)) continue;
                if (l.Count == 1) { pair[v] = l[0]; continue; }
                var p = V3(a.ReadVec(ma, pa, v));
                float best = float.MaxValue;
                foreach (int j in l)
                {
                    float d = (V3(b.ReadVec(mb, pb, j)) - p).LengthSquared();
                    if (d < best) { best = d; pair[v] = j; }
                }
            }
            res.Add((ma, mb, pair));
        }
        return res;
    }

    /// <summary>The move from shape <paramref name="from"/> to shape <paramref name="to"/> (both a body mod's models for one slot).</summary>
    public static RefitField Build(MdlFile from, MdlFile to, string key)
    {
        var f = new RefitField { Key = key };
        f.Source.Add(from, "from", (_, mat, n) => n > 0 && BodySurface.IsSkinMaterial(mat));
        var move = new List<Vector3>(); var tn = new List<Vector3>();
        foreach (var (ma, mb, pair) in PairByUv(from, to))
        {
            var pa = from.Element(ma, VUsage.Position)!; var na = from.Element(ma, VUsage.Normal);
            var pb = mb >= 0 ? to.Element(mb, VUsage.Position) : null; var nb = mb >= 0 ? to.Element(mb, VUsage.Normal) : null;
            for (int v = 0; v < pair.Length; v++)
            {
                var p = V3(from.ReadVec(ma, pa, v));
                var n0 = na != null ? Nrm(na, from.ReadVec(ma, na, v)) : Vector3.UnitY;
                if (pair[v] >= 0 && pb != null)
                {
                    move.Add(V3(to.ReadVec(mb, pb, pair[v])) - p);
                    tn.Add(nb != null ? Nrm(nb, to.ReadVec(mb, nb, pair[v])) : n0);
                    f.Paired++;
                }
                else { move.Add(Vector3.Zero); tn.Add(n0); }
            }
        }
        f.Points = move.Count;
        f.Move = move.ToArray(); f.ToNormal = tn.ToArray();
        f.Source.Build();
        if (f.Source.P.Count != f.Move.Length) throw new MdlException("refit: the skin's points didn't line up");
        f._lo = new Vector3(float.MaxValue);
        foreach (var p in f.Source.P) f._lo = Vector3.Min(f._lo, p);
        for (int i = 0; i < f.Source.P.Count; i++)
        {
            var c = f.CellOf(f.Source.P[i]);
            if (!f._grid.TryGetValue(c, out var l)) f._grid[c] = l = new List<int>();
            l.Add(i);
        }
        return f;
    }

    (int, int, int) CellOf(Vector3 p) { var q = (p - _lo) / _cell; return ((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z)); }

    /// <summary>Cloth: the sphere of skin whose moves are averaged reaches this far, plus this many times the cloth's distance.</summary>
    public static float SpreadBase = 0.015f, SpreadPerDist = 1f;

    /// <summary>The largest move of any skin point (how different the two shapes are).</summary>
    public float MostMove => Move.Length == 0 ? 0 : Move.Max(m => m.Length());

    /// <summary>Where a point of a piece goes, and how its surface turns. On the skin (skin parts): exactly the move of the spot
    /// under it. Cloth: the moves of the skin around it, averaged over a sphere that grows with how far out the cloth is (1.5 cm
    /// plus its distance), so cloth spanning a gap (the cleavage, between the legs) takes both sides - a cloth point never
    /// follows one breast and its neighbour the other.</summary>
    public (Vector3 move, Quaternion turn) At(Vector3 p, bool skin)
    {
        var (d, tri, bar) = Source.NearestTri(p, 0.5f);
        if (tri < 0) return (Vector3.Zero, Quaternion.Identity);
        var (a, b, c) = Source.Tris[tri];
        if (skin || d < 1e-4f)
        {
            var mv = Move[a] * bar.X + Move[b] * bar.Y + Move[c] * bar.Z;
            var ns = Source.N[a] * bar.X + Source.N[b] * bar.Y + Source.N[c] * bar.Z;
            var nt = ToNormal[a] * bar.X + ToNormal[b] * bar.Y + ToNormal[c] * bar.Z;
            return (mv, Turn(ns, nt));
        }
        float r = SpreadBase + SpreadPerDist * d, r2 = r * r, inv = 1f / (0.25f * r2);
        var (cx, cy, cz) = CellOf(p);
        int k = (int)MathF.Ceiling(r / _cell);
        Vector3 sum = Vector3.Zero, sn = Vector3.Zero, st = Vector3.Zero; float tot = 0;
        for (int x = cx - k; x <= cx + k; x++)
            for (int y = cy - k; y <= cy + k; y++)
                for (int z = cz - k; z <= cz + k; z++)
                {
                    if (!_grid.TryGetValue((x, y, z), out var l)) continue;
                    foreach (int i in l)
                    {
                        float d2 = (Source.P[i] - p).LengthSquared();
                        if (d2 > r2) continue;
                        float g = MathF.Exp(-d2 * inv);
                        sum += Move[i] * g; sn += Source.N[i] * g; st += ToNormal[i] * g; tot += g;
                    }
                }
        if (tot <= 1e-9f)
        {
            var mv = Move[a] * bar.X + Move[b] * bar.Y + Move[c] * bar.Z;
            return (mv, Turn(Source.N[a] * bar.X + Source.N[b] * bar.Y + Source.N[c] * bar.Z, ToNormal[a] * bar.X + ToNormal[b] * bar.Y + ToNormal[c] * bar.Z));
        }
        return (sum / tot, Turn(sn, st));
    }

    /// <summary>The turn taking direction a to direction b (shortest arc).</summary>
    static Quaternion Turn(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f) return Quaternion.Identity;
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.9999f) return Quaternion.Identity;      // flipped: leave it (never happens between shapes of one body)
        var axis = Vector3.Cross(a, b);
        var q = new Quaternion(axis, 1f + dot);
        return Quaternion.Normalize(q);
    }

    /// <summary>Every point of the model's meshes (all LODs): its move and turn. Index: mesh number in the file.</summary>
    public Dictionary<int, (Vector3[] move, Quaternion[] turn)> ForModel(MdlFile m, CancellationToken ct = default)
    {
        var res = new Dictionary<int, (Vector3[], Quaternion[])>();
        for (int mi = 0; mi < m.Meshes.Count; mi++)
        {
            var pe = m.Element(mi, VUsage.Position);
            int n = m.Meshes[mi].VertexCount;
            if (pe == null || n == 0) continue;
            bool skin = BodySurface.IsSkinMaterial(MatOf(m, mi));
            var pts = new Vector3[n];
            for (int v = 0; v < n; v++) pts[v] = V3(m.ReadVec(mi, pe, v));
            var mv = new Vector3[n]; var tq = new Quaternion[n];
            System.Threading.Tasks.Parallel.For(0, n, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, v =>
            {
                var (a, b) = At(pts[v], skin);
                mv[v] = a; tq[v] = b;
            });
            res[mi] = (mv, tq);
        }
        return res;
    }

    /// <summary>How well a piece sits on this field's first shape: the mean distance from the piece's skin to that skin (by UV
    /// pairs when they line up, else to the nearest spot), or - a piece with no skin - how its cloth sits on it (cloth inside
    /// the skin counts heavily). Smaller = closer. kind: "skin" or "cloth".</summary>
    public static (double score, string kind) Fit(MdlFile piece, MdlFile shape, BodySurface? shapeSurface = null)
    {
        var skinPairs = PairByUv(piece, shape);
        int pts = 0, paired = 0; double sum = 0;
        foreach (var (ma, mb, pair) in skinPairs)
        {
            var pa = piece.Element(ma, VUsage.Position)!;
            var pb = mb >= 0 ? shape.Element(mb, VUsage.Position) : null;
            for (int v = 0; v < pair.Length; v++)
            {
                pts++;
                if (pair[v] < 0 || pb == null) continue;
                paired++;
                sum += (V3(piece.ReadVec(ma, pa, v)) - V3(shape.ReadVec(mb, pb, pair[v]))).Length();
            }
        }
        if (pts > 0 && paired >= pts / 2) return (sum / paired, "skin");
        var surf = shapeSurface;
        if (surf == null)
        {
            surf = new BodySurface();
            surf.Add(shape, "shape", (_, mat, n) => n > 0 && BodySurface.IsSkinMaterial(mat));
            surf.Build();
        }
        if (surf.Empty) return (double.MaxValue, "cloth");
        if (pts > 0)
        {
            // skin, but not the same UVs: the distance to the nearest spot
            double s2 = 0; int n2 = 0;
            foreach (var (ma, _, pair) in skinPairs)
            {
                var pa = piece.Element(ma, VUsage.Position)!;
                for (int v = 0; v < pair.Length; v += 3) { var (d, _, _) = surf.Nearest(V3(piece.ReadVec(ma, pa, v)), 0.2f); if (d < 0.2f) { s2 += d; n2++; } }
            }
            if (n2 > 0) return (s2 / n2, "skin");
        }
        // cloth only: within 4 cm of the skin, how far out (inside counts 20 times)
        double sc = 0; int nc = 0;
        foreach (int mi in piece.LodMeshes(0))
        {
            var pe = piece.Element(mi, VUsage.Position);
            if (pe == null) continue;
            int step = Math.Max(1, piece.Meshes[mi].VertexCount / 1500);
            for (int v = 0; v < piece.Meshes[mi].VertexCount; v += step)
            {
                var p = V3(piece.ReadVec(mi, pe, v));
                var (d, _, at, nn) = surf.NearestDetail(p, 0.04f);
                if (d > 0.04f) continue;
                float sd = Vector3.Dot(p - at, nn);
                sc += sd < 0 ? -sd * 20 : sd; nc++;
            }
        }
        return (nc > 0 ? sc / nc : double.MaxValue, "cloth");
    }
}
