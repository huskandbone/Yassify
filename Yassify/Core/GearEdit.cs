// Yassify engine (shared with Tailor) - the edits: move / rotate / scale / inflate pieces, copy weights
// from the body, add (or take off) YAS weights. Every edit is worked out from
// the original file each time, so sliders never pile up rounding.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Yassify.Core;

public enum YasRegion { None, Butt, Chest, Belly, Thighs, Arms, Fingers, Toes, Groin, Other }

/// <summary>How a piece (or part) takes the body's weights (0.1.15). AsMade: the mod's own weights, with the body's jiggle
/// shares added on top (as before). Tight: near the skin, the exact weights of the skin actually under it (the piece's
/// own skin, or the other piece you're wearing) - for shorts, leggings, straps. Soft: the jiggle shares spread over the skin around each spot, so cloth hangs instead of hugging - dresses,
/// baggy pants. Same: a part goes with the whole piece.</summary>
public enum ClothFit { Same, AsMade, Tight, Soft }

public sealed class MeshEdit
{
    public Vector3 Move;                 // metres, game space (x left, y up, z forward)
    public Vector3 Rotate;               // degrees about x, y, z
    public Vector3 Scale = Vector3.One;
    public float Inflate;                // metres along the surface normal
    public bool Reweight;                // all bones from the body
    public bool NoYas;                   // leave this piece out of YAS (0.1.0; the slider replaced it)
    public float Jiggle = 1f;            // how much of the YAS jiggle this part takes: 0 = none, 1 = like the skin (0.1.15: no more than that)
    public ClothFit Fit = ClothFit.Same; // a part: its own fit, or Same = the whole piece's (YasSettings.Fit)
    public bool JiggleOff => !NoYas && Jiggle <= 0f;   // 0%: any YAS weights it has come off
    public string Follow = "";           // "" own weights, "head" rigid j_kao, or a donor: "face", "ears", "tail", "hair", "body"
    public bool FollowBends;             // false: the whole part takes the weights of the spot it touches (moves as one)
    public float Sway = 1f;              // hair: share on hair bones x this (0 = stiff on the head, 1 = as made, up to 2)
    public HairSource Source = HairSource.Same;   // hair: where its weights come from

    public bool MovesPoints => Move != Vector3.Zero || Rotate != Vector3.Zero || Scale != Vector3.One || Inflate != 0;
    public MeshEdit Clone() => (MeshEdit)MemberwiseClone();
}

/// <summary>A brush stroke from the body view: a small fix around a spot.</summary>
public enum StrokeKind { Grab, Push, FixClip, CopySkin, Smooth, Stiffen, Loosen, Paint }

public sealed class Stroke
{
    public StrokeKind Kind;
    public Vector3 Center;               // in the piece's own (edited) space
    public float Radius = 0.02f;         // metres
    public Vector3 Delta;                // Grab: how far the middle moves
    public float Amount;                 // Push: metres along the surface (minus = pull in); FixClip: gap to leave over the skin
    public float Strength = 1f;          // 0 - 1
    public int Part = -1;                // only this part (LOD 0 number), -1 = any
    public string Bone = "";             // 0.1.25, Paint: the hair bone it paints on ("j_kao": back onto the head)
    public bool Shapes => Kind is StrokeKind.Grab or StrokeKind.Push or StrokeKind.FixClip;
    public bool Hits(int part) => Part < 0 || Part == part;
    /// <summary>Smooth fall-off: 1 in the middle, 0 at the edge ((1 - t^2)^2).</summary>
    public float Falloff(Vector3 p)
    {
        float t2 = (p - Center).LengthSquared() / (Radius * Radius);
        if (t2 >= 1) return 0;
        float k = 1 - t2;
        return k * k * Strength;
    }
    public Stroke Clone() => (Stroke)MemberwiseClone();
}

public sealed class YasSettings
{
    public bool Enabled;
    public float Master = 1f;            // the whole piece: multiplies every area (0 - 1; 0.1.15)
    public ClothFit Fit = ClothFit.AsMade;   // the whole piece's fit (a part can have its own)
    public float Softness = 0.5f;        // Soft fit: how wide the jiggle is spread, 0 - 1 (SoftRadius: up to 12 cm)
    public Dictionary<YasRegion, float> Strength = new()
    {
        [YasRegion.Butt] = 1, [YasRegion.Chest] = 1, [YasRegion.Belly] = 1, [YasRegion.Thighs] = 1,
        [YasRegion.Arms] = 1, [YasRegion.Fingers] = 1, [YasRegion.Toes] = 1, [YasRegion.Groin] = 0, [YasRegion.Other] = 1,
    };
    /// <summary>Areas set from outside (0.1.13: the tummy, from the piece on the other side of the waist): the value
    /// used for that area instead of its slider x Overall x part jiggle.</summary>
    public Dictionary<YasRegion, float> Lock = new();
    public float FadeStart = 0.015f;     // full YAS up to this far from the skin
    public float FadeEnd = 0.08f;        // none beyond this
    public bool Allow8 = true;           // let a point hold 8 bones (widens 4-bone models)

    public YasSettings Clone()
    {
        var c = (YasSettings)MemberwiseClone();
        c.Strength = new Dictionary<YasRegion, float>(Strength);
        c.Lock = new Dictionary<YasRegion, float>(Lock);
        return c;
    }
}

public sealed class EditSettings
{
    public MeshEdit Whole = new();
    public Dictionary<int, MeshEdit> Meshes = new();   // by mesh number within its LOD
    public YasSettings Yas = new();
    public bool RemoveYas;
    public bool MatchSeams;                              // 0.1.9-0.1.12 geometric seam match - off (0.1.14: no seam matching at all)
    /// <summary>0.1.14: wherever this piece lies on the skin of another body piece (a legs piece's straps on the
    /// upper body), it takes that skin's weights - eased in over 2 cm past where the skin changes.</summary>
    public bool FollowOtherBody;
    public string Slot = "";                             // the piece's own body slot ("Chest", "Legs"...), for FollowOtherBody
    /// <summary>0.1.16: refit from one of the body mod's shapes to another (their model paths in the body mod; "" = none).</summary>
    public string RefitFrom = "", RefitTo = "";
    public string RefitLabel = "";                       // "SFW M (Default) -> SFW Hazelnut L (Default Hazelnut)", for the backup note
    public bool RefitOn => RefitFrom.Length > 0 && RefitTo.Length > 0 && !string.Equals(RefitFrom, RefitTo, StringComparison.OrdinalIgnoreCase);
    public HairSettings Hair = new();                    // hair: weights made fresh
    public List<Stroke> Strokes = new();                 // brush fixes, in order

    public MeshEdit For(int slot) => Meshes.TryGetValue(slot, out var e) ? e : Empty;
    static readonly MeshEdit Empty = new();

    public bool AnyTight => Yas.Fit == ClothFit.Tight || Meshes.Values.Any(m => m.Fit == ClothFit.Tight);
    public bool NeedsBody => Yas.Enabled || FollowOtherBody || AnyTight || Meshes.Values.Any(m => m.Reweight) || Whole.Reweight
                             || Strokes.Any(k => k.Kind is StrokeKind.FixClip or StrokeKind.CopySkin);
    public bool Follows => Yassify.Core.Follow.Active(Whole.Follow) || Meshes.Values.Any(m => Yassify.Core.Follow.Active(m.Follow));
    public bool Any => NeedsBody || RemoveYas || Follows || Strokes.Count > 0 || Whole.MovesPoints || Meshes.Values.Any(m => m.MovesPoints) || HairActive || RefitOn;
    /// <summary>A hair edit: a sway slider moved, weights moved or made fresh, a stiffen / loosen brush.</summary>
    public bool HairActive => Whole.Sway != 1f || Whole.Source is HairSource.Moved or HairSource.Fresh
                              || Meshes.Values.Any(m => m.Sway != 1f || m.Source is HairSource.Moved or HairSource.Fresh)
                              || Strokes.Any(k => k.Kind is StrokeKind.Stiffen or StrokeKind.Loosen or StrokeKind.Paint);
    /// <summary>What a part follows: its own setting, else the whole piece's.</summary>
    public (string what, bool bends) FollowOf(int slot)
    {
        var e = For(slot);
        return e.Follow.Length > 0 ? (e.Follow, e.FollowBends) : (Whole.Follow, Whole.FollowBends);
    }

    public EditSettings Clone()
    {
        var c = new EditSettings { Whole = Whole.Clone(), Yas = Yas.Clone(), RemoveYas = RemoveYas, MatchSeams = MatchSeams, Hair = Hair.Clone(), Strokes = Strokes.Select(k => k.Clone()).ToList(),
                                   FollowOtherBody = FollowOtherBody, Slot = Slot, RefitFrom = RefitFrom, RefitTo = RefitTo, RefitLabel = RefitLabel };
        foreach (var kv in Meshes) c.Meshes[kv.Key] = kv.Value.Clone();
        return c;
    }
}

/// <summary>Nearest-skin lookups, kept between slider moves: they only need
/// redoing when the piece moves or the body changes.</summary>
public sealed class NearestCache
{
    public string Key = "";
    public readonly Dictionary<(int mesh, int v), (float dist, Dictionary<string, float> w)> Hits = new();
    /// <summary>The Soft fit's spread weights per point, and per LOD what they were worked out for (the width, which parts).</summary>
    public readonly Dictionary<(int mesh, int v), Dictionary<string, float>> Soft = new();
    public readonly Dictionary<int, string> SoftKeys = new();
    /// <summary>Where each point lies on the piece's own skin and on the other piece's (Tight / following the other piece):
    /// the spots stay put while only sliders move (the skin's weights are read fresh each time).</summary>
    public readonly Dictionary<int, (float d, int tri, Vector3 bar)[]> OwnHits = new(), OtherHits = new();
    public object? OtherFor;
    /// <summary>The refit's move per point (0.1.16) - from the file's own positions, so moving the piece doesn't redo it.</summary>
    public string RefitKey = "";
    public object? RefitOf;                              // the file they were worked out on
    public Dictionary<int, (Vector3[] move, Quaternion[] turn)>? RefitMoves;
    public void Clear() { Hits.Clear(); Soft.Clear(); SoftKeys.Clear(); OwnHits.Clear(); OtherHits.Clear(); OtherFor = null; Key = ""; }
}

