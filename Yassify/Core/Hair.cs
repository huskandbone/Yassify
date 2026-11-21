// Yassify engine (shared with Tailor) - hair sway, on the bones your character already has. The hair bones are
// read from the game (where each sits, its parent, whether the hair's physics moves
// it); a check says what each part of a hair rides (sways, stiff, or another
// hairstyle's bones that aren't on you); and the weights can be made fresh - stiff
// at each lock's root, looser towards its end, the Blender add-on's Auto-Weight
// Hair - moved off missing bones onto ones you have, or scaled per part.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Yassify.Core;

/// <summary>Where a part's weights come from.</summary>
public enum HairSource
{
    Same,      // a part: as the whole hair; the whole hair: as made
    Own,       // as the mod made them
    Moved,     // as made, but weights on bones you don't have go onto ones you do
    Fresh,     // made fresh on the bones you have: stiff roots, loose ends
}

/// <summary>Settings for weights made fresh (the add-on's Auto-Weight Hair defaults).</summary>
public sealed class HairSettings
{
    public float StiffMm = 20f;          // each lock stays on the head this far down from where its bones start
    public float LoosenMm = 120f;        // then eases onto its bones over this far
    public float Curve = 1f;             // >1 holds the top still longer
    public float EndsFree = 1f;          // how free the very ends get (1 = ride their bones completely)
    public int Smooth = 2;               // passes averaging each point with its neighbours
    /// <summary>0.1.25 (as some hair mods are weighted): each lock's bones share the hair in bands across it - every
    /// point the same distance down the lock gets the same mix, whatever its distance from the bones - blended between
    /// neighbouring bones over BlendMm more than their own length.</summary>
    public bool Bands;
    public float BlendMm;
    /// <summary>0.1.26: which hair each bone carries (bone -> "L" / "R" / "F" / "B" = that side of your head, "pN" = part N of the
    /// piece; none = any hair near it). A point that some bone is set to carries only on those bones; other points only on the
    /// bones left on "any".</summary>
    public Dictionary<string, string> BoneHair = new(StringComparer.Ordinal);
    public HairSettings Clone()
    {
        var c = (HairSettings)MemberwiseClone();
        c.BoneHair = new Dictionary<string, string>(BoneHair, StringComparer.Ordinal);
        return c;
    }

    /// <summary>0.1.26: the hair an assignment names (for the lists).</summary>
    public static string HairLabel(string a, Func<int, string>? part = null) => a switch
    {
        "" => "Any (nearest)",
        "L" => "Left side",
        "R" => "Right side",
        "F" => "Front",
        "B" => "Back",
        _ when a.StartsWith('p') && int.TryParse(a.AsSpan(1), out int n) => part?.Invoke(n) ?? $"Part {n + 1}",
        _ => a,
    };
}

/// <summary>The hair bones on your character, read from the game.</summary>
public sealed class HairRig
{
    public sealed class Bone
    {
        public string Name = "", Parent = "";
        public Vector3 Head;             // bind pose, your character's model space (y up, x = your left)
        public bool Moves;               // the hair's physics moves it
        /// <summary>0.1.19, a bone of a hand-placed lock: how much the hair near it rides it, how far round (metres, 0 = no
        /// limit), and where a lock's last bone ends.</summary>
        public float Follow = 1f, Reach;
        public Vector3? Tip;
    }

    public readonly Dictionary<string, Bone> Bones = new(StringComparer.Ordinal);   // j_kao + j_kami* + j_ex_h*
    public readonly HashSet<string> All = new(StringComparer.Ordinal);              // every bone on you
    public string ExSet = "";            // the hairstyle whose j_ex bones are loaded ("0123"), "" none
    public bool PhysicsKnown;            // the hair's physics file was read
    public string PhysicsFile = "";
    /// <summary>0.1.18: your head bone at rest (rig space), and your race ("c1801") - new hair bones hang from it.</summary>
    public Matrix4x4 KaoRest = Matrix4x4.Identity;
    public bool KaoKnown;
    public string Race = "";
    /// <summary>The hair skeleton your hairstyle loads now (its number, -1 none) and its game path.</summary>
    public int HairSkel = -1;
    public string HairSkelPath = "";
    /// <summary>Bones Tailor is adding (0.1.18): when there are any, fresh weights go on them alone.</summary>
    public readonly List<string> Added = new();
    /// <summary>0.1.19, locks placed by hand: what a point doesn't give its new locks keeps the weights it had (Hair.KeepOwn).</summary>
    public bool KeepOwn;
    /// <summary>0.1.24, posed locks: per new bone, from where it sits in the hair as made to where it was posed (row vectors:
    /// p * delta). The hair riding it is reshaped by it (Hair.Pose).</summary>
    public Dictionary<string, Matrix4x4> PoseDelta = new(StringComparer.Ordinal);
    /// <summary>Bends a point of the piece (with its weights) into your race's shape; null = same race.</summary>
    public Func<Vector3, Dictionary<string, float>, Vector3>? ToRig;
    /// <summary>0.1.25: the bones your body's own physics moves (j_kami...), null unknown.</summary>
    public IReadOnlySet<string>? BodySim;

