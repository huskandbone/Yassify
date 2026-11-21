// Yassify engine (shared with Tailor) - "Follows": a part takes its weights from a model on your character
// (face, ears, hair, tail slot, body) or rides the head. Rigid by default: the
// whole part gets the weights of the one spot where it touches that model, so
// a lip ring moves with the lip as one piece instead of stretching.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Yassify.Core;

/// <summary>A model a part can follow.</summary>
public sealed class Donor
{
    public BodySurface Surface = new();
    public string Label = "";
    /// <summary>Bends a point of the piece (with its weights) into the donor's
    /// space - racial deformer, when the piece is for another race. null = same space.</summary>
    public Func<Vector3, Dictionary<string, float>, Vector3>? ToSurface;
}

public static class Follow
{
    public const string Keep = "", Own = "own", Head = "head", Body = "body";
    public static bool Active(string what) => what.Length > 0 && what != Own;

    /// <summary>The weights at the spot where the points come closest to the
    /// surface (blended across the triangle there), small shares dropped.</summary>
    public static (Dictionary<string, float> w, float dist, int at) Anchor(IReadOnlyList<Vector3> pts, BodySurface surface, float maxDist = 0.25f)
    {
        var hits = new (float d, Dictionary<string, float> w)[pts.Count];
        System.Threading.Tasks.Parallel.For(0, pts.Count, i =>
        {
            var (d, w, _) = surface.Nearest(pts[i], maxDist);
            hits[i] = (d, w);
        });
        int best = -1;
        for (int i = 0; i < hits.Length; i++)
            if (hits[i].w.Count > 0 && (best < 0 || hits[i].d < hits[best].d)) best = i;
        if (best < 0) return (new Dictionary<string, float>(), float.MaxValue, -1);
        return (Clean(hits[best].w), hits[best].d, best);
    }

    /// <summary>Normalised, shares under 2% dropped.</summary>
    public static Dictionary<string, float> Clean(Dictionary<string, float> w, float min = 0.02f)
    {
        float t = w.Values.Sum();
        if (t <= 1e-6f) return new Dictionary<string, float>();
        var k = w.Where(kv => kv.Value / t >= min).ToDictionary(kv => kv.Key, kv => kv.Value);
        float t2 = k.Values.Sum();
        return k.ToDictionary(kv => kv.Key, kv => kv.Value / t2);
    }

    static readonly (string prefix, string name)[] Names =
    {
        ("j_f_mmayu", "inner eyebrow"), ("j_f_mayu", "eyebrow"), ("j_f_miken", "between the brows"), ("j_f_hana", "nose"),
        ("j_f_umlip", "upper lip (side)"), ("j_f_ulip", "upper lip"), ("j_f_dmlip", "lower lip (side)"), ("j_f_dlip", "lower lip"),
        ("j_f_lip", "mouth corner"), ("j_f_hoho", "cheek"), ("j_f_shoho", "cheek"), ("j_f_ago", "chin"), ("j_f_dago", "chin"),
        ("j_f_umab", "upper eyelid"), ("j_f_dmab", "lower eyelid"), ("j_f_mab", "eyelid"), ("j_f_eye", "eye"), ("j_f_bero", "tongue"),
        ("j_f_hagukiup", "upper teeth"), ("j_f_hagukidn", "lower teeth"), ("j_f_", "face"), ("j_ago", "jaw"),
        ("j_kao", "head"), ("j_kubi", "neck"), ("j_mimi", "ear"), ("j_zera", "ear (base)"), ("j_zerb", "ear (tip)"), ("j_zerc", "ear (tip)"),
        ("j_zer", "ear"), ("n_ear_a", "earring bone"), ("n_ear_b", "earring bone (hanging)"), ("j_kami", "hair"), ("j_ex_", "extra (hair/gear) bone"),
        ("n_sippo", "tail"), ("j_sebo_c", "upper chest"), ("j_sebo_b", "chest"), ("j_sebo_a", "waist"), ("j_kosi", "hips"),
        ("iv_c_mune", "breast (YAS)"), ("j_mune", "breast"), ("iv_kyokin", "pectoral (YAS)"), ("ya_fukubu", "belly (YAS)"), ("iv_fukubu", "belly (YAS)"),
        ("j_sako", "collarbone"), ("n_hte", "wrist twist"), ("n_hhiji", "elbow"), ("n_hkata", "shoulder"), ("j_ude_a", "upper arm"), ("j_ude_b", "forearm"), ("j_te", "hand"),
    };