public sealed class EditResult
{
    /// <summary>Per part (LOD 0 number): what it follows, the weights it took, how far the touching spot was.</summary>
    public Dictionary<int, (string what, Dictionary<string, float> w, float dist, bool bends)> Followed = new();
    public byte[] Bytes = Array.Empty<byte>();
    public List<string> Report = new();
    public int PointsMoved, PointsReweighted, PointsWithYas, BonesAdded, TableFull, FarFromBody;
    public bool Widened;
    /// <summary>Seams with your other pieces: points that took the other piece's weights, points where this one
    /// has jiggle but the other piece has none (the seam can open there), and which pieces.</summary>
    public int SeamMatched, SeamOpen;
    /// <summary>Points that took the skin of another body piece (FollowOtherBody), e.g. straps above the waist.</summary>
    public int OtherBodyPoints;
    /// <summary>Points whose breast share the Chest slider changed (raised to the skin's, or scaled).</summary>
    public int ChestChanged;
    /// <summary>0.1.15: points that copied the skin under them (Tight), points with spread jiggle (Soft), and points that
    /// took the skin of the other piece you're wearing (Tight or following the upper / lower body).</summary>
    public int TightPoints, SoftPoints, OtherSkinPoints;
    /// <summary>0.1.16: points the refit moved (LOD 0) and the most any moved (metres).</summary>
    public int RefitPoints;
    public float RefitMost;
    /// <summary>Hair: points given fresh weights, points moved off bones you don't have, and the check of the result.</summary>
    public int HairFresh, HairMoved, HairPosed;      // HairPosed (0.1.24): points reshaped by posed locks
    public List<HairPartCheck>? HairCheck;
    public SortedSet<string> SeamWith = new(StringComparer.Ordinal), SeamOpenWith = new(StringComparer.Ordinal);
}

public static class GearEditor
{
    // YAS bone -> the regular bone it hangs from (YAS 1.0.5, c0201)
    public static readonly Dictionary<string, string> YasParent = new()
    {
        ["iv_ko_c_l"] = "j_ko_b_l",
        ["iv_kusu_c_l"] = "j_kusu_b_l",
        ["iv_naka_c_l"] = "j_naka_b_l",
        ["iv_hito_c_l"] = "j_hito_b_l",
        ["iv_ko_c_r"] = "j_ko_b_r",
        ["iv_kusu_c_r"] = "j_kusu_b_r",
        ["iv_naka_c_r"] = "j_naka_b_r",
        ["iv_hito_c_r"] = "j_hito_b_r",
        ["iv_asi_oya_a_l"] = "j_asi_e_l",
        ["iv_asi_oya_b_l"] = "iv_asi_oya_a_l",
        ["iv_asi_hito_a_l"] = "j_asi_e_l",
        ["iv_asi_hito_b_l"] = "iv_asi_hito_a_l",
        ["iv_asi_naka_a_l"] = "j_asi_e_l",
        ["iv_asi_naka_b_l"] = "iv_asi_naka_a_l",
        ["iv_asi_kusu_a_l"] = "j_asi_e_l",
        ["iv_asi_kusu_b_l"] = "iv_asi_kusu_a_l",
        ["iv_asi_ko_a_l"] = "j_asi_e_l",
        ["iv_asi_ko_b_l"] = "iv_asi_ko_a_l",
        ["iv_asi_oya_a_r"] = "j_asi_e_r",
        ["iv_asi_oya_b_r"] = "iv_asi_oya_a_r",
        ["iv_asi_hito_a_r"] = "j_asi_e_r",
        ["iv_asi_hito_b_r"] = "iv_asi_hito_a_r",
        ["iv_asi_naka_a_r"] = "j_asi_e_r",
        ["iv_asi_naka_b_r"] = "iv_asi_naka_a_r",
        ["iv_asi_kusu_a_r"] = "j_asi_e_r",
        ["iv_asi_kusu_b_r"] = "iv_asi_kusu_a_r",
        ["iv_asi_ko_a_r"] = "j_asi_e_r",
        ["iv_asi_ko_b_r"] = "iv_asi_ko_a_r",
        ["iv_nitoukin_l"] = "j_ude_a_l",
        ["iv_nitoukin_r"] = "j_ude_a_r",
        ["iv_c_mune_l"] = "j_mune_l",
        ["iv_c_mune_r"] = "j_mune_r",
        ["iv_kougan_l"] = "j_kosi",
        ["iv_kougan_r"] = "j_kosi",
        ["iv_ochinko_a"] = "j_kosi",
        ["iv_ochinko_b"] = "iv_ochinko_a",
        ["iv_ochinko_c"] = "iv_ochinko_b",
        ["iv_ochinko_d"] = "iv_ochinko_c",
        ["iv_ochinko_e"] = "iv_ochinko_d",
        ["iv_ochinko_f"] = "iv_ochinko_e",
        ["iv_kuritto"] = "j_kosi",
        ["iv_inshin_l"] = "j_kosi",
        ["iv_inshin_r"] = "j_kosi",
        ["iv_omanko"] = "j_kosi",
        ["iv_koumon"] = "j_kosi",
        ["iv_koumon_l"] = "j_kosi",
        ["iv_koumon_r"] = "j_kosi",
        ["iv_shiri_l"] = "j_kosi",
        ["iv_shiri_r"] = "j_kosi",
        ["iv_kyokin_phys_l"] = "j_sebo_c",
        ["iv_kyokin_phys_r"] = "j_sebo_c",
        ["iv_fukubu_phys"] = "j_sebo_a",
        ["iv_fukubu_phys_l"] = "j_sebo_a",
        ["iv_fukubu_phys_r"] = "j_sebo_a",
        ["iv_daitai_phys_l"] = "j_asi_a_l",
        ["iv_daitai_phys_r"] = "j_asi_a_r",
        ["iv_kintama_phys_l"] = "j_kosi",
        ["iv_kintama_phys_r"] = "j_kosi",
        ["iv_funyachin_phy_a"] = "j_kosi",
        ["iv_funyachin_phy_b"] = "iv_funyachin_phy_a",
        ["iv_funyachin_phy_c"] = "iv_funyachin_phy_b",
        ["iv_funyachin_phy_d"] = "iv_funyachin_phy_c",
        ["ya_fukubu_phys"] = "j_kosi",
        ["ya_shiri_phys_l"] = "j_kosi",
        ["ya_shiri_phys_r"] = "j_kosi",
        ["ya_daitai_phys_l"] = "j_asi_a_l",
        ["ya_daitai_phys_r"] = "j_asi_a_r",
    };

    public static bool IsYas(string bone) => bone.StartsWith("iv_") || bone.StartsWith("ya_");

    public static YasRegion Region(string b)
    {
        if (!IsYas(b)) return YasRegion.None;
        if (b.StartsWith("ya_shiri") || b.StartsWith("iv_shiri")) return YasRegion.Butt;
        if (b.StartsWith("iv_c_mune") || b.StartsWith("iv_kyokin")) return YasRegion.Chest;
        if (b.StartsWith("ya_fukubu") || b.StartsWith("iv_fukubu")) return YasRegion.Belly;
        if (b.StartsWith("ya_daitai") || b.StartsWith("iv_daitai")) return YasRegion.Thighs;
        if (b.StartsWith("iv_nitoukin")) return YasRegion.Arms;
        if (b.StartsWith("iv_asi_")) return YasRegion.Toes;
        if (b.EndsWith("_c_l") || b.EndsWith("_c_r")) return YasRegion.Fingers;
        foreach (var g in new[] { "iv_kougan", "iv_ochinko", "iv_inshin", "iv_kuritto", "iv_omanko", "iv_koumon", "iv_kintama", "iv_funyachin" })
            if (b.StartsWith(g)) return YasRegion.Groin;
        return YasRegion.Other;
    }

    /// <summary>The regular bone a YAS bone hangs from (through other YAS
    /// bones), or null if it isn't one YAS knows.</summary>
    public static string? Root(string b)
    {
        string p = b;
        int guard = 0;
        while (IsYas(p))
        {
            if (!YasParent.TryGetValue(p, out var pp) || guard++ > 8) return null;
            p = pp;
        }
        return p;
    }

    /// <summary>Weights with the YAS bones folded back into the bone each one
    /// hangs from - undoes AddYas up to 100% (past 100%, what came from other
    /// bones goes back to the YAS bone's parent). Unknown iv_/ya_ bones: taken
    /// out and the rest scaled back up.</summary>
    public static Dictionary<string, float> FoldYas(Dictionary<string, float> w)
    {
        var res = new Dictionary<string, float>();
        float lost = 0;
        foreach (var kv in w)
        {
            if (!IsYas(kv.Key)) { res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value; continue; }
            var r = Root(kv.Key);
            if (r == null) { lost += kv.Value; continue; }
            res[r] = res.GetValueOrDefault(r) + kv.Value;
        }
        float t = res.Values.Sum();
        if (t <= 1e-6f) return StripYas(w);
        return lost > 0 ? res.ToDictionary(kv => kv.Key, kv => kv.Value / t) : res;
    }

