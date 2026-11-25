// Yassify - everything it asks Penumbra (by IPC label, the same labels and types as Penumbra.Api 5.x,
// without shipping that package).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;

namespace Yassify;

/// <summary>A model file on your character that comes from a Penumbra mod.</summary>
public sealed class WornModel
{
    public string FullPath = "";          // the file on disk
    public string ModDir = "";            // the mod's folder name (what Penumbra calls its directory)
    public string ModName = "";
    public string RelPath = "";           // inside the mod folder
    public List<string> GamePaths = new();
    public string Label => $"{SlotName} - {ModName}";
    public string SlotName
    {
        get
        {
            var g = GamePaths.FirstOrDefault() ?? RelPath;
            var f = Path.GetFileNameWithoutExtension(g).ToLowerInvariant();
            if (f.EndsWith("_top")) return "Chest";
            if (f.EndsWith("_dwn")) return "Legs";
            if (f.EndsWith("_glv")) return "Hands";
            if (f.EndsWith("_sho")) return "Feet";
            if (f.EndsWith("_met")) return "Head";
            if (f.EndsWith("_ear")) return "Earrings";
            if (f.EndsWith("_nek")) return "Necklace";
            if (f.EndsWith("_wrs")) return "Bracelets";
            if (f.EndsWith("_rir") || f.EndsWith("_ril")) return "Ring";
            if (g.Contains("/hair/")) return "Hair";
            if (g.Contains("/tail/")) return "Tail";
            if (g.Contains("/body/")) return "Body";
            if (g.Contains("/face/")) return "Face";
            return "Model";
        }
    }
    /// <summary>The race code in the game path (c0201...), or "".</summary>
    public string Race
    {
        get
        {
            var name = Path.GetFileName(GamePaths.FirstOrDefault() ?? "");
            return name.Length >= 5 && name[0] == 'c' ? name.Substring(0, 5) : "";
        }
    }
}

public sealed class PenumbraBridge
{
    public const string Tag = "Yassify";
    public const int Priority = 99999;
    const int Success = 0;            // PenumbraApiEc.Success
    const int RedrawTypeRedraw = 0;   // RedrawType.Redraw
    readonly IDalamudPluginInterface _pi;

    public PenumbraBridge(IDalamudPluginInterface pi) { _pi = pi; }

    public bool Available
    {
        get
        {
            try { var v = _pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5").InvokeFunc(); return v.Item1 >= 5; }
            catch { return false; }
        }
    }

    public string ModDirectory()
    {
        try { return _pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc() ?? ""; } catch { return ""; }
    }

    public Dictionary<string, string> ModList()
    {
        try { return _pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc() ?? new(); } catch { return new(); }
    }

    /// <summary>The file Penumbra gives your character for a game path, or "" when no mod changes it.</summary>
    public string ResolvePlayer(string gamePath)
    {
        try
        {
            var r = _pi.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath").InvokeFunc(gamePath) ?? "";
            if (r.StartsWith('|'))
            {
                int end = r.IndexOf('|', 1);
                r = end < 0 ? "" : r.Substring(end + 1);
            }
            if (r.Length == 0 || string.Equals(r.Replace('\\', '/'), gamePath, StringComparison.OrdinalIgnoreCase)) return "";
            return r;
        }
        catch { return ""; }
    }

    /// <summary>The mod a file on disk belongs to (its name), or "" when it isn't in Penumbra's mod folder.</summary>
    public string ModNameOf(string fullPath)
    {
        var dir = ModDirectory();
        if (dir.Length == 0) return "";
        string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
        var rel = fullPath.Substring(root.Length);
        int cut = rel.IndexOfAny(new[] { '\\', '/' });
        if (cut <= 0) return "";
        var modDir = rel.Substring(0, cut);
        return ModList().GetValueOrDefault(modDir, modDir);
    }

    /// <summary>The models from mods on your character right now. Game thread.</summary>
    public List<WornModel> WornModels()
    {
        var res = new List<WornModel>();
        var dir = ModDirectory();
        if (dir.Length == 0) return res;
        string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var names = ModList();
        Dictionary<string, HashSet<string>>?[] all;
        try
        {
            all = _pi.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                     .InvokeFunc(new ushort[] { 0 });
        }
        catch { return res; }
        var paths = all != null && all.Length > 0 ? all[0] : null;
        if (paths == null) return res;
        foreach (var (actual, games) in paths)
        {
            // Penumbra can put "|prefix|" in front of a path; the file is what follows
            var path = actual;
            if (path.StartsWith('|'))
            {
                int end = path.IndexOf('|', 1);
                if (end < 0) continue;
                path = path.Substring(end + 1);
            }
            if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            string full;
            try { full = Path.GetFullPath(path); } catch { continue; }
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // the game's own file, or our preview
            var rel = full.Substring(root.Length);
            int cut = rel.IndexOfAny(new[] { '\\', '/' });
            if (cut <= 0) continue;
            var modDir = rel.Substring(0, cut);
            res.Add(new WornModel
            {
                FullPath = full, ModDir = modDir, ModName = names.GetValueOrDefault(modDir, modDir),
                RelPath = rel.Substring(cut + 1), GamePaths = games.OrderBy(x => x).ToList(),
            });
        }
        return res;
    }

    /// <summary>Shows files in place of the game paths, on everyone, above every mod (a temporary mod - gone when it's
    /// stopped or the game closes).</summary>
    public bool Preview(Dictionary<string, string> gamePathToFile, out string error)
    {
        error = "";
        try
        {
            StopPreview();
            int ec = _pi.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5")
                        .InvokeFunc(Tag, gamePathToFile, string.Empty, Priority);
            if (ec != Success) { error = "Penumbra error " + ec; return false; }
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public void StopPreview()
    {
        try { _pi.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(Tag, Priority); } catch { }
    }

    /// <summary>The collection your own character uses. Null when Penumbra can't say.</summary>
    public Guid? PlayerCollection()
    {
        try { return _pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0).Item3.Item1; }
        catch { return null; }
    }

    /// <summary>A mod's state in a collection: switched on or off, and the options picked (group name -> option names).
    /// Null when the mod has no settings there, or Penumbra can't say.</summary>
    public (bool enabled, Dictionary<string, List<string>> settings)? ModSettings(Guid collection, string modDir)
    {
        try
        {
            var (ec, t) = _pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5")
                             .InvokeFunc(collection, modDir, string.Empty, false);
            if (ec != 0 || t == null) return null;
            return (t.Value.Item1, t.Value.Item3 ?? new Dictionary<string, List<string>>());
        }
        catch { return null; }
    }

    public void ReloadMod(string modDir)
    {
        try { _pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5").InvokeFunc(modDir, string.Empty); } catch { }
    }

    public void RedrawPlayer()
    {
        try { _pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5").InvokeAction(0, RedrawTypeRedraw); } catch { }
    }
}