    public static bool IsSway(string n) => n.StartsWith("j_kami", StringComparison.Ordinal) || n.StartsWith("j_ex_h", StringComparison.Ordinal);
    /// <summary>A hair bone that's on you and moves (physics not read: every hair bone on you).</summary>
    public bool Moves(string n) => IsSway(n) && Bones.TryGetValue(n, out var b) && (b.Moves || !PhysicsKnown);
    public List<Bone> Moving() => Added.Count > 0
        ? Added.Where(Bones.ContainsKey).Select(n => Bones[n]).ToList()
        : Bones.Values.Where(b => IsSway(b.Name) && (b.Moves || !PhysicsKnown)).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();

    /// <summary>0.1.26: the bones in order: each lock (a bone whose parent doesn't move) from its root down, branches in turn; depth below
    /// its lock's root; the lock's number.</summary>
    public List<(string name, int depth, int lockNo)> InOrder(List<string> moving)
    {
        var set = moving.ToHashSet(StringComparer.Ordinal);
        var kids = moving.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var n in moving)
            if (Bones.TryGetValue(n, out var b) && set.Contains(b.Parent) && b.Parent != n) kids[b.Parent].Add(n);
        var res = new List<(string, int, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int lockNo = 0;
        void Walk(string n, int d, int l)
        {
            if (!seen.Add(n)) return;
            res.Add((n, d, l));
            foreach (var k in kids[n]) Walk(k, d + 1, l);
        }
        foreach (var n in moving)
            if (!(Bones.TryGetValue(n, out var b) && set.Contains(b.Parent)) && !seen.Contains(n)) Walk(n, 0, lockNo++);
        foreach (var n in moving) if (!seen.Contains(n)) Walk(n, 0, lockNo++);   // (a loop of parents: shouldn't happen)
        return res;
    }

    /// <summary>A bone and every moving bone below it.</summary>
    public List<string> Below(List<string> moving, string top)
    {
        var res = new List<string> { top };
        for (int i = 0; i < res.Count; i++)
            foreach (var n in moving)
                if (!res.Contains(n) && Bones.TryGetValue(n, out var b) && b.Parent == res[i]) res.Add(n);
        return res;
    }

    public Vector3 Point(Vector3 p, Dictionary<string, float> w) => ToRig != null ? ToRig(p, w) : p;

    static readonly Regex Ex = new(@"^j_ex_h(\d{4})_(.+)$", RegexOptions.Compiled);
    public static (string num, string rest)? ExParts(string n)
    {
        var m = Ex.Match(n);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>"j_kami_a, j_kami_b, j_kami_f_l, j_kami_f_r and h0123's 12 bones; the physics moves 14"</summary>
    public string Describe()
    {
        var sway = Bones.Values.Where(b => IsSway(b.Name)).ToList();
        if (sway.Count == 0) return "no hair bones";
        var van = sway.Where(b => b.Name.StartsWith("j_kami", StringComparison.Ordinal)).Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var ex = sway.Where(b => ExParts(b.Name) != null).GroupBy(b => ExParts(b.Name)!.Value.num).OrderBy(g => g.Key).ToList();
        var bits = new List<string>();
        if (van.Count > 0) bits.Add(string.Join(", ", van));
        foreach (var g in ex) bits.Add($"h{g.Key}'s {g.Count()} bone{(g.Count() == 1 ? "" : "s")}");
        string s = string.Join(" and ", bits);
        if (PhysicsKnown) s += $"; the hair's physics moves {sway.Count(b => b.Moves)} of them";
        if (Added.Count > 0) s += $" (with the {Added.Count} new ones)";
        return s;
    }
}

/// <summary>Stiff-root / loose-end weights from the bones' places (the add-on's _hr_model + _hr_auto).</summary>
public sealed class HairModel
{
    public const float Eps = 0.008f, Pow = 3f;
    public const int TopK = 3;
    public readonly List<string> Bones = new();
    public readonly List<(string bone, string under)> Inferred = new();
    readonly Dictionary<string, Vector3> _head = new(StringComparer.Ordinal);
    readonly List<(int bone, Vector3 a, Vector3 b)> _segs = new();
    Vector3[] _rootHead = Array.Empty<Vector3>(), _udir = Array.Empty<Vector3>();
    float[] _follow = Array.Empty<float>(), _reach = Array.Empty<float>();      // 0.1.19: per bone (hand-placed locks)
    // 0.1.25, bands: each way down a lock (root to a last bone), and its bones' stretches along it (from, to)
    sealed class BandPath
    {
        public int[] Bones = Array.Empty<int>();
        public Vector3 Root, Dir;
        public float[] S0 = Array.Empty<float>(), S1 = Array.Empty<float>();
        public (Vector3 a, Vector3 b)[] Segs = Array.Empty<(Vector3, Vector3)>();   // its own line (a bone shared with another way: only its part toward this one)
    }
    readonly List<BandPath> _paths = new();
    Vector3 _kao;                                       // 0.1.26: your head bone (sides of the head for BoneHair)