    /// <summary>Adds YAS bones the way YAS bodies do: each YAS bone takes its
    /// share out of the bone it hangs from (the breast's iv_c_mune out of
    /// j_mune, the butt bones out of j_kosi), in the same proportion the body
    /// has there - so with the YAS bones at rest the piece moves exactly as
    /// before. scale(bone) multiplies a bone's share (strength x fade).</summary>
    public static Dictionary<string, float> AddYas(Dictionary<string, float> gear, Dictionary<string, float> body, Func<string, float> scale)
    {
        float bt = body.Values.Sum();
        if (bt <= 1e-6f) return gear;
        // the body's family weight per regular bone (it + the YAS bones under it)
        var fam = new Dictionary<string, float>();
        foreach (var kv in body)
        {
            string? r = IsYas(kv.Key) ? Root(kv.Key) : kv.Key;
            if (r == null) continue;
            fam[r] = fam.GetValueOrDefault(r) + kv.Value / bt;
        }
        var take = new Dictionary<string, Dictionary<string, float>>();   // root -> yas bone -> amount
        foreach (var kv in body)
        {
            if (!IsYas(kv.Key)) continue;
            var r = Root(kv.Key);
            if (r == null || !gear.TryGetValue(r, out var g) || g <= 0) continue;
            float f = fam.GetValueOrDefault(r);
            if (f <= 1e-6f) continue;
            float x = g * (kv.Value / bt) / f * scale(kv.Key);
            if (x <= 0.5f / 255f) continue;
            if (!take.TryGetValue(r, out var d)) take[r] = d = new Dictionary<string, float>();
            d[kv.Key] = x;
        }
        if (take.Count == 0) return gear;
        var res = new Dictionary<string, float>(gear);
        var excess = new Dictionary<string, float>();    // over 100% only: what a bone's own weight couldn't cover
        foreach (var (r, d) in take)
        {
            float g = gear[r];
            float sum = d.Values.Sum();
            float k = sum > g ? g / sum : 1f;              // the parent's own weight first
            foreach (var kv in d) res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value * k;
            res[r] = g - sum * k;
            if (res[r] <= 1e-6f) res.Remove(r);
            if (sum > g + 1e-6f) excess[r] = sum - g;
        }
        // Over 100%, a spot whose parent bone is used up (the skin there is mostly on YAS
        // bones already) takes the rest from its other regular bones (hip, thigh, spine),
        // so the slider keeps adding jiggle until the whole spot rides the YAS bones.
        // At 100% and under nothing comes from here (each family can cover itself).
        float want = excess.Values.Sum();
        if (want > 1e-6f)
        {
            var pool = res.Where(kv => kv.Value > 1e-6f && !IsYas(kv.Key)).ToList();
            float have = pool.Sum(kv => kv.Value);
            if (have > 1e-6f)
            {
                float got = Math.Min(want, have);
                foreach (var kv in pool)
                {
                    float nv = kv.Value - got * (kv.Value / have);
                    if (nv <= 1e-6f) res.Remove(kv.Key); else res[kv.Key] = nv;
                }
                foreach (var (r, ex) in excess)
                {
                    var d = take[r];
                    float sum = d.Values.Sum();
                    float share = got * (ex / want);
                    foreach (var kv in d) res[kv.Key] = res.GetValueOrDefault(kv.Key) + share * (kv.Value / sum);
                }
            }
        }
        return res;
    }

    static readonly (string bone, string yas)[] Breasts = { ("j_mune_l", "iv_c_mune_l"), ("j_mune_r", "iv_c_mune_r") };
    public static bool IsBreastYas(string b) => b.StartsWith("iv_c_mune", StringComparison.Ordinal);
    static bool IsBreast(string b) => b is "j_mune_l" or "j_mune_r" || IsBreastYas(b);
    /// <summary>Where more breast share may come from: the spine the breasts sit on - never the arms, shoulders or neck,
    /// so the cloth still follows them (0.1.13: taking from the arms bent a hoodie's chest when the arms were raised).</summary>
    static readonly HashSet<string> BreastPool = new(StringComparer.Ordinal) { "j_sebo_a", "j_sebo_b", "j_sebo_c" };

    /// <summary>The breast share a point gets: scale times its base below 100%; above, a smooth curve 1 - (1 - base)^scale
    /// that rises like the slider near the edge of the breast and tops out gently at all-on-the-breast - multiplying and
    /// capping (0.1.12) made a crease line where the share hit 100% (a hoodie at 200% distorted).</summary>
    public static float BreastTarget(float baseShare, float scale) =>
        scale <= 1f ? baseShare * scale : 1f - MathF.Pow(1f - Math.Clamp(baseShare, 0f, 1f), scale);

    /// <summary>The breasts as a whole (0.1.12). The body's physics moves j_mune itself - iv_c_mune only rides along on it
    /// (the AIO's breast physics simulates j_mune_l / _r and nothing under them) - so moving weight between the two, all
    /// the Chest slider used to do, changed nothing you could see, and a piece with no j_mune got no bounce at all. Now a
    /// point's whole breast share (j_mune + iv_c_mune, per side) is at least the skin's there (faded for loose cloth: a
    /// piece weighted stiffer than the skin gets its bounce; one weighted bouncier keeps its own), times scale: less goes
    /// onto the chest (j_sebo_c, the bone the breasts hang from), more comes from the point's spine bones only (0.1.13; never
    /// the arms, shoulders or neck) along a smooth curve past 100% (BreastTarget). Added
    /// share is split between the two bones like the skin's. Returns w itself when nothing changes.</summary>
    public static Dictionary<string, float> ChestShare(Dictionary<string, float> w, Dictionary<string, float>? skin, float fade, float scale)
    {
        float tot = w.Values.Sum();
        if (tot <= 1e-6f) return w;
        float st = skin?.Values.Sum() ?? 0f;
        Dictionary<string, float>? res = null;
        foreach (var (jm, iv) in Breasts)
        {
            var cur = res ?? w;
            float gm = cur.GetValueOrDefault(jm), gi = cur.GetValueOrDefault(iv), gear = gm + gi;
            float sm = 0, si = 0;
            if (skin != null && st > 1e-6f) { sm = skin.GetValueOrDefault(jm) / st * tot; si = skin.GetValueOrDefault(iv) / st * tot; }
            float baseShare = MathF.Max(gear, (sm + si) * Math.Clamp(fade, 0f, 1f));
            if (baseShare <= 1e-6f) continue;
            float target = Math.Max(0f, BreastTarget(baseShare / tot, scale) * tot);
            if (MathF.Abs(target - gear) < 0.25f / 255f * tot) continue;
            res ??= new Dictionary<string, float>(w);
            if (target < gear)
            {
                float k = target / gear;
                if (gm > 0) res[jm] = gm * k;
                if (gi > 0) res[iv] = gi * k;
                res["j_sebo_c"] = res.GetValueOrDefault("j_sebo_c") + gear * (1 - k);
            }
            else
            {
                var pool = res.Where(kv => kv.Value > 1e-6f && BreastPool.Contains(kv.Key)).ToList();
                float have = pool.Sum(kv => kv.Value);
                float want = MathF.Min(target - gear, have);
                if (want <= 1e-6f) continue;
                foreach (var kv in pool) res[kv.Key] = kv.Value - want * (kv.Value / have);
                float rm = sm + si > 1e-6f ? sm / (sm + si) : gear > 1e-6f ? gm / gear : 1f;
                res[jm] = res.GetValueOrDefault(jm) + want * rm;
                if (want * (1 - rm) > 1e-6f) res[iv] = res.GetValueOrDefault(iv) + want * (1 - rm);
            }
        }
        if (res == null) return w;
        foreach (var key in res.Where(kv => kv.Value <= 1e-6f).Select(kv => kv.Key).ToList()) res.Remove(key);
        return res;
    }

