// Yassify engine - finding mods that bring a YAS skeleton.
//
// A character has one skeleton. Two mods that each replace it can't both win: Penumbra takes the one with the higher
// priority, and everything made for the other (its bones, its physics) then runs on a skeleton it wasn't made for. With two
// YAS skeletons that can look fine - until a pose or the jiggle goes wrong, and nothing says why.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yassify.Core;

public static class YasSkeletons
{
    /// <summary>One place in a mod (its default option, or one option of a group) that brings a person's skeleton with YAS
    /// bones in it - for these races.</summary>
    public sealed record Source(string ModDir, bool Default, string Group, string Option, int Index, string GroupType, long GroupDefault, HashSet<string> Races);

    /// <summary>A person's body skeleton: chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb. Minions, mounts,
    /// monsters and weapons have skeletons too (chara/monster/..., chara/weapon/...) - those are no business of this.</summary>
    static readonly Regex Human = new(@"^chara/human/(c\d{4})/skeleton/base/b\d{4}/skl_c\d{4}b\d{4}\.sklb$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Bones only a YAS skeleton has (the butt, belly and thigh jiggle bones). An IVCS skeleton has iv_ bones and none of these.</summary>
    static readonly byte[][] Marks = { Encoding.ASCII.GetBytes("ya_shiri_phys"), Encoding.ASCII.GetBytes("ya_fukubu_phys"), Encoding.ASCII.GetBytes("ya_daitai_phys") };

    /// <summary>Whether a skeleton file carries YAS bones (a skeleton file keeps its bones' names as plain text).</summary>
    public static bool IsYas(ReadOnlySpan<byte> sklb)
    {
        foreach (var m in Marks) if (sklb.IndexOf(m) >= 0) return true;
        return false;
    }

    static bool Mentions(string file)
    {
        try { return File.Exists(file) && Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("/skeleton/base/", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Every mod in the mods folder that brings a YAS skeleton, and where in the mod. Only reads: a mod's options are
    /// looked through only when its option files mention a skeleton at all, and a skeleton file only to see whose bones it has.</summary>
    public static List<Source> Scan(string modsDir)
    {
        var res = new List<Source>();
        if (!Directory.Exists(modsDir)) return res;
        foreach (var dir in Directory.EnumerateDirectories(modsDir))
        {
            try
            {
                bool mentions = Mentions(Path.Combine(dir, ModOptions.MetaFile)) || Mentions(Path.Combine(dir, ModOptions.DefaultFile))
                                || Directory.EnumerateFiles(dir, "group_*.json").Any(Mentions);
                if (!mentions) continue;
                var opts = new ModOptions(dir);
                var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);       // skeleton file -> has YAS bones
                foreach (var c in opts.Containers())
                {
                    if (c.Node["Files"] is not JsonObject files) continue;
                    var races = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (gamePath, v) in files)
                    {
                        var m = Human.Match(gamePath.Replace('\\', '/').TrimStart('/'));
                        if (!m.Success || v is not JsonValue jv || !jv.TryGetValue<string>(out var rel) || rel.Length == 0) continue;
                        if (!seen.TryGetValue(rel, out bool yas))
                        {
                            string full = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                            yas = File.Exists(full) && IsYas(File.ReadAllBytes(full));
                            seen[rel] = yas;
                        }
                        if (yas) races.Add(m.Groups[1].Value.ToLowerInvariant());
                    }
                    if (races.Count == 0) continue;
                    string type = c.GroupNode?["Type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "Single";
                    long def = 0;
                    if (c.GroupNode?["DefaultSettings"] is JsonValue dv) { if (!dv.TryGetValue<long>(out def) && dv.TryGetValue<ulong>(out var du)) def = unchecked((long)du); }
                    res.Add(new Source(Path.GetFileName(dir), c.IsDefault, c.Group, c.Option, c.Index, type, def, races));
                }
            }
            catch { /* a mod that can't be read is not a skeleton to warn about */ }
        }
        return res;
    }

    /// <summary>Whether a source counts, given the options picked for its mod (group name -> picked option names, as Penumbra
    /// reports them). A group with nothing reported falls back to the mod's own default pick.</summary>
    public static bool IsActive(Source s, Dictionary<string, List<string>> settings)
    {
        if (s.Default) return true;
        if (settings.TryGetValue(s.Group, out var picked)) return picked.Contains(s.Option);
        return s.GroupType.Equals("Multi", StringComparison.OrdinalIgnoreCase) ? s.Index is >= 0 and < 63 && (s.GroupDefault >> s.Index & 1) != 0 : s.GroupDefault == s.Index;
    }

    /// <summary>The mods that are switched on and bring a YAS skeleton right now, each with the races it brings one for.
    /// <paramref name="settingsOf"/>: a mod's state in the player's collection - null when it has none there.</summary>
    public static List<(string modDir, HashSet<string> races)> Active(IEnumerable<Source> sources, Func<string, (bool enabled, Dictionary<string, List<string>> settings)?> settingsOf)
    {
        var res = new List<(string, HashSet<string>)>();
        foreach (var g in sources.GroupBy(s => s.ModDir, StringComparer.OrdinalIgnoreCase))
        {
            if (settingsOf(g.Key) is not { enabled: true } st) continue;
            var races = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in g) if (IsActive(s, st.settings)) races.UnionWith(s.Races);
            if (races.Count > 0) res.Add((g.Key, races));
        }
        return res;
    }

    /// <summary>The mods among them that fight over the same skeleton: each shares at least one race with another. Fewer
    /// than two: nothing clashes (a YAS skeleton for the women and another for the men get along).</summary>
    public static List<string> Clashing(List<(string modDir, HashSet<string> races)> active)
    {
        var res = new List<string>();
        for (int i = 0; i < active.Count; i++)
            for (int j = 0; j < active.Count; j++)
                if (i != j && active[i].races.Overlaps(active[j].races)) { res.Add(active[i].modDir); break; }
        return res;
    }
}