    static string Family(string n)
    {
        var p = HairRig.ExParts(n);
        if (p != null) return "h" + p.Value.num;
        return n.StartsWith("j_kami", StringComparison.Ordinal) ? "kami" : n;
    }

    /// <summary>loY: the lowest point of the hair (rig space) - a hanging chain's last bone drives everything down to it.</summary>
    public static HairModel? Build(HairRig rig, float loY)
    {
        var S = rig.Moving().Select(b => b.Name).Where(n => n != "j_kao").ToList();
        if (S.Count == 0) return null;
        var hm = new HairModel();
        hm.Bones.AddRange(S);
        var set = new HashSet<string>(S, StringComparer.Ordinal);
        foreach (var n in S) hm._head[n] = rig.Bones[n].Head;
        var par = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var n in S) par[n] = set.Contains(rig.Bones[n].Parent) ? rig.Bones[n].Parent : null;

        HashSet<string> Ancestors(string n)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = par[n];
            for (int hops = 0; cur != null && hops < 256; hops++) { seen.Add(cur); cur = par[cur]; }
            return seen;
        }
        // a chain arriving flattened (every bone straight under the head): hang a bone under the nearest
        // bone of its own family sitting above it
        var down = -Vector3.UnitY;
        foreach (var n in S)
        {
            if (par[n] != null) continue;
            string fam = Family(n);
            string? best = null;
            float bd = 1e9f;
            foreach (var q in S)
            {
                if (q == n || Family(q) != fam) continue;
                var v = hm._head[n] - hm._head[q];
                float L = v.Length();
                if (L < 1e-6f || L > 0.25f || hm._head[q].Y <= hm._head[n].Y + 0.005f) continue;
                if (Vector3.Dot(v / L, down) < 0.5f) continue;
                if (Ancestors(q).Contains(n)) continue;
                if (L < bd) { best = q; bd = L; }
            }
            if (best != null) { par[n] = best; hm.Inferred.Add((n, best)); }
        }
        var kids = S.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var n in S) if (par[n] is { } pp) kids[pp].Add(n);
        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in S)
        {
            string r = n;
            for (int hops = 0; par[r] != null && hops < 256; hops++) r = par[r]!;
            root[n] = r;
        }
        var ends = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (int j = 0; j < S.Count; j++)
        {
            var n = S[j];
            var h = hm._head[n];
            if (kids[n].Count > 0)
            {
                var acc = Vector3.Zero;
                foreach (var c in kids[n]) { hm._segs.Add((j, h, hm._head[c])); acc += hm._head[c]; }
                ends[n] = acc / kids[n].Count;
                continue;
            }
            // a hand-placed lock's last bone ends where its end was put (0.1.19)
            if (rig.Bones[n].Tip is { } tip && Vector3.Distance(tip, h) > 1e-4f)
            {
                hm._segs.Add((j, h, tip));
                ends[n] = tip;
                continue;
            }
            Vector3 d;
            float plen;
            if (par[n] is { } p0) { d = h - hm._head[p0]; plen = d.Length(); }
            else { d = down; plen = 0; }
            if (d.Length() < 1e-6f) d = down;
            d = Vector3.Normalize(d);
            float L2 = d.Y < -0.2f ? (h.Y - loY) / -d.Y + 0.02f : MathF.Max(plen * 2f, 0.05f);   // hangs: on down to the bottom; else on the way it goes
            L2 = Math.Clamp(L2, 0.03f, 1.5f);
            var e = h + d * L2;
            hm._segs.Add((j, h, e));
            ends[n] = e;
        }
        hm._follow = S.Select(n => Math.Clamp(rig.Bones[n].Follow, 0f, 1f)).ToArray();
        hm._reach = S.Select(n => MathF.Max(0f, rig.Bones[n].Reach)).ToArray();
        hm._rootHead = S.Select(n => hm._head[root[n]]).ToArray();
        hm._kao = rig.Bones.TryGetValue("j_kao", out var kb) ? kb.Head : Vector3.Zero;
        // lines for bands (0.1.25): each way from a lock's root down to one of its last bones; its way down = from the root's
        // head to that last bone's end
        Vector3 BandEnd(string n)
        {
            // a last bone's band: as long as the bone above it (its line runs on down the hair for the locks way, not for bands)
            var h = hm._head[n];
            if (kids[n].Count > 0 || rig.Bones[n].Tip is { } t0 && Vector3.Distance(t0, h) > 1e-4f) return ends[n];
            var d = ends[n] - h;
            float L = par[n] is { } p0 ? Vector3.Distance(h, hm._head[p0]) : 0.06f;
            return d.Length() < 1e-6f ? h : h + Vector3.Normalize(d) * Math.Clamp(L, 0.03f, 0.25f);
        }
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < S.Count; j++) idx[S[j]] = j;
        foreach (var leaf in S.Where(n => kids[n].Count == 0))
        {
            var path = new List<int>();
            for (string? c = leaf; c != null && path.Count < 256; c = par[c]) path.Insert(0, idx[c]);
            var r0 = hm._head[S[path[0]]];
            var dir = ends[leaf] - r0;
            dir = dir.Length() < 1e-6f ? down : Vector3.Normalize(dir);
            hm._paths.Add(new BandPath
            {
                Bones = path.ToArray(), Root = r0, Dir = dir,
                S0 = path.Select(j => Vector3.Dot(hm._head[S[j]] - r0, dir)).ToArray(),
                S1 = path.Select(j => Vector3.Dot(BandEnd(S[j]) - r0, dir)).ToArray(),
                Segs = path.Select((j, k) => k + 1 < path.Count ? (hm._head[S[j]], hm._head[S[path[k + 1]]]) : (hm._head[S[j]], ends[S[j]])).ToArray(),
            });
        }
        hm._udir = S.Select(n =>
        {
            var v = ends[n] - hm._head[root[n]];
            return v.Length() < 1e-6f ? down : Vector3.Normalize(v);
        }).ToArray();
        return hm;
    }

    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        var rel = p - a;
        if (l2 < 1e-12f) return rel.Length();
        float t = Math.Clamp(Vector3.Dot(rel, ab) / l2, 0f, 1f);
        return (rel - ab * t).Length();
    }

    /// <summary>The bone whose stretch is nearest a point (for weights on a missing bone).</summary>
    public string Nearest(Vector3 p)
    {
        int best = -1;
        float bd = float.MaxValue;
        foreach (var (j, a, b) in _segs)
        {
            float d = SegDist(p, a, b);
            if (d < bd) { bd = d; best = j; }
        }
        return best >= 0 ? Bones[best] : "j_kao";
    }

    /// <summary>Weights for one point (rig space): who it follows = the nearest bones (up to three, blended);
    /// how much = how far along that bone's chain it is, past the stiff stretch. The rest stays on j_kao.</summary>
    public float[] Row(Vector3 p, HairSettings s) => Row(p, s, -1);

    /// <summary>0.1.26: the bones a point may ride (BoneHair; part = the piece's part it's in, -1 none) - null = all.</summary>
    bool[]? Allowed(Vector3 p, HairSettings s, int part)
    {
        if (s.BoneHair == null || s.BoneHair.Count == 0) return null;
        int nb = Bones.Count;
        var allow = new bool[nb];
        bool mine = false;
        for (int j = 0; j < nb; j++)
        {
            if (!s.BoneHair.TryGetValue(Bones[j], out var a) || a.Length == 0) continue;
            bool on = a switch
            {
                "L" => p.X > _kao.X,                    // a character's left is +X (the _l bones' side)
                "R" => p.X <= _kao.X,
                "F" => p.Z > _kao.Z,
                "B" => p.Z <= _kao.Z,
                _ => a.StartsWith('p') && int.TryParse(a.AsSpan(1), out int n) && n == part,
            };
            if (on) { allow[j] = true; mine = true; }
        }
        if (!mine)
            for (int j = 0; j < nb; j++) allow[j] = !(s.BoneHair.TryGetValue(Bones[j], out var a) && a.Length > 0);
        return allow;
    }

    public float[] Row(Vector3 p, HairSettings s, int part)
    {
        var allow = Allowed(p, s, part);
        if (s.Bands) return BandRow(p, s, allow);
        int nb = Bones.Count;
        var D = new float[nb];
        Array.Fill(D, float.MaxValue);
        foreach (var (j, a, b) in _segs) D[j] = MathF.Min(D[j], SegDist(p, a, b));
        var W = new float[nb];
        for (int j = 0; j < nb; j++)
            W[j] = D[j] == float.MaxValue || (_reach[j] > 0 && D[j] >= _reach[j]) || (allow != null && !allow[j]) ? 0f : 1f / MathF.Pow(D[j] + Eps, Pow);   // out of a lock's reach: not it
        if (nb > TopK)
        {
            var keep = Enumerable.Range(0, nb).OrderByDescending(j => W[j]).Take(TopK).ToHashSet();
            for (int j = 0; j < nb; j++) if (!keep.Contains(j)) W[j] = 0;
        }
        float sw = W.Sum();
        var row = new float[nb + 1];
        if (sw <= 0) { row[nb] = 1; return row; }
        float tot = 0;
        for (int j = 0; j < nb; j++)
        {
            if (W[j] <= 0) continue;
            float along = Vector3.Dot(p - _rootHead[j], _udir[j]);
            float t = Math.Clamp((along * 1000f - s.StiffMm) / MathF.Max(1e-6f, s.LoosenMm), 0f, 1f);
            if (s.Curve != 1f && t > 0) t = MathF.Pow(t, s.Curve);
            row[j] = W[j] / sw * t * s.EndsFree * _follow[j] * Edge(_reach[j] > 0 ? D[j] / _reach[j] : 0f);
            tot += row[j];
        }
        row[nb] = Math.Clamp(1f - tot, 0f, 1f);       // j_kao
        return row;
    }

    /// <summary>0.1.25, bands: which lock a point belongs to = the nearest locks (up to two, blended by
    /// distance, within their reach); within a lock, how far down it the point is (along the lock's way down, not to its nearest
    /// bone) shares it out between the bones there - each bone's band reaches its own length (+ the blend) either side of its
    /// middle, so neighbours overlap; above the first bone's middle it's all the first, below the last's all the last. How much
    /// rides the lock at all = past the stiff stretch, as along the locks.</summary>
    float[] BandRow(Vector3 p, HairSettings s, bool[]? allow)
    {
        int nb = Bones.Count, np = _paths.Count;
        var row = new float[nb + 1];
        if (np == 0) { row[nb] = 1; return row; }
        // the nearest ways down (two at most, blended by distance; a lock's reach limits it)
        var wc = new float[np];
        var dc = new float[np];
        var reachC = new float[np];
        for (int c = 0; c < np; c++)
        {
            float d = float.MaxValue, r = 0;
            for (int k = 0; k < _paths[c].Segs.Length; k++)
                if (allow == null || allow[_paths[c].Bones[k]]) d = MathF.Min(d, SegDist(p, _paths[c].Segs[k].a, _paths[c].Segs[k].b));
            foreach (int j in _paths[c].Bones) r = MathF.Max(r, _reach[j]);
            dc[c] = d; reachC[c] = r;
            wc[c] = d == float.MaxValue || (r > 0 && d >= r) ? 0f : 1f / MathF.Pow(d + Eps, Pow);
        }
        if (np > 2)
        {
            var keep = Enumerable.Range(0, np).OrderByDescending(c => wc[c]).Take(2).ToHashSet();
            for (int c = 0; c < np; c++) if (!keep.Contains(c)) wc[c] = 0;
        }
        float swc = wc.Sum();
        if (swc <= 0) { row[nb] = 1; return row; }
        float blend = MathF.Max(0f, s.BlendMm) / 1000f;
        float tot = 0;
        for (int c = 0; c < np; c++)
        {
            if (wc[c] <= 0) continue;
            var path = _paths[c];
            int n = path.Bones.Length;
            float sp = Vector3.Dot(p - path.Root, path.Dir);
            // its bones' shares at that depth: each bone's band reaches its length (+ the blend) either side of its middle
            var share = new float[n];
            float ssum = 0;
            int kf = 0, kl = n - 1;                      // the first / last bone of it the point may ride
            if (allow != null)
            {
                kf = Array.FindIndex(path.Bones, j => allow[j]);
                kl = Array.FindLastIndex(path.Bones, j => allow[j]);
                if (kf < 0) continue;
            }
            for (int k = 0; k < n; k++)
            {
                if (allow != null && !allow[path.Bones[k]]) continue;
                float lo = MathF.Min(path.S0[k], path.S1[k]), hi = MathF.Max(path.S0[k], path.S1[k]);
                float mid = (lo + hi) / 2f, len = MathF.Max(hi - lo, 0.01f);
                // above the first bone's middle: all the first; below the last's: all the last
                if ((k == kf && sp <= mid) || (k == kl && sp >= mid)) { share[k] = 1f; }
                else share[k] = MathF.Max(0f, 1f - MathF.Abs(sp - mid) / (len + blend));
                ssum += share[k];
            }
            if (ssum <= 1e-6f) continue;
            float t = Math.Clamp((sp * 1000f - s.StiffMm) / MathF.Max(1e-6f, s.LoosenMm), 0f, 1f);
            if (s.Curve != 1f && t > 0) t = MathF.Pow(t, s.Curve);
            float edge = Edge(reachC[c] > 0 ? dc[c] / reachC[c] : 0f);
            for (int k = 0; k < n; k++)
            {
                if (share[k] <= 0) continue;
                int j = path.Bones[k];
                float x = wc[c] / swc * share[k] / ssum * t * s.EndsFree * _follow[j] * edge;
                row[j] += x;
                tot += x;
            }
        }
        row[nb] = Math.Clamp(1f - tot, 0f, 1f);       // j_kao
        return row;
    }

    /// <summary>A lock's hold on the hair fades out over the outer 40% of its reach (x: distance / reach).</summary>
    static float Edge(float x)
    {
        if (x <= 0.6f) return 1f;
        if (x >= 1f) return 0f;
        float u = (x - 0.6f) / 0.4f;
        return 1f - u * u * (3f - 2f * u);
    }

    /// <summary>Rows smoothed across the mesh (half and half with the neighbours' average per pass), then the head
    /// plus the 3 strongest sway bones kept, adding up to 1 - as weights.</summary>
    public Dictionary<string, float>[] Finish(float[][] rows, int[] indices, int passes)
    {
        int n = rows.Length, nb = Bones.Count + 1;
        if (passes > 0 && n > 1 && indices.Length >= 3)
        {
            var nbrs = new List<int>[n];
            for (int i = 0; i < n; i++) nbrs[i] = new List<int>(6);
            for (int k = 0; k + 2 < indices.Length; k += 3)
            {
                int a = indices[k], b = indices[k + 1], c = indices[k + 2];
                if (a >= n || b >= n || c >= n) continue;
                nbrs[a].Add(b); nbrs[a].Add(c); nbrs[b].Add(a); nbrs[b].Add(c); nbrs[c].Add(a); nbrs[c].Add(b);
            }
            for (int pass = 0; pass < passes; pass++)
            {
                var next = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (nbrs[i].Count == 0) { next[i] = r; continue; }
                    var acc = new float[nb];
                    foreach (int q in nbrs[i]) for (int j = 0; j < nb; j++) acc[j] += rows[q][j];
                    var o = new float[nb];
                    for (int j = 0; j < nb; j++) o[j] = 0.5f * r[j] + 0.5f * acc[j] / nbrs[i].Count;
                    next[i] = o;
                }
                rows = next;
            }
        }
        var res = new Dictionary<string, float>[n];
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            var keep = Enumerable.Range(0, nb - 1).Where(j => r[j] > 0).OrderByDescending(j => r[j]).Take(TopK).ToList();
            float t = keep.Sum(j => r[j]) + MathF.Max(0, r[nb - 1]);
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            if (t <= 1e-9f) { d["j_kao"] = 1f; res[i] = d; continue; }
            foreach (int j in keep) if (r[j] / t > 1e-4f) d[Bones[j]] = r[j] / t;
            float k2 = MathF.Max(0, r[nb - 1]) / t;
            if (k2 > 1e-4f || d.Count == 0) d["j_kao"] = d.Count == 0 ? 1f : k2;
            res[i] = d;
        }
        return res;
    }
}

