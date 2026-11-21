// Yassify engine (shared with Tailor) - finding body models in a mod folder and loading them as the body.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Yassify.Core;

public sealed class BodyCandidate
{
    public string RelPath = "";
    public string Slot = "";        // Chest / Legs / Hands / Feet / other
    public int YasBones;
    public int Points;
    public string Race = "";
}

public static class BodyFiles
{
    public static string SlotOf(string path)
    {
        var f = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (f.EndsWith("_top")) return "Chest";
        if (f.EndsWith("_dwn")) return "Legs";
        if (f.EndsWith("_glv")) return "Hands";
        if (f.EndsWith("_sho")) return "Feet";
        return "Other";
    }

    /// <summary>Body models in a mod folder: the smallclothes slots (e0000) and
    /// anything else that carries YAS bones.</summary>
    public static List<BodyCandidate> Scan(string modRoot, Action<string>? log = null)
    {
        var res = new List<BodyCandidate>();
        if (!Directory.Exists(modRoot)) return res;
        foreach (var f in Directory.EnumerateFiles(modRoot, "*.mdl", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            bool small = name.Contains("e0000_");
            try
            {
                if (new FileInfo(f).Length > 64 << 20) continue;
                var m = MdlFile.Read(File.ReadAllBytes(f));
                int yas = m.BoneNames.Count(GearEditor.IsYas);
                if (!small && yas == 0) continue;
                int pts = 0;
                foreach (int mi in m.LodMeshes(0)) pts += m.Meshes[mi].VertexCount;
                res.Add(new BodyCandidate
                {
                    RelPath = Path.GetRelativePath(modRoot, f), Slot = SlotOf(f), YasBones = yas, Points = pts,
                    Race = name.Length >= 5 && name[0] == 'c' ? name.Substring(0, 5) : "",
                });
            }
            catch (Exception e) { log?.Invoke($"{f}: {e.Message}"); }
        }
        return res.OrderBy(c => c.Slot).ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A sensible first pick: per slot, the first model with YAS bones
    /// (preferring ones with "yiggle" or "yas" in their path).</summary>
    public static List<string> DefaultPick(List<BodyCandidate> all)
    {
        var res = new List<string>();
        foreach (var g in all.Where(c => c.YasBones > 0 && c.Slot != "Other").GroupBy(c => c.Slot))
        {
            var best = g.OrderByDescending(c => c.RelPath.Contains("yiggle", StringComparison.OrdinalIgnoreCase) || c.RelPath.Contains("yas", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).First();
            res.Add(best.RelPath);
        }
        return res;
    }

    public static BodySurface Load(string modRoot, IEnumerable<string> relPaths, out List<string> problems)
    {
        problems = new List<string>();
        var body = new BodySurface();
        foreach (var rel in relPaths)
        {
            try
            {
                var m = MdlFile.Read(File.ReadAllBytes(Path.Combine(modRoot, rel)));
                if (body.Add(m, rel) == 0) problems.Add($"{rel}: no skin meshes");
            }
            catch (Exception e) { problems.Add($"{rel}: {e.Message}"); }
        }
        body.Build();
        return body;
    }
}