    /// <summary>A bone in plain words ("lower lip (left)"); the bone name if it isn't known.</summary>
    public static string Friendly(string bone)
    {
        foreach (var (p, n) in Names)
            if (bone.StartsWith(p))
            {
                string side = bone.EndsWith("_l") ? " (left)" : bone.EndsWith("_r") ? " (right)" : "";
                return n + side;
            }
        return bone;
    }

    /// <summary>"lower lip 80%, jaw 20%" - the biggest shares, friendly names merged.</summary>
    public static string Describe(Dictionary<string, float> w, int max = 3)
    {
        if (w.Count == 0) return "nothing";
        float t = w.Values.Sum();
        var byName = new Dictionary<string, float>();
        foreach (var kv in w) byName[Friendly(kv.Key)] = byName.GetValueOrDefault(Friendly(kv.Key)) + kv.Value / t;
        return string.Join(", ", byName.OrderByDescending(kv => kv.Value).Take(max).Select(kv => $"{kv.Key} {kv.Value:P0}"));
    }
}

/// <summary>Turning a gizmo's matrix into Move / Turn / Size and back. Matches
/// the editor's transform: points go (p - pivot) x Scale x RotX x RotY x RotZ
/// + pivot + Move (System.Numerics row-vector order).</summary>
public static class GizmoMath
{
    public static Matrix4x4 Compose(Vector3 move, Vector3 rotDeg, Vector3 scale, Vector3 pivot)
    {
        const float d = MathF.PI / 180f;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rotDeg.X * d) * Matrix4x4.CreateRotationY(rotDeg.Y * d)
               * Matrix4x4.CreateRotationZ(rotDeg.Z * d) * Matrix4x4.CreateTranslation(pivot + move);
    }

    public static (Vector3 move, Vector3 rotDeg, Vector3 scale) Decompose(Matrix4x4 g, Vector3 pivot)
    {
        var r1 = new Vector3(g.M11, g.M12, g.M13);
        var r2 = new Vector3(g.M21, g.M22, g.M23);
        var r3 = new Vector3(g.M31, g.M32, g.M33);
        var sc = new Vector3(r1.Length(), r2.Length(), r3.Length());
        if (sc.X < 1e-9f || sc.Y < 1e-9f || sc.Z < 1e-9f) return (g.Translation - pivot, Vector3.Zero, Vector3.One);
        r1 /= sc.X; r2 /= sc.Y; r3 /= sc.Z;
        // R = Rx(a) Ry(b) Rz(c): M13 = -sin b, M23 = sin a cos b, M33 = cos a cos b, M12 = cos b sin c, M11 = cos b cos c
        float sb = Math.Clamp(-r1.Z, -1f, 1f);
        float a, b = MathF.Asin(sb), c;
        if (MathF.Abs(sb) < 0.99999f)
        {
            a = MathF.Atan2(r2.Z, r3.Z);
            c = MathF.Atan2(r1.Y, r1.X);
        }
        else
        {
            a = 0;
            c = MathF.Atan2(-r2.X, r2.Y);
        }
        const float k = 180f / MathF.PI;
        return (g.Translation - pivot, new Vector3(a * k, b * k, c * k), sc);
    }

    /// <summary>The rotation of x with its stretch taken out (rows made square), at x's position.</summary>
    public static Matrix4x4 Frame(in Matrix4x4 x)
    {
        var a = new Vector3(x.M11, x.M12, x.M13);
        var b = new Vector3(x.M21, x.M22, x.M23);
        var t = Matrix4x4.CreateTranslation(x.M41, x.M42, x.M43);
        if (a.LengthSquared() < 1e-12f) return t;
        a = Vector3.Normalize(a);
        b -= a * Vector3.Dot(a, b);
        if (b.LengthSquared() < 1e-12f) return t;
        b = Vector3.Normalize(b);
        var c = Vector3.Cross(a, b);
        return new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, x.M41, x.M42, x.M43, 1);
    }

    /// <summary>The bone a part mostly hangs from - YAS jiggle bones skipped when it has others,
    /// so a gizmo sitting on it doesn't bounce.</summary>
    public static string Anchor(Dictionary<string, float> sum)
    {
        var plain = sum.Where(kv => kv.Value > 0 && !GearEditor.IsYas(kv.Key)).ToList();
        var from = plain.Count > 0 ? plain : sum.Where(kv => kv.Value > 0).ToList();
        return from.Count > 0 ? from.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key : "j_kao";
    }

    /// <summary>A Havok-style transform (scale, rotation, then translation) as a row-vector matrix.</summary>
    public static Matrix4x4 Qs(Vector3 t, Quaternion q, Vector3 s)
    {
        q = q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
    }
}