/// <summary>A part's check: what it rides, from its weights.</summary>
public sealed class HairPartCheck
{
    public int Part, Points, Swaying;                  // points with at least 10% on hair bones that move
    public bool Rigid => Swaying == 0 && SwayAny == 0;
    public int SwayAny;                                // points on any hair bone at all
    public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);      // bones not on your character
    public readonly SortedSet<string> Still = new(StringComparer.Ordinal);        // hair bones on you the physics doesn't move
    public readonly SortedSet<string> Sets = new(StringComparer.Ordinal);         // hairstyles whose j_ex bones it uses
    public float SwayShare => Points > 0 ? (float)Swaying / Points : 0f;
}

public static class Hair
{
    /// <summary>Scales the share of a point on hair bones by k (0 = all on the head, 2 = twice as much, from its other bones).</summary>
    public static Dictionary<string, float> ScaleSway(Dictionary<string, float> w, float k)
    {
        float tot = w.Values.Sum();
        if (tot <= 1e-6f) return w;
        float sigma = w.Where(kv => HairRig.IsSway(kv.Key)).Sum(kv => kv.Value);
        if (sigma <= 1e-6f) return w;                    // nothing sways here: Make fresh or Loosen won't find bones
        float target = Math.Clamp(sigma * k, 0f, tot);
        return SetSway(w, sigma, target, tot);
    }

