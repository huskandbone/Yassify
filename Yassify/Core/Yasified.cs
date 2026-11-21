// Yassify engine (shared with Tailor) - which pieces are done already: the worn list shows each piece's YAS areas (read
// from its weights) and whether the file on disk is exactly one Tailor saved (from the
// backups), so a piece that's already YAS isn't done twice.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yassify.Core;

/// <summary>What a model's weights say about YAS: LOD 0 points with any weight on a YAS bone, per area.</summary>
public sealed class YasInfo
{
    public int Points, WithYas;
    public readonly SortedDictionary<YasRegion, int> Areas = new();
    public bool Has => WithYas > 0;
    public string AreaList => string.Join(", ", Areas.Keys.Select(Label));
    public string Detail => Has ? $"{WithYas:N0} of {Points:N0} points have YAS weights - " + string.Join(", ", Areas.Select(kv => $"{Label(kv.Key)} {kv.Value:N0}"))
                                : $"no YAS weights on its {Points:N0} points";
    public static string Label(YasRegion r) => r switch
    {
        YasRegion.Butt => "butt", YasRegion.Chest => "chest", YasRegion.Belly => "belly", YasRegion.Thighs => "thighs",
        YasRegion.Arms => "upper arms", YasRegion.Fingers => "fingertips", YasRegion.Toes => "toes", YasRegion.Groin => "groin",
        _ => "other",
    };
}

/// <summary>The YAS settings a Tailor save was made with (0.1.13), kept in its backup entry, so the piece on the other
/// side of the waist can match its tummy. Per area: the value the weights were made with (slider x Overall, or the lock).</summary>
public sealed class SavedYas
{
    public bool Enabled { get; set; }
    public bool Removed { get; set; }
    public Dictionary<string, float> Effective { get; set; } = new();

    /// <summary>What the piece's weights in this area were made with: 0 = no YAS there, 1 = like the skin.</summary>
    public float Of(YasRegion r) => !Enabled || Removed ? 0f : Effective.TryGetValue(r.ToString(), out var x) ? x : 1f;

    public static SavedYas From(EditSettings s)
    {
        var y = new SavedYas { Enabled = s.Yas.Enabled, Removed = s.RemoveYas };
        foreach (var r in Enum.GetValues<YasRegion>())
            if (r != YasRegion.None)
                y.Effective[r.ToString()] = s.Yas.Lock.TryGetValue(r, out var lk) ? lk : s.Yas.Strength.GetValueOrDefault(r, 1f) * s.Yas.Master;
        return y;
    }
}

public static class Yasified
{
    public static YasInfo Scan(MdlFile m)
    {
        var info = new YasInfo();
        foreach (int mi in m.LodMeshes(0))
        {
            var me = m.Meshes[mi];
            var table = me.BoneTableIndex < m.BoneTables.Count ? m.BoneTables[me.BoneTableIndex] : null;
            for (int v = 0; v < me.VertexCount; v++)
            {
                info.Points++;
                if (table == null) continue;
                HashSet<YasRegion>? seen = null;
                foreach (var (sl, w) in m.ReadWeights(mi, v))
                {
                    if (w == 0 || sl >= table.Count || table[sl] >= m.BoneNames.Count) continue;
                    var bn = m.BoneNames[table[sl]];
                    if (GearEditor.IsYas(bn)) (seen ??= new()).Add(GearEditor.Region(bn));
                }
                if (seen == null) continue;
                info.WithYas++;
                foreach (var r in seen) info.Areas[r] = info.Areas.GetValueOrDefault(r) + 1;
            }
        }
        return info;
    }

    /// <summary>The newest Tailor save that the file on disk is exactly (its sha = what Tailor wrote), or null: never saved
    /// with Tailor, put back with Restore, or changed some other way since.</summary>
    public static BackupEntry? SavedBy(BackupStore store, string modDir, string relPath, string sha)
    {
        static string Norm(string p) => p.Replace('\\', '/');
        foreach (var e in store.List(modDir, relPath))       // newest first
        {
            if (e.Note.StartsWith("before restoring", StringComparison.Ordinal)) continue;
            if (!e.IsSet) { if (e.ShaAfter == sha) return e; continue; }
            int i = e.Files.FindIndex(f => string.Equals(Norm(f), Norm(relPath), StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i < e.ShasAfter.Count && e.ShasAfter[i] == sha) return e;
        }
        return null;
    }
}