    /// <summary>Weights with the YAS bones taken out (the rest scaled back up to
    /// 100%). A point only on YAS bones goes to the bone they hang from.</summary>
    public static Dictionary<string, float> StripYas(Dictionary<string, float> w)
    {
        var van = w.Where(kv => !IsYas(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        float t = van.Values.Sum();
        if (t > 1e-6f) return van.ToDictionary(kv => kv.Key, kv => kv.Value / t);
        var res = new Dictionary<string, float>();
        foreach (var kv in w)
        {
            string p = kv.Key;
            int guard = 0;
            while (IsYas(p) && YasParent.TryGetValue(p, out var pp) && guard++ < 8) p = pp;
            if (IsYas(p)) p = "j_kosi";
            res[p] = res.GetValueOrDefault(p) + kv.Value;
        }
        t = res.Values.Sum();
        return t > 1e-6f ? res.ToDictionary(kv => kv.Key, kv => kv.Value / t) : res;
    }

    /// <summary>Each YAS bone keeps k of its weight; the rest goes back to the bone it hangs from (a part's Jiggle on copied weights).</summary>
    public static Dictionary<string, float> ScaleYas(Dictionary<string, float> w, float k)
    {
        if (k >= 1f) return w;
        var res = new Dictionary<string, float>();
        foreach (var kv in w)
        {
            var r = IsYas(kv.Key) ? Root(kv.Key) : null;
            if (r == null) { res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value; continue; }
            if (k > 0) res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value * k;
            res[r] = res.GetValueOrDefault(r) + kv.Value * (1 - k);
        }
        return res;
    }

    public static Dictionary<string, float> Normalized(Dictionary<string, float> w)
    {
        float t = w.Values.Sum();
        return t > 1e-6f && MathF.Abs(t - 1f) > 1e-5f ? w.ToDictionary(kv => kv.Key, kv => kv.Value / t) : w;
    }

    /// <summary>(1 - f) of a plus f of b, each made to add up to 1 first.</summary>
    public static Dictionary<string, float> Blend(Dictionary<string, float> a, Dictionary<string, float> b, float f)
    {
        if (f <= 0f) return a;
        if (f >= 1f) return Normalized(b);
        var res = new Dictionary<string, float>();
        foreach (var kv in Normalized(a)) res[kv.Key] = kv.Value * (1 - f);
        foreach (var kv in Normalized(b)) res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value * f;
        return res;
    }

    /// <summary>The Soft fit's spread: 12 cm at 100%, in whole centimetres (each width is worked out once).</summary>
    public static float SoftRadius(float softness) => MathF.Round(Math.Clamp(softness, 0f, 1f) * 12f) / 100f;

    static float Fade(float d, YasSettings s)
    {
        if (d <= s.FadeStart) return 1;
        if (d >= s.FadeEnd || s.FadeEnd <= s.FadeStart) return 0;
        float u = 1 - (d - s.FadeStart) / (s.FadeEnd - s.FadeStart);
        return u * u * (3 - 2 * u);
    }

    /// <summary>Whole-number weights out of 255 for the top n bones.</summary>
    public static List<(string bone, int w)> Quantize(Dictionary<string, float> w, int n)
    {
        var top = w.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(n).ToList();
        float t = top.Sum(kv => kv.Value);
        if (t <= 0) return new();
        var raw = top.Select(kv => (kv.Key, v: kv.Value / t * 255f)).ToList();
        var q = raw.Select(x => (x.Key, w: (int)MathF.Floor(x.v), frac: x.v - MathF.Floor(x.v))).ToList();
        int left = 255 - q.Sum(x => x.w);
        foreach (var i in Enumerable.Range(0, q.Count).OrderByDescending(i => q[i].frac).ThenBy(i => i).Take(left))
            q[i] = (q[i].Key, q[i].w + 1, q[i].frac);
        return q.Where(x => x.w > 0).Select(x => (x.Key, x.w)).ToList();
    }

    static Matrix4x4 Xform(MeshEdit e, Vector3 pivot)
    {
        var rot = Matrix4x4.CreateRotationX(e.Rotate.X * MathF.PI / 180) * Matrix4x4.CreateRotationY(e.Rotate.Y * MathF.PI / 180)
                  * Matrix4x4.CreateRotationZ(e.Rotate.Z * MathF.PI / 180);
        return Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(e.Scale) * rot * Matrix4x4.CreateTranslation(pivot + e.Move);
    }

    static Vector3 Dir(Vector3 n, MeshEdit e)
    {
        // normals: inverse scale, then the rotation
        var s = new Vector3(e.Scale.X == 0 ? 1 : 1 / e.Scale.X, e.Scale.Y == 0 ? 1 : 1 / e.Scale.Y, e.Scale.Z == 0 ? 1 : 1 / e.Scale.Z);
        var v = n * s;
        var rot = Matrix4x4.CreateRotationX(e.Rotate.X * MathF.PI / 180) * Matrix4x4.CreateRotationY(e.Rotate.Y * MathF.PI / 180)
                  * Matrix4x4.CreateRotationZ(e.Rotate.Z * MathF.PI / 180);
        v = Vector3.TransformNormal(v, rot);
        float l = v.Length();
        return l > 1e-9f ? v / l : n;
    }

    /// <summary>The centre of each LOD 0 mesh (by number) and of the whole model.</summary>
    public static (Dictionary<int, Vector3> meshes, Vector3 whole) Pivots(MdlFile m)
    {
        var res = new Dictionary<int, Vector3>();
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        int slot = 0;
        foreach (int mi in m.LodMeshes(0))
        {
            var pe = m.Element(mi, VUsage.Position);
            var mlo = new Vector3(float.MaxValue); var mhi = new Vector3(float.MinValue);
            if (pe != null)
                for (int v = 0; v < m.Meshes[mi].VertexCount; v++)
                {
                    var p4 = m.ReadVec(mi, pe, v); var p = new Vector3(p4.X, p4.Y, p4.Z);
                    mlo = Vector3.Min(mlo, p); mhi = Vector3.Max(mhi, p);
                }
            res[slot++] = mlo.X <= mhi.X ? (mlo + mhi) / 2 : Vector3.Zero;
            if (mlo.X <= mhi.X) { lo = Vector3.Min(lo, mlo); hi = Vector3.Max(hi, mhi); }
        }
        return (res, lo.X <= hi.X ? (lo + hi) / 2 : Vector3.Zero);
    }

    static string GeometryKey(EditSettings s, BodySurface? body)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(body?.GetHashCode() ?? 0).Append('|');
        if (s.RefitOn) sb.Append("refit:").Append(s.RefitFrom).Append('>').Append(s.RefitTo).Append('|');
        void M(MeshEdit e) => sb.Append(e.Move).Append(e.Rotate).Append(e.Scale).Append(e.Inflate).Append(';');
        M(s.Whole);
        foreach (var kv in s.Meshes.OrderBy(k => k.Key)) { sb.Append(kv.Key).Append(':'); M(kv.Value); }
        foreach (var k in s.Strokes.Where(k => k.Shapes)) sb.Append(k.Kind).Append(k.Center).Append(k.Radius).Append(k.Delta).Append(k.Amount).Append(k.Strength).Append(k.Part).Append(';');
        return sb.ToString();
    }

    /// <summary>The shape brushes on one point (in order). n = the point's normal; body for FixClip.</summary>
    public static Vector3 Sculpt(Vector3 q, Vector3 n, int part, IReadOnlyList<Stroke> strokes, BodySurface? body)
    {
        foreach (var k in strokes)
        {
            if (!k.Shapes || !k.Hits(part)) continue;
            float f = k.Falloff(q);
            if (f <= 0) continue;
            switch (k.Kind)
            {
                case StrokeKind.Grab: q += k.Delta * f; break;
                case StrokeKind.Push: q += n * (k.Amount * f); break;
                case StrokeKind.FixClip:
                    if (body == null || body.Empty) break;
                    var (d, _, at, sn) = body.NearestDetail(q, 0.05f);
                    if (d > 0.05f) break;
                    float over = Vector3.Dot(q - at, sn);             // how far outside the skin (minus = inside)
                    if (over < k.Amount) q += sn * ((k.Amount - over) * f);
                    break;
            }
        }
        return q;
    }

    /// <summary>For tests: every point's weights through f (by bone name, 0-1), written back the way Apply writes them.</summary>
    public static byte[] ApplyWeightsForTest(byte[] original, Func<Dictionary<string, float>, Dictionary<string, float>> f)
    {
        var m = MdlFile.Read(original);
        var res = new EditResult();
        var added = new HashSet<string>();
        for (int mi = 0; mi < m.Meshes.Count; mi++)
        {
            var me = m.Meshes[mi];
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
            int slots = m.WeightSlots(mi);
            if (table == null || slots <= 0) continue;
            for (int v = 0; v < me.VertexCount; v++)
            {
                var w = WeightsOf(m, mi, v, table);
                if (w.Count == 0) continue;
                var nw = f(w);
                if (!ReferenceEquals(nw, w)) WriteNamed(m, mi, v, nw, slots, added, res);
            }
        }
        return m.Write();
    }

    static Dictionary<string, float> WeightsOf(MdlFile m, int mi, int v, List<ushort>? table)
    {
        var d = new Dictionary<string, float>();
        if (table == null) return d;
        foreach (var (sl, w) in m.ReadWeights(mi, v))
        {
            if (sl >= table.Count || w == 0) continue;
            string bn = m.BoneNames[table[sl]];
            d[bn] = d.GetValueOrDefault(bn) + w / 255f;
        }
        return d;
    }

    public static EditResult Apply(byte[] original, EditSettings s, BodySurface? body, CancellationToken ct = default, NearestCache? cache = null,
                                   IReadOnlyDictionary<string, Donor>? donors = null, SeamGuide? seams = null, HairRig? rig = null,
                                   BodySurface? otherSkin = null, RefitField? refit = null)
    {
        var res = new EditResult();
        var m = MdlFile.Read(original);
        if (!s.Any) { res.Bytes = original; return res; }
        var (pivots, wholePivot) = Pivots(m);
        bool hairOn = rig != null && s.HairActive;
        bool weights = s.NeedsBody || s.RemoveYas || s.Follows || s.Strokes.Any(k => !k.Shapes) || hairOn;
        var hairStrokes = s.Strokes.Where(k => k.Kind is StrokeKind.Stiffen or StrokeKind.Loosen or StrokeKind.Paint).ToList();
        HairModel? hairModel = null;
        if (hairOn)
        {
            // the hair's lowest point: a hanging chain's last bone drives everything down to it
            float loY = float.MaxValue;
            foreach (int mi in m.LodMeshes(0))
            {
                var pe0 = m.Element(mi, VUsage.Position);
                if (pe0 == null) continue;
                for (int v = 0; v < m.Meshes[mi].VertexCount; v++) loY = MathF.Min(loY, m.ReadVec(mi, pe0, v).Y);
            }
            hairModel = HairModel.Build(rig!, loY == float.MaxValue ? 0f : loY);
            if (hairModel == null) res.Report.Add("No hair bones on your character that move - nothing for the hair to sway on.");
        }
        // where this piece sits on another piece you're wearing (found on the original positions)
        var spots = weights && s.MatchSeams && seams != null && !seams.Empty ? SeamSpots.Find(m, seams) : null;
        if (spots != null && spots.Count == 0) spots = null;
        var shapeStrokes = s.Strokes.Where(k => k.Shapes).ToList();
        var weightStrokes = s.Strokes.Where(k => k.Kind == StrokeKind.CopySkin).ToList();
        var smoothStrokes = s.Strokes.Where(k => k.Kind == StrokeKind.Smooth).ToList();
        // rigid "follows" weights per part, worked out on LOD 0 and reused for the other LODs
        var rigid = new Dictionary<int, Dictionary<string, float>?>();
        Donor? DonorFor(string what) =>
            what == Follow.Body ? (body == null || body.Empty ? null : new Donor { Surface = body, Label = "body" })
            : donors != null && donors.TryGetValue(what, out var dn) && !dn.Surface.Empty ? dn : null;
        if (s.NeedsBody && (body == null || body.Empty))
            throw new MdlException("Pick the body to take weights from first (My body tab).");
        if (s.Yas.Enabled && s.Yas.Allow8)
        {
            bool four = false;
            for (int mi = 0; mi < m.Meshes.Count; mi++) if (m.WeightSlots(mi) == 4) four = true;
            if (four)
            {
                if (m.WidenTo8(out var why)) res.Widened = true;
                else res.Report.Add("Kept 4 bones per point (" + why + ").");
            }
        }
        var wholeX = Xform(s.Whole, wholePivot);
        cache ??= new NearestCache();
        string gk = GeometryKey(s, body);
        if (cache.Key != gk) { cache.Clear(); cache.Key = gk; }
        if (!ReferenceEquals(cache.OtherFor, otherSkin)) { cache.OtherHits.Clear(); cache.OtherFor = otherSkin; }
        // the refit (0.1.16): every point's move to the other body shape, worked out on the file's own positions - it comes first,
        // and every other edit (move, puff out, brushes, the skin lookups) works on the refitted piece
        Dictionary<int, (Vector3[] move, Quaternion[] turn)>? rf = null;
        if (s.RefitOn && refit != null)
        {
            if (cache.RefitKey != refit.Key || cache.RefitMoves == null || !ReferenceEquals(cache.RefitOf, original))
            {
                cache.RefitMoves = refit.ForModel(m, ct); cache.RefitKey = refit.Key; cache.RefitOf = original;
            }
            rf = cache.RefitMoves;
        }
        else if (s.RefitOn) throw new MdlException("The body shapes for the refit are still loading - a moment.");
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        var addedBones = new HashSet<string>();
        int startBones = m.BoneNames.Count;

        // 0.1.15: the piece's own skin goes first, so a Tight part can copy the weights it ends up with
        string MatOf(int mi) => m.Meshes[mi].MaterialIndex < m.MaterialNames.Count ? m.MaterialNames[m.Meshes[mi].MaterialIndex] : "";
        bool anyTight = s.AnyTight;
        BodySurface? ownSkin = null;
        bool ownSkinTried = false;
        ClothFit FitOf(int part, int mi) => BodySurface.IsSkinMaterial(MatOf(mi)) ? ClothFit.AsMade
            : s.For(part).Fit != ClothFit.Same ? s.For(part).Fit : s.Yas.Fit == ClothFit.Same ? ClothFit.AsMade : s.Yas.Fit;
        // where a part's points end up (moved, turned, puffed out, brushed) - as the loop below writes them
        Vector3[] MovedPoints(int mi, int part)
        {
            var e0 = s.For(part);
            var pv0 = pivots.TryGetValue(part, out var pvv) ? pvv : wholePivot;
            bool sculpt0 = shapeStrokes.Any(k => k.Hits(part));
            var rf0 = rf != null && rf.TryGetValue(mi, out var rfm) ? rfm : default;
            bool moves0 = e0.MovesPoints || s.Whole.MovesPoints || sculpt0 || rf0.move != null;
            var meshX0 = Xform(e0, pv0);
            var pe0 = m.Element(mi, VUsage.Position);
            var ne0 = m.Element(mi, VUsage.Normal);
            var res0 = new Vector3[m.Meshes[mi].VertexCount];
            if (pe0 == null) return res0;
            for (int v = 0; v < res0.Length; v++)
            {
                var p4 = m.ReadVec(mi, pe0, v);
                var p = new Vector3(p4.X, p4.Y, p4.Z);
                if (rf0.move != null) p += rf0.move[v];
                if (moves0)
                {
                    var q = Vector3.Transform(Vector3.Transform(p, meshX0), wholeX);
                    Vector3 nn0 = Vector3.UnitY;
                    if (ne0 != null)
                    {
                        var n4 = m.ReadVec(mi, ne0, v);
                        var n = ne0.Type == 8 ? new Vector3(n4.X, n4.Y, n4.Z) * 2 - Vector3.One : new Vector3(n4.X, n4.Y, n4.Z);
                        if (rf0.turn != null) n = Vector3.Transform(n, rf0.turn[v]);
                        nn0 = Dir(Dir(n, e0), s.Whole);
                        if (e0.Inflate != 0 || s.Whole.Inflate != 0) q += nn0 * (e0.Inflate + s.Whole.Inflate);
                    }
                    if (sculpt0) q = Sculpt(q, nn0, part, shapeStrokes, body);
                    p = q;
                }
                res0[v] = p;
            }
            return res0;
        }
        for (int l = 0; l < m.LodCount; l++)
        {
            var lodMeshes = m.LodMeshes(l).ToList();
            // Soft (0.1.15): every soft point's skin weights, spread over the cloth around it - across all the soft parts at
            // once, so a skirt cut into front and back pieces spreads over the join too. The cloth bridges the gaps the skin
            // has (between the legs, the butt's crease), so it doesn't take one cheek on one side of the crease and the other
            // past it. Kept until the piece moves (only the width and which parts are soft count).
            var softParts = s.Yas.Enabled && body != null && !body.Empty && SoftRadius(s.Yas.Softness) > 0f
                ? Enumerable.Range(0, lodMeshes.Count).Where(k => FitOf(k, lodMeshes[k]) == ClothFit.Soft && m.Meshes[lodMeshes[k]].VertexCount > 0
                                                                   && !Follow.Active(s.FollowOf(k).what)).ToList()
                : new List<int>();
            if (softParts.Count > 0)
            {
                string sk = $"{l}|{SoftRadius(s.Yas.Softness)}|{string.Join(",", softParts)}";
                if (!cache.SoftKeys.TryGetValue(l, out var had) || had != sk)
                {
                    var pos = new List<Vector3>(); var wts = new List<Dictionary<string, float>>(); var at = new List<(int, int)>();
                    foreach (int k in softParts)
                    {
                        int mi = lodMeshes[k];
                        var mp = MovedPoints(mi, k);
                        var found = new (float d, int tri, Vector3 bar)[mp.Length];
                        System.Threading.Tasks.Parallel.For(0, mp.Length, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, v => found[v] = body!.NearestTri(mp[v], 0.3f));
                        for (int v = 0; v < mp.Length; v++)
                        {
                            if (found[v].tri < 0) continue;
                            pos.Add(mp[v]); wts.Add(body!.WeightsAt(found[v].tri, found[v].bar)); at.Add((mi, v));
                        }
                    }
                    var spread = BodySurface.Spread(pos, wts, SoftRadius(s.Yas.Softness));
                    foreach (int k in softParts) { int mi = lodMeshes[k]; for (int v = 0; v < m.Meshes[mi].VertexCount; v++) cache.Soft.Remove((mi, v)); }
                    for (int i = 0; i < at.Count; i++) cache.Soft[at[i]] = spread[i];
                    cache.SoftKeys[l] = sk;
                }
            }
            var order = anyTight ? Enumerable.Range(0, lodMeshes.Count).OrderBy(k => BodySurface.IsSkinMaterial(MatOf(lodMeshes[k])) ? 0 : 1).ThenBy(k => k).ToList()
                                 : Enumerable.Range(0, lodMeshes.Count).ToList();      // (the file's order otherwise: the same bytes as before)
            foreach (int part in order)
            {
                int mi = lodMeshes[part];
                bool skinPart = BodySurface.IsSkinMaterial(MatOf(mi));
                if (l == 0 && !skinPart && anyTight && weights && !ownSkinTried)
                {
                    // the skin parts are done (they come first): what they are now is what's drawn under the cloth
                    ownSkinTried = true;
                    var os = new BodySurface();
                    if (os.Add(m, "this piece's skin", (_, mat, _) => BodySurface.IsSkinMaterial(mat)) > 0) { os.Build(); if (!os.Empty) ownSkin = os; }
                }
                ct.ThrowIfCancellationRequested();
                var e = s.For(part);
                var pivot = pivots.TryGetValue(part, out var pv) ? pv : wholePivot;
                int slot = part + 1;                             // (below, slot - 1 is the part's number)
                var fit = FitOf(part, mi);
                var me = m.Meshes[mi];
                var pe = m.Element(mi, VUsage.Position);
                if (pe == null) continue;
                bool sculpt = shapeStrokes.Any(k => k.Hits(slot - 1));
                var rfMesh = rf != null && rf.TryGetValue(mi, out var rfmm) ? rfmm : default;
                bool moves = e.MovesPoints || s.Whole.MovesPoints || sculpt || rfMesh.move != null;
                var meshX = Xform(e, pivot);
                var ne = m.Element(mi, VUsage.Normal);
                var dirEls = new List<VElement>();
                foreach (var el in m.Meshes[mi].Elements)
                    if ((el.Usage == (byte)VUsage.Normal || el.Usage == (byte)VUsage.Binormal || el.Usage == (byte)VUsage.Flow)
                        && (el.Type == 2 || el.Type == 3 || el.Type == 14 || el.Type == 8)) dirEls.Add(el);
                var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
                int slots = m.WeightSlots(mi);
                var (fwhat, fbends) = s.FollowOf(slot - 1);
                bool follow = Follow.Active(fwhat);
                bool doWeights = weights && table != null && slots > 0 && (follow || !(s.Yas.Enabled && e.NoYas && !e.Reweight && !s.Whole.Reweight && !s.RemoveYas));
                Dictionary<string, float>? followW = null;           // rigid: every point
                Dictionary<string, float>[]? followPer = null;       // bends: per point
                if (follow && doWeights && me.VertexCount > 0)
                {
                    if (fwhat == Follow.Head) { followW = new Dictionary<string, float> { ["j_kao"] = 1f }; if (l == 0) res.Followed[slot - 1] = (fwhat, followW, 0, false); }
                    else if (!fbends && l > 0 && rigid.TryGetValue(slot - 1, out var rw)) followW = rw;
                    else
                    {
                        var dn = DonorFor(fwhat);
                        if (dn == null)
                        {
                            if (l == 0) res.Report.Add($"Part {slot}: follows {fwhat}, but that model isn't loaded - kept its own weights.");
                            if (l == 0) rigid[slot - 1] = null;
                        }
                        else
                        {
                            // the part's points as edited, bent into the followed model's space
                            var pts = new Vector3[me.VertexCount];
                            for (int v = 0; v < me.VertexCount; v++)
                            {
                                var p4 = m.ReadVec(mi, pe, v);
                                var p = Vector3.Transform(Vector3.Transform(new Vector3(p4.X, p4.Y, p4.Z), meshX), wholeX);
                                if (dn.ToSurface != null) p = dn.ToSurface(p, WeightsOf(m, mi, v, table));
                                pts[v] = p;
                            }
                            if (!fbends)
                            {
                                var (aw, ad, _) = Follow.Anchor(pts, dn.Surface);
                                followW = aw.Count > 0 ? aw : null;
                                if (l == 0)
                                {
                                    rigid[slot - 1] = followW;
                                    if (followW != null) res.Followed[slot - 1] = (fwhat, followW, ad, false);
                                    else res.Report.Add($"Part {slot}: nothing of the {dn.Label} within 25 cm - kept its own weights.");
                                }
                            }
                            else
                            {
                                var per = new Dictionary<string, float>[pts.Length];
                                var dists = new float[pts.Length];
                                System.Threading.Tasks.Parallel.For(0, pts.Length, v =>
                                {
                                    var (hd, hw, _) = dn.Surface.Nearest(pts[v], 0.25f);
                                    per[v] = Follow.Clean(hw, 0.005f); dists[v] = hd;
                                });
                                followPer = per;
                                if (l == 0)
                                {
                                    var sum = new Dictionary<string, float>();
                                    foreach (var w in per) foreach (var kv in w) sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + kv.Value;
                                    res.Followed[slot - 1] = (fwhat, Follow.Clean(sum), dists.Length > 0 ? dists.Min() : 0, true);
                                }
                            }
                        }
                    }
                    if (followW == null && followPer == null) follow = false;
                }

                // hair: where this part's weights come from; made fresh = worked out for every point first
                // (on its edited place, bent into your race), smoothed across the mesh
                var hsrc = HairSource.Own;
                Dictionary<string, float>[]? autoW = null;
                if (hairOn && doWeights)
                {
                    hsrc = e.Source != HairSource.Same ? e.Source : (s.Whole.Source == HairSource.Same ? HairSource.Own : s.Whole.Source);
                    if (hsrc == HairSource.Fresh && hairModel != null && me.VertexCount > 0)
                    {
                        var rows = new float[me.VertexCount][];
                        for (int v = 0; v < me.VertexCount; v++)
                        {
                            var p4 = m.ReadVec(mi, pe, v);
                            var q = Vector3.Transform(Vector3.Transform(new Vector3(p4.X, p4.Y, p4.Z), meshX), wholeX);
                            rows[v] = hairModel.Row(rig!.Point(q, WeightsOf(m, mi, v, table)), s.Hair, part);
                        }
                        autoW = hairModel.Finish(rows, Array.ConvertAll(m.ReadIndices(mi), x => (int)x), s.Hair.Smooth);
                        if (rig!.KeepOwn)
                            for (int v = 0; v < me.VertexCount; v++) autoW[v] = Hair.KeepOwn(autoW[v], WeightsOf(m, mi, v, table), rig);
                        if (l == 0) res.HairFresh += me.VertexCount;
                    }
                }
                // 0.1.24: posed locks reshape the hair riding them (its fresh weights say how much)
                bool pose = autoW != null && rig != null && rig.PoseDelta.Count > 0;
                if (pose) moves = true;

                // look up the skin under every point at once (in parallel) when
                // they'll be needed and aren't cached - the transform is applied
                // to the lookups the same way as below
                bool tightHere = doWeights && !follow && fit == ClothFit.Tight;
                bool softHere = doWeights && !follow && softParts.Contains(part);
                bool otherHere = doWeights && !follow && !skinPart && otherSkin != null && (s.FollowOtherBody || tightHere);
                bool needHits = doWeights && !follow && (s.Yas.Enabled && !e.NoYas && !e.JiggleOff && s.Yas.Master > 0f || e.Reweight || s.Whole.Reweight || tightHere);
                bool fillCache = needHits && !cache.Hits.ContainsKey((mi, 0)) && me.VertexCount > 0;
                bool needOwn = tightHere && ownSkin != null && !cache.OwnHits.ContainsKey(mi);
                bool needOther = otherHere && !cache.OtherHits.ContainsKey(mi);
                Vector3[]? ptsAll = null;
                if (me.VertexCount > 0 && (fillCache || needOwn || needOther)) ptsAll = MovedPoints(mi, part);
                if (fillCache && ptsAll != null)
                {
                    var hits = new (float, Dictionary<string, float>)[ptsAll.Length];
                    System.Threading.Tasks.Parallel.For(0, ptsAll.Length, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, v =>
                    {
                        var (hd, ht, hb) = body!.NearestTri(ptsAll[v], 0.3f);
                        hits[v] = (hd, ht >= 0 ? body.WeightsAt(ht, hb) : new Dictionary<string, float>());
                    });
                    for (int v = 0; v < ptsAll.Length; v++) cache.Hits[(mi, v)] = hits[v];
                }
                // Tight / following the other piece: the skin actually drawn there - this piece's own (as edited, above) and
                // the other body piece you're wearing - looked up for every point at once
                if (ptsAll != null && (needOwn || needOther))
                {
                    var oh = needOwn ? new (float, int, Vector3)[ptsAll.Length] : null;
                    var xh = needOther ? new (float, int, Vector3)[ptsAll.Length] : null;
                    System.Threading.Tasks.Parallel.For(0, ptsAll.Length, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, v =>
                    {
                        if (oh != null) oh[v] = ownSkin!.NearestTri(ptsAll[v], 0.3f);
                        if (xh != null) xh[v] = otherSkin!.NearestTri(ptsAll[v], 0.3f);
                    });
                    if (oh != null) cache.OwnHits[mi] = oh;
                    if (xh != null) cache.OtherHits[mi] = xh;
                }
                var ownHit = tightHere && ownSkin != null && cache.OwnHits.TryGetValue(mi, out var ohc) ? ohc : null;
                var otherHit = otherHere && cache.OtherHits.TryGetValue(mi, out var xhc) ? xhc : null;

                for (int v = 0; v < me.VertexCount; v++)
                {
                    var p4 = m.ReadVec(mi, pe, v);
                    var p = new Vector3(p4.X, p4.Y, p4.Z);
                    var p0 = p;                                  // where it was in the file (seams are found there)
                    if (moves)
                    {
                        Vector3 n = Vector3.Zero;
                        if (ne != null) { var n4 = m.ReadVec(mi, ne, v); n = ne.Type == 8 ? new Vector3(n4.X, n4.Y, n4.Z) * 2 - Vector3.One : new Vector3(n4.X, n4.Y, n4.Z); }
                        var turnV = Quaternion.Identity;
                        if (rfMesh.move != null)
                        {
                            var mvv = rfMesh.move[v];
                            p += mvv; turnV = rfMesh.turn[v];
                            if (ne != null) n = Vector3.Transform(n, turnV);
                            if (l == 0 && mvv.LengthSquared() > 1e-8f) { res.RefitPoints++; res.RefitMost = MathF.Max(res.RefitMost, mvv.Length()); }
                        }
                        var q = Vector3.Transform(Vector3.Transform(p, meshX), wholeX);
                        var nn = ne != null ? Dir(Dir(n, e), s.Whole) : Vector3.UnitY;
                        if (e.Inflate != 0 || s.Whole.Inflate != 0) q += nn * (e.Inflate + s.Whole.Inflate);
                        if (sculpt) q = Sculpt(q, nn, slot - 1, shapeStrokes, body);
                        var poseTurn = Matrix4x4.Identity;
                        if (pose)
                        {
                            // posed in your shape (rig space), the move carried back to the piece's
                            var qr = rig!.Point(q, WeightsOf(m, mi, v, table));
                            var (pq, pt) = Hair.Pose(qr, autoW![v], rig.PoseDelta);
                            q += pq - qr;
                            poseTurn = pt;
                            if (l == 0 && (pq - qr).LengthSquared() > 1e-8f) res.HairPosed++;
                        }
                        m.WriteVec(mi, pe, v, new Vector4(q, p4.W));
                        foreach (var de in dirEls)
                        {
                            var d4 = m.ReadVec(mi, de, v);
                            var d = de.Type == 8 ? new Vector3(d4.X, d4.Y, d4.Z) * 2 - Vector3.One : new Vector3(d4.X, d4.Y, d4.Z);
                            if (d.LengthSquared() < 1e-12f) continue;
                            if (!turnV.IsIdentity) d = Vector3.Transform(d, turnV);
                            // normals take the inverse scale; tangents the scale itself
                            Vector3 r;
                            if (de.Usage == (byte)VUsage.Normal) r = Dir(Dir(d, e), s.Whole);
                            else
                            {
                                var t1 = Vector3.TransformNormal(d, Matrix4x4.CreateScale(e.Scale) * RotOnly(e));
                                var t2 = Vector3.TransformNormal(t1, Matrix4x4.CreateScale(s.Whole.Scale) * RotOnly(s.Whole));
                                float tl = t2.Length(); r = tl > 1e-9f ? t2 / tl : d;
                            }
                            if (pose && !poseTurn.IsIdentity)
                            {
                                var rr = Vector3.TransformNormal(r, poseTurn);
                                if (rr.LengthSquared() > 1e-12f) r = Vector3.Normalize(rr);
                            }
                            var w4 = de.Type == 8 ? new Vector4((r + Vector3.One) / 2, d4.W) : new Vector4(r, d4.W);
                            m.WriteVec(mi, de, v, w4);
                        }
                        p = q;
                        lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
                        res.PointsMoved++;
                    }
                    if (!doWeights) continue;

                    // current weights by bone name
                    var cur = new Dictionary<string, float>();
                    foreach (var (sl, w) in m.ReadWeights(mi, v))
                    {
                        if (sl >= table!.Count) continue;
                        string bn = m.BoneNames[table[sl]];
                        cur[bn] = cur.GetValueOrDefault(bn) + w / 255f;
                    }
                    if (follow)
                    {
                        var fw = followW ?? followPer![v];
                        if (fw.Count == 0) continue;
                        WriteW(fw);
                        continue;
                    }
                    if (!weights) continue;
                    if (cur.Count == 0 && autoW == null) continue;
                    bool hadYas = cur.Keys.Any(IsYas);
                    Dictionary<string, float> baseW = cur;
                    float dist = 0; Dictionary<string, float>? donor = null;
                    bool reweight = e.Reweight || s.Whole.Reweight;
                    bool yas = s.Yas.Enabled && !e.NoYas && !e.JiggleOff && s.Yas.Master > 0f;
                    bool yasOff = s.RemoveYas || (s.Yas.Enabled && (e.JiggleOff || s.Yas.Master <= 0f));
                    if (reweight || yas || tightHere)
                    {
                        if (!cache.Hits.TryGetValue((mi, v), out var hit))
                        {
                            var (hd, ht, hb) = body!.NearestTri(p, 0.3f);
                            cache.Hits[(mi, v)] = hit = (hd, ht >= 0 ? body.WeightsAt(ht, hb) : new Dictionary<string, float>());
                        }
                        (dist, donor) = hit;
                        if (donor.Count == 0) { res.FarFromBody++; donor = null; }
                    }
                    // a YAS bone's share: its area's slider x Overall (x this part's Jiggle, unless the weights are copied whole)
                    float Slider(string b, bool withPart)
                    {
                        if (s.Yas.Lock.TryGetValue(Region(b), out var lk)) return lk;
                        return Math.Min(1f, s.Yas.Strength.GetValueOrDefault(Region(b), 1f)) * Math.Min(1f, s.Yas.Master) * (withPart ? Math.Min(1f, e.Jiggle) : 1f);
                    }
                    // the breasts: the Chest slider (x this part's Jiggle) - not Overall, so turning a dress's jiggle down doesn't
                    // shrink how it follows your bust size
                    float chestScale = s.Yas.Lock.TryGetValue(YasRegion.Chest, out var lkc) ? lkc
                                     : Math.Clamp(s.Yas.Strength.GetValueOrDefault(YasRegion.Chest, 1f), 0f, 1f) * Math.Clamp(e.Jiggle, 0f, 1f);
                    bool changed = false, countedYas = false;
                    if (reweight && donor != null)
                    {
                        float t = donor.Values.Sum();
                        if (t > 1e-6f) { baseW = FoldYas(donor.ToDictionary(kv => kv.Key, kv => kv.Value / t)); changed = true; res.PointsReweighted++; }
                    }
                    // following the other body piece (0.1.14): how far onto its skin this point lies - 0 where the body pieces meet,
                    // eased in over 2 cm. 0.1.15: what it takes there is that skin's weights exactly (the piece you're wearing,
                    // else the body's), mixed in at the end - after this piece's sliders, which used to pull the straps off the skin
                    float fo = 0; Dictionary<string, float>? otherW = null;
                    if (s.FollowOtherBody && !skinPart && s.Slot.Length > 0 && body != null && body.SourceSlot.Count > 0)
                    {
                        var (d0, w0, src0) = body.NearestFrom(p, 0.3f);
                        if (src0 >= 0 && body.SourceSlot.TryGetValue(src0, out var sl0) && sl0 != s.Slot && w0.Count > 0)
                        {
                            var (dOwn, _, _) = body.NearestFrom(p, 0.3f, k => body.SourceSlot.TryGetValue(k, out var sk) && sk == s.Slot);
                            float margin = (float.IsFinite(dOwn) && dOwn < 0.3f ? dOwn : 0.3f) - d0;
                            float u = Math.Clamp(margin / 0.02f, 0f, 1f);
                            fo = u * u * (3f - 2f * u);
                            if (fo > 0)
                            {
                                if (otherHit != null && otherHit[v].tri >= 0 && otherHit[v].d < 0.1f)
                                {
                                    otherW = otherSkin!.WeightsAt(otherHit[v].tri, otherHit[v].bar);
                                    if (l == 0) res.OtherSkinPoints++;
                                }
                                else otherW = w0;
                            }
                        }
                    }
                    if ((yas || yasOff) && baseW.Keys.Any(IsYas)) { baseW = FoldYas(baseW); changed = true; }
                    // near a seam with another piece the sliders ease back to 100% (like the skin), so a piece
                    // edited first meets the other at the skin's weights; one that has YAS is copied below
                    var (fSeam, kSeam) = spots != null ? spots.Near(p0) : (0f, -1);
                    // Soft (0.1.15): the jiggle shares from the skin under the cloth around the spot, not the spot alone (see above)
                    var yasDonor = donor;
                    if (softHere && donor != null && cache.Soft.TryGetValue((mi, v), out var spreadW) && spreadW.Count > 0)
                    {
                        yasDonor = spreadW;
                        if (l == 0) res.SoftPoints++;
                    }
                    if (yas && yasDonor != null)
                    {
                        float f = Fade(dist, s.Yas);
                        if (f > 0)
                        {
                            var nw = AddYas(baseW, yasDonor, b =>
                            {
                                if (IsBreastYas(b)) return f;           // the breast's share is set as a whole below (ChestShare)
                                float x = Slider(b, true);
                                return (x + (1f - x) * fSeam) * f;
                            });
                            if (nw.Keys.Any(IsYas))
                            {
                                baseW = nw;
                                changed = true;
                                res.PointsWithYas++;
                                countedYas = true;
                            }
                        }
                    }
                    // the breasts as a whole: the Chest slider (and Overall / part jiggle) set how much of the skin's breast
                    // bounce the point gets - see ChestShare (0.1.12)
                    // 0.1.14: never more than the skin's (scale 1): the breast bone carries the breasts' SIZE as well as
                    // their bounce, so going over made them bigger. Their bounce is the physics' (Physics tab) or the game's per-item switch (BreastPhysics).
                    // 0.1.15: the Chest slider is back, 0 - 100% - under 100% a point
                    // follows the breasts less, their bounce and their size alike (the same bone carries both)
                    if (yas)
                    {
                        var cw = ChestShare(baseW, donor, donor != null ? Fade(dist, s.Yas) : 0f, chestScale);
                        if (!ReferenceEquals(cw, baseW))
                        {
                            baseW = cw;
                            changed = true;
                            res.ChestChanged++;
                            if (!countedYas && cw.Keys.Any(IsYas)) res.PointsWithYas++;
                        }
                    }
                    // Tight (0.1.15): near the skin a point takes the weights of the
                    // skin actually drawn there - this piece's own skin as edited above, or the other piece you're wearing - exactly,
                    // so cloth and skin move as one. Where that skin isn't there (cut away under the cloth) it takes the body's,
                    // with this piece's sliders. Eased back to the weights above over the loose-cloth fade.
                    if (tightHere && !skinPart)
                    {
                        float dc = float.MaxValue;
                        Dictionary<string, float>? copy = null;
                        bool fromOther = false;
                        if (ownHit != null && ownHit[v].tri >= 0) { dc = ownHit[v].d; copy = ownSkin!.WeightsAt(ownHit[v].tri, ownHit[v].bar); }
                        if (otherHit != null && otherHit[v].tri >= 0 && otherHit[v].d < dc) { dc = otherHit[v].d; copy = otherSkin!.WeightsAt(otherHit[v].tri, otherHit[v].bar); fromOther = true; }
                        if (copy != null && donor != null && dc > dist + 0.01f) { copy = null; fromOther = false; }
                        if (copy == null && donor != null)
                        {
                            dc = dist;
                            var bw = FoldYas(Normalized(donor));
                            if (s.Yas.Enabled && s.Yas.Master > 0f)
                            {
                                bw = AddYas(bw, donor, b => IsBreastYas(b) ? 1f : Slider(b, false));
                                bw = ChestShare(bw, donor, 1f, Math.Clamp(s.Yas.Strength.GetValueOrDefault(YasRegion.Chest, 1f), 0f, 1f));
                            }
                            copy = bw;
                        }
                        if (copy != null && copy.Count > 0)
                        {
                            float ft = Fade(dc, s.Yas);
                            if (ft > 0)
                            {
                                var cw2 = Normalized(copy);
                                if (s.RemoveYas || e.JiggleOff) cw2 = FoldYas(cw2);
                                else if (e.Jiggle < 1f) cw2 = ScaleYas(cw2, Math.Max(0f, e.Jiggle));
                                baseW = Blend(baseW, cw2, ft);
                                changed = true;
                                if (l == 0) { res.TightPoints++; if (fromOther) res.OtherSkinPoints++; }
                            }
                        }
                    }
                    if (fo > 0 && otherW != null && otherW.Count > 0)
                    {
                        baseW = Blend(baseW, otherW, fo);
                        changed = true;
                        if (l == 0) res.OtherBodyPoints++;
                    }
                    if (hairOn)
                    {
                        if (autoW != null) { baseW = autoW[v]; changed = true; }
                        else if (hsrc == HairSource.Moved)
                        {
                            var moved = Hair.Retarget(baseW, rig!, rig!.Point(p, baseW), hairModel, out bool ch);
                            if (ch) { baseW = moved; changed = true; if (l == 0) res.HairMoved++; }
                        }
                        float sway = s.Whole.Sway * e.Sway;
                        if (MathF.Abs(sway - 1f) > 1e-4f) { baseW = Hair.ScaleSway(baseW, sway); changed = true; }
                        foreach (var k in hairStrokes)
                        {
                            if (!k.Hits(slot - 1)) continue;
                            float f = k.Falloff(p);
                            if (f <= 0) continue;
                            if (k.Kind == StrokeKind.Paint)
                            {
                                // 0.1.25: onto the painted bone (a bone you have, else nothing), a quarter of the way each stamp
                                string pb = k.Bone.Length > 0 ? k.Bone : "j_kao";
                                if (pb != "j_kao" && !rig!.Bones.ContainsKey(pb)) continue;
                                baseW = Hair.PaintTo(baseW, pb, 0.25f * f);
                            }
                            else baseW = k.Kind == StrokeKind.Stiffen ? Hair.ScaleSway(baseW, 1f - 0.25f * f) : Hair.Loosen(baseW, 0.25f * f);
                            changed = true;
                        }
                    }
                    foreach (var k in weightStrokes)
                    {
                        if (!k.Hits(slot - 1) || body == null || body.Empty) continue;
                        float f = k.Falloff(p);
                        if (f <= 0) continue;
                        var (sd, sw, _) = body.Nearest(p, 0.1f);
                        float st = sw.Values.Sum();
                        if (sd > 0.1f || st <= 1e-6f) continue;
                        var mix = new Dictionary<string, float>();
                        foreach (var kv in baseW) mix[kv.Key] = kv.Value * (1 - f);
                        foreach (var kv in sw) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + kv.Value / st * f;
                        baseW = mix;
                        changed = true;
                    }
                    // the seam itself: the other piece's weights when it has YAS there (eased in around it)
                    if (kSeam >= 0 && fSeam > 0 && seams!.HasYas(kSeam))
                    {
                        baseW = SeamSpots.Mix(baseW, seams.Weights(kSeam), fSeam);
                        changed = true;
                        if (l == 0 && fSeam >= 0.999f) { res.SeamMatched++; res.SeamWith.Add(seams.LabelOf(kSeam)); }
                    }
                    else if (l == 0 && kSeam >= 0 && fSeam >= 0.999f && baseW.Keys.Any(IsYas))
                    {
                        res.SeamOpen++;                          // jiggle here, none on the other piece: it can open
                        res.SeamOpenWith.Add(seams!.LabelOf(kSeam));
                    }
                    if (!changed && !hadYas) continue;
                    if (!changed) continue;
                    WriteW(baseW);
                    continue;

                    void WriteW(Dictionary<string, float> w) => WriteNamed(m, mi, v, w, slots, addedBones, res);
                }

                // smoothing brush: each touched point moves toward the average of itself and its neighbours (twice)
                var smooth = smoothStrokes.Where(k => k.Hits(slot - 1)).ToList();
                if (smooth.Count > 0 && doWeights && me.VertexCount > 0)
                {
                    int nV = me.VertexCount;
                    var pos = new Vector3[nV];
                    var fall = new float[nV];
                    bool any = false;
                    for (int v = 0; v < nV; v++)
                    {
                        var p4 = m.ReadVec(mi, pe, v);
                        pos[v] = new Vector3(p4.X, p4.Y, p4.Z);
                        float f = 0;
                        foreach (var k in smooth) f = Math.Max(f, k.Falloff(pos[v]));
                        fall[v] = f; any |= f > 0;
                    }
                    if (any)
                    {
                        var nb = new HashSet<int>[nV];
                        var idx = m.ReadIndices(mi);
                        void Link(int a, int b) { if (a >= nV || b >= nV || a == b) return; (nb[a] ??= new()).Add(b); (nb[b] ??= new()).Add(a); }
                        for (int k = 0; k + 2 < idx.Length; k += 3) { Link(idx[k], idx[k + 1]); Link(idx[k + 1], idx[k + 2]); Link(idx[k + 2], idx[k]); }
                        // points at the same spot (split edges) share neighbours, so seams smooth together
                        var spot = new Dictionary<(int, int, int), List<int>>();
                        for (int v = 0; v < nV; v++)
                        {
                            if (fall[v] <= 0) continue;
                            var key = ((int)MathF.Round(pos[v].X * 5000), (int)MathF.Round(pos[v].Y * 5000), (int)MathF.Round(pos[v].Z * 5000));
                            if (!spot.TryGetValue(key, out var sl)) spot[key] = sl = new List<int>();
                            sl.Add(v);
                        }
                        foreach (var sl in spot.Values) for (int a = 0; a < sl.Count; a++) for (int b = a + 1; b < sl.Count; b++) Link(sl[a], sl[b]);
                        var tbl = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
                        var W = new Dictionary<string, float>[nV];
                        for (int v = 0; v < nV; v++) W[v] = WeightsOf(m, mi, v, tbl);
                        for (int it = 0; it < 2; it++)
                        {
                            var next = (Dictionary<string, float>[])W.Clone();
                            for (int v = 0; v < nV; v++)
                            {
                                if (fall[v] <= 0 || W[v].Count == 0) continue;
                                var avg = new Dictionary<string, float>(W[v]);
                                int cnt = 1;
                                if (nb[v] != null)
                                    foreach (var u in nb[v])
                                    {
                                        if (W[u].Count == 0) continue;
                                        foreach (var kv in W[u]) avg[kv.Key] = avg.GetValueOrDefault(kv.Key) + kv.Value;
                                        cnt++;
                                    }
                                var mix = new Dictionary<string, float>();
                                foreach (var kv in W[v]) mix[kv.Key] = kv.Value * (1 - fall[v]);
                                foreach (var kv in avg) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + kv.Value / cnt * fall[v];
                                next[v] = mix;
                            }
                            W = next;
                        }
                        for (int v = 0; v < nV; v++) if (fall[v] > 0 && W[v].Count > 0) WriteNamed(m, mi, v, W[v], slots, addedBones, res);
                    }
                }
            }
        }
        if (res.PointsMoved > 0) m.GrowBoxes(lo, hi);
        if (rig != null) res.HairCheck = Hair.Check(m, rig);     // what each part rides now (0.1.17: shown on the Hair / Hats tab)
        res.BonesAdded = m.BoneNames.Count - startBones;
        res.Bytes = m.Write();
        if (res.Widened) res.Report.Add("Points can now hold 8 bones (was 4).");
        if (res.BonesAdded > 0) res.Report.Add($"Added {res.BonesAdded} bone(s): {string.Join(", ", addedBones.OrderBy(x => x))}.");
        if (res.TableFull > 0) res.Report.Add($"{res.TableFull} point(s) lost a bone: that piece already uses 64 bones.");
        if (res.FarFromBody > 0) res.Report.Add($"{res.FarFromBody} point(s) too far from the body to take weights.");
        return res;
    }

    /// <summary>Writes named weights to a point: to whole numbers, then to this mesh's bone slots
    /// (adding bones to the model and its table as needed).</summary>
    static void WriteNamed(MdlFile m, int mi, int v, Dictionary<string, float> baseW, int slots, HashSet<string> addedBones, EditResult res)
    {
                    var q8 = Quantize(baseW, slots);
                    var outW = new List<(int slot, int w)>();
                    var drop = new List<string>();
                    foreach (var (bn, w) in q8)
                    {
                        int bi = m.BoneIndex(bn);
                        if (bi < 0) { bi = m.AddBone(bn); addedBones.Add(bn); }
                        int ts = m.TableSlot(mi, bi);
                        if (ts < 0) { drop.Add(bn); continue; }
                        outW.Add((ts, w));
                    }
                    if (drop.Count > 0)
                    {
                        res.TableFull++;
                        var keep = baseW.Where(kv => !drop.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                        outW.Clear();
                        foreach (var (bn, w) in Quantize(keep, slots))
                        {
                            int ts = m.TableSlot(mi, m.BoneIndex(bn) < 0 ? m.AddBone(bn) : m.BoneIndex(bn));
                            if (ts >= 0) outW.Add((ts, w));
                        }
                        int tot = outW.Sum(x => x.w);
                        if (tot != 255 && outW.Count > 0) outW[0] = (outW[0].slot, outW[0].w + 255 - tot);
                    }
                    if (outW.Count == 0) return;
                    m.WriteWeights(mi, v, outW);
    }

    static Matrix4x4 RotOnly(MeshEdit e)
        => Matrix4x4.CreateRotationX(e.Rotate.X * MathF.PI / 180) * Matrix4x4.CreateRotationY(e.Rotate.Y * MathF.PI / 180)
           * Matrix4x4.CreateRotationZ(e.Rotate.Z * MathF.PI / 180);
}