    /// <summary>Moves a share a (0..1) of a point's non-sway weight onto its hair bones (more sway where it already has some).</summary>
    public static Dictionary<string, float> Loosen(Dictionary<string, float> w, float a)
    {
        float tot = w.Values.Sum();
        float sigma = w.Where(kv => HairRig.IsSway(kv.Key)).Sum(kv => kv.Value);
        if (tot <= 1e-6f || sigma <= 1e-6f) return w;
        return SetSway(w, sigma, sigma + a * (tot - sigma), tot);
    }

    static Dictionary<string, float> SetSway(Dictionary<string, float> w, float sigma, float target, float tot)
    {
        var r = new Dictionary<string, float>(StringComparer.Ordinal);
        float rest = tot - sigma;
        float k = target / sigma;
        foreach (var kv in w)
        {
            if (HairRig.IsSway(kv.Key)) r[kv.Key] = kv.Value * k;
            else if (target <= sigma || rest <= 1e-6f) r[kv.Key] = kv.Value;
            else r[kv.Key] = kv.Value * Math.Max(0f, (rest - (target - sigma)) / rest);
        }
        if (target < sigma) r["j_kao"] = r.GetValueOrDefault("j_kao") + (sigma - target);   // less sway goes onto the head
        foreach (var key in r.Where(kv => kv.Value <= 1e-7f).Select(kv => kv.Key).ToList()) r.Remove(key);
        return r;
    }

    /// <summary>0.1.25, the Paint brush: a point's weights moved toward all on one bone (or, erasing, all on the head) by a.</summary>
    public static Dictionary<string, float> PaintTo(Dictionary<string, float> w, string bone, float a)
    {
        a = Math.Clamp(a, 0f, 1f);
        float tot = w.Values.Sum();
        if (tot <= 1e-6f) return new Dictionary<string, float>(StringComparer.Ordinal) { [bone] = 1f };
        var r = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (b, x) in w) r[b] = x / tot * (1f - a);
        r[bone] = r.GetValueOrDefault(bone) + a;
        foreach (var key in r.Where(kv => kv.Value <= 1e-7f).Select(kv => kv.Key).ToList()) r.Remove(key);
        return r;
    }

    /// <summary>0.1.24: a point of the hair (rig space) and its weights - where it goes once its locks are posed: each posed bone's
    /// share moves it with that bone, the rest stays (linear blend, like the game's skinning). Also the blended turn, for its
    /// normal and tangents.</summary>
    public static (Vector3 p, Matrix4x4 turn) Pose(Vector3 p, Dictionary<string, float> w, IReadOnlyDictionary<string, Matrix4x4> deltas)
    {
        float tot = 0, posed = 0;
        foreach (var x in w.Values) tot += x;
        if (tot <= 1e-6f || deltas.Count == 0) return (p, Matrix4x4.Identity);
        var acc = Vector3.Zero;
        var lin = new Matrix4x4();
        foreach (var (b, x) in w)
        {
            if (x <= 0 || !deltas.TryGetValue(b, out var d)) continue;
            float f = x / tot;
            acc += Vector3.Transform(p, d) * f;
            lin += d * f;
            posed += f;
        }
        if (posed <= 1e-6f) return (p, Matrix4x4.Identity);
        acc += p * (1f - posed);
        lin += Matrix4x4.Identity * (1f - posed);
        lin.M41 = lin.M42 = lin.M43 = 0; lin.M44 = 1;
        return (acc, lin);
    }

    /// <summary>0.1.19, locks placed by hand: the share of a point that stays off its new locks (its head share, fresh) keeps the
    /// weights it had - bones you don't have, and bones of Tailor's earlier locks, go onto the head - so hair away from every
    /// lock stays as it was (on the head, for hair with no bones of its own).</summary>
    /// <summary>Bones Tailor added to a hair (j_ex_hNNNN_tl...), as Tailor names them.</summary>
    static readonly Regex OursRx = new(@"^j_ex_h\d{4}_tl", RegexOptions.Compiled);

    public static Dictionary<string, float> KeepOwn(Dictionary<string, float> fresh, Dictionary<string, float> own, HairRig rig)
    {
        float head = fresh.GetValueOrDefault("j_kao");
        float tot = own.Values.Sum();
        if (head <= 1e-6f || tot <= 1e-6f) return fresh;
        var r = new Dictionary<string, float>(fresh, StringComparer.Ordinal);
        r.Remove("j_kao");
        foreach (var (b, x) in own)
        {
            if (x <= 0) continue;
            string to = rig.All.Contains(b) && !OursRx.IsMatch(b) ? b : "j_kao";
            r[to] = r.GetValueOrDefault(to) + head * x / tot;
        }
        return r;
    }

    /// <summary>The vanilla bone a j_ex suffix stands for (as Anamnesis maps them): ke_a / ke_b back hair upper / lower,
    /// ke_l / ke_r bangs, ke_f front (side from x: + is your left).</summary>
    public static string? VanillaFor(string rest, float x)
    {
        var r = rest.StartsWith("ke_", StringComparison.Ordinal) ? rest.Substring(3) : rest;
        var tok = r.Split('_')[0];
        return tok switch
        {
            "a" => "j_kami_a", "b" => "j_kami_b", "l" => "j_kami_f_l", "r" => "j_kami_f_r",
            "f" => x >= 0 ? "j_kami_f_l" : "j_kami_f_r",
            _ => null,
        };
    }

    /// <summary>Weights on bones you don't have moved onto ones you do: another hairstyle's j_ex bone onto the
    /// loaded hairstyle's of the same name, else its vanilla stand-in (j_kami), else the moving bone whose stretch
    /// is nearest; anything else missing onto the head.</summary>
    public static Dictionary<string, float> Retarget(Dictionary<string, float> w, HairRig rig, Vector3 pRig, HairModel? model, out bool changed)
    {
        changed = false;
        Dictionary<string, float>? res = null;
        foreach (var (b, x) in w)
        {
            if (rig.All.Contains(b)) continue;
            string to = "j_kao";
            var ex = HairRig.ExParts(b);
            if (ex != null && rig.ExSet.Length > 0 && rig.All.Contains($"j_ex_h{rig.ExSet}_{ex.Value.rest}")) to = $"j_ex_h{rig.ExSet}_{ex.Value.rest}";
            else if (ex != null && VanillaFor(ex.Value.rest, pRig.X) is { } v && rig.Moves(v)) to = v;
            else if (HairRig.IsSway(b) && model != null) to = model.Nearest(pRig);
            res ??= new Dictionary<string, float>(w, StringComparer.Ordinal);
            res.Remove(b);
            res[to] = res.GetValueOrDefault(to) + x;
            changed = true;
        }
        return res ?? w;
    }

    /// <summary>Every LOD 0 point of a hair in rig space (bent into your race when it's made for another) - where new bones go.</summary>
    public static List<Vector3> RigPoints(MdlFile m, HairRig rig)
    {
        var res = new List<Vector3>();
        foreach (int mi in m.LodMeshes(0))
        {
            var pe = m.Element(mi, VUsage.Position);
            if (pe == null) continue;
            var me = m.Meshes[mi];
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
            for (int v = 0; v < me.VertexCount; v++)
            {
                var p4 = m.ReadVec(mi, pe, v);
                var p = new Vector3(p4.X, p4.Y, p4.Z);
                if (rig.ToRig != null)
                {
                    var w = new Dictionary<string, float>(StringComparer.Ordinal);
                    if (table != null)
                        foreach (var (sl, x) in m.ReadWeights(mi, v))
                            if (x > 0 && sl < table.Count && table[sl] < m.BoneNames.Count) w[m.BoneNames[table[sl]]] = w.GetValueOrDefault(m.BoneNames[table[sl]]) + x / 255f;
                    p = rig.Point(p, w);
                }
                res.Add(p);
            }
        }
        return res;
    }

    /// <summary>What each LOD 0 part rides.</summary>
    public static List<HairPartCheck> Check(MdlFile m, HairRig rig)
    {
        var res = new List<HairPartCheck>();
        int part = -1;
        foreach (int mi in m.LodMeshes(0))
        {
            part++;
            var c = new HairPartCheck { Part = part };
            res.Add(c);
            var me = m.Meshes[mi];
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
            if (table == null) continue;
            for (int v = 0; v < me.VertexCount; v++)
            {
                float tot = 0, moving = 0, any = 0;
                foreach (var (sl, w) in m.ReadWeights(mi, v))
                {
                    if (w == 0 || sl >= table.Count || table[sl] >= m.BoneNames.Count) continue;
                    string bn = m.BoneNames[table[sl]];
                    float f = w / 255f;
                    tot += f;
                    if (!rig.All.Contains(bn)) c.Missing.Add(bn);
                    if (HairRig.ExParts(bn) is { } ex) c.Sets.Add(ex.num);
                    if (!HairRig.IsSway(bn)) continue;
                    any += f;
                    if (rig.Moves(bn)) moving += f;
                    else if (rig.All.Contains(bn)) c.Still.Add(bn);
                }
                if (tot <= 0) continue;
                c.Points++;
                if (any > 0) c.SwayAny++;
                if (moving / tot >= 0.1f) c.Swaying++;
            }
        }
        return res;
    }
}
