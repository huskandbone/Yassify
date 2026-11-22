// Yassify engine (shared with Tailor) - the game's per-item switches (EQP, "equipment parameters"). One of them, "Disable Breast Physics", stops the
// breasts bouncing while a top is worn: no weights can make that top move (seen on a top for item e0461:
// only that top lost its bounce). This reads the game's table and a mod's own EQP edits, and adds the edit that turns
// breast physics back on - in the mod option that brings the top's model, in Penumbra's format.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yassify.Core;

public static class Eqp
{
    public const string GamePath = "chara/xls/equipmentparameter/equipmentparameter.eqp";
    /// <summary>The game's value for sets the table leaves out (Penumbra's Eqp.DefaultEntry).</summary>
    public const ulong DefaultEntry = 0x3fe00070603f00;
    public const ulong BodyMask = 0xFFFF;                     // the body (chest) slot's switches: the low 16 bits
    public const ulong BodyDisableBreastPhysics = 0x4000;
    const int BlockSize = 160, NumBlocks = 64;

    /// <summary>The game's entry for a gear set, as Penumbra reads it (ExpandedEqpGmpBase.GetDefaultInternal): the first
    /// 8 bytes say which blocks of 160 sets are in the file; a missing block means the default entry.</summary>
    public static ulong Entry(byte[] file, int setId)
    {
        if (setId <= 0) setId = 1;
        int block = setId / BlockSize;
        if (block >= NumBlocks || file.Length < 8) return DefaultEntry;
        ulong control = BitConverter.ToUInt64(file, 0);
        ulong bit = 1ul << block;
        if ((control & bit) == 0) return DefaultEntry;
        int count = BitOperations.PopCount(control & (bit - 1));
        long at = ((long)BlockSize * count + setId % BlockSize) * 8;
        return at + 8 <= file.Length ? BitConverter.ToUInt64(file, (int)at) : DefaultEntry;
    }

    static readonly Regex TopPath = new(@"chara/equipment/e(\d{4})/model/c\d{4}e\d{4}_top\.mdl$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"chara/equipment/e0461/model/c0201e0461_top.mdl" -> 461; -1 for anything that isn't a top.</summary>
    public static int TopSetId(IEnumerable<string> gamePaths)
    {
        foreach (var g in gamePaths)
        {
            var m = TopPath.Match(g.Replace('\\', '/'));
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return -1;
    }
}

/// <summary>A Penumbra mod's options as editable JSON. Two layouts: the older one, a default_mod.json and a group_*.json per
/// group; and the one-file one (mod format 4, Penumbra from late August 2026), where meta.json holds it all - "DefaultData"
/// for the default option and "Groups" for the rest - and the old files are left behind as .bak.
/// 0.1.2: before, only the older layout was read. On a one-file mod every option edit (the chest's physics switch) went into a new default_mod.json that Penumbra no longer opens, so it did nothing.</summary>
public sealed class ModOptions
{
    public sealed record Container(string File, string Name, JsonObject Node);

    public const string MetaFile = "meta.json", DefaultFile = "default_mod.json";

    public readonly string Root;
    public readonly Dictionary<string, JsonNode> Files = new(StringComparer.OrdinalIgnoreCase);   // file name -> its JSON
    readonly Dictionary<string, bool> _bom = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The mod keeps everything in meta.json (mod format 4 and later).</summary>
    public bool OneFile { get; private set; }

    static (JsonNode? node, bool bom) ReadJson(string path)
    {
        var raw = File.ReadAllBytes(path);
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        return (JsonNode.Parse(Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0))), bom);
    }

    /// <summary>Whether a mod folder is in the one-file layout: its meta.json says format 4 or later.</summary>
    public static bool IsOneFile(string modRoot)
    {
        try
        {
            var p = Path.Combine(modRoot, MetaFile);
            return File.Exists(p) && ReadJson(p).node is JsonObject o && o["FileVersion"] is JsonValue v && v.TryGetValue<int>(out var ver) && ver >= 4;
        }
        catch { return false; }
    }

    public ModOptions(string modRoot)
    {
        Root = modRoot;
        var metaPath = Path.Combine(modRoot, MetaFile);
        if (File.Exists(metaPath))
        {
            var (mn, mbom) = ReadJson(metaPath);
            if (mn is JsonObject mo && mo["FileVersion"] is JsonValue fv && fv.TryGetValue<int>(out var ver) && ver >= 4)
            {
                OneFile = true; Files[MetaFile] = mo; _bom[MetaFile] = mbom;
                return;      // a default_mod.json or group file lying beside it is not part of the mod any more
            }
        }
        var names = new List<string>();
        if (File.Exists(Path.Combine(modRoot, DefaultFile))) names.Add(DefaultFile);
        names.AddRange(Directory.EnumerateFiles(modRoot, "group_*.json").Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        foreach (var n in names)
        {
            var (node, bom) = ReadJson(Path.Combine(modRoot, n));
            if (node is JsonObject) { Files[n] = node; _bom[n] = bom; }
        }
    }

    static IEnumerable<Container> GroupContainers(string file, JsonObject group, string fallbackName)
    {
        string name = group["Name"]?.GetValue<string>() ?? fallbackName;
        foreach (var key in new[] { "Options", "Containers" })
            if (group[key] is JsonArray arr)
                for (int i = 0; i < arr.Count; i++)
                    if (arr[i] is JsonObject o) yield return new Container(file, $"{name}: {o["Name"]?.GetValue<string>() ?? $"#{i + 1}"}", o);
    }

    /// <summary>Every place that holds Files / Manipulations: the default option, each option of a group, each container of
    /// a combining group.</summary>
    public IEnumerable<Container> Containers()
    {
        foreach (var (file, root) in Files)
        {
            if (OneFile)
            {
                if (root["DefaultData"] is JsonObject dd) yield return new Container(file, "Default", dd);
                if (root["Groups"] is JsonArray groups)
                    for (int g = 0; g < groups.Count; g++)
                        if (groups[g] is JsonObject go)
                            foreach (var c in GroupContainers(file, go, $"Group {g + 1}")) yield return c;
                continue;
            }
            if (string.Equals(file, DefaultFile, StringComparison.OrdinalIgnoreCase)) { yield return new Container(file, "Default", (JsonObject)root); continue; }
            foreach (var c in GroupContainers(file, (JsonObject)root, file)) yield return c;
        }
    }

    /// <summary>The mod's default option (always on with the mod) - where an edit goes when no option brings the file. Made
    /// when the mod has none (create), in the layout the mod is in.</summary>
    public Container? Default(bool create = true)
    {
        if (OneFile)
        {
            var root = (JsonObject)Files[MetaFile];
            if (root["DefaultData"] is not JsonObject dd)
            {
                if (!create) return null;
                root["DefaultData"] = dd = new JsonObject { ["Files"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
            }
            return new Container(MetaFile, "Default", dd);
        }
        if (!Files.TryGetValue(DefaultFile, out var def))
        {
            if (!create) return null;
            Files[DefaultFile] = def = new JsonObject { ["Version"] = 0, ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
        }
        return new Container(DefaultFile, "Default", (JsonObject)def);
    }

    static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>The option that puts this file (path inside the mod folder) into the game - where its EQP edit belongs.</summary>
    public Container? Providing(string relPath)
    {
        string want = Norm(relPath);
        foreach (var c in Containers())
            if (c.Node["Files"] is JsonObject files && files.Any(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(Norm(s), want, StringComparison.OrdinalIgnoreCase)))
                return c;
        return null;
    }

    static bool IsBodyEqp(JsonNode? n, int setId) =>
        n is JsonObject o && string.Equals(o["Type"]?.GetValue<string>(), "Eqp", StringComparison.OrdinalIgnoreCase)
        && o["Manipulation"] is JsonObject m && m["SetId"] is JsonValue sid && sid.TryGetValue<int>(out var id) && id == setId
        && string.Equals(m["Slot"]?.GetValue<string>(), "Body", StringComparison.OrdinalIgnoreCase);

    /// <summary>The body EQP value this mod sets for the set, if any (the providing option's first, else any option's).</summary>
    public ulong? BodyEqp(int setId, Container? prefer = null)
    {
        foreach (var c in (prefer != null ? new[] { prefer } : Array.Empty<Container>()).Concat(Containers()))
            if (c.Node["Manipulations"] is JsonArray arr)
                foreach (var n in arr)
                    if (IsBodyEqp(n, setId) && n!["Manipulation"]!["Entry"] is JsonValue e && e.TryGetValue<ulong>(out var v))
                        return v;
        return null;
    }

    /// <summary>Puts the body EQP edit for the set into the container (replacing one already there), as Penumbra writes it:
    /// {"Type": "Eqp", "Manipulation": {"SetId": 461, "Slot": "Body", "Entry": value}}. Returns its file.</summary>
    public string SetBodyEqp(Container c, int setId, ulong bodyEntry)
    {
        if (c.Node["Manipulations"] is not JsonArray arr) c.Node["Manipulations"] = arr = new JsonArray();
        for (int i = arr.Count - 1; i >= 0; i--) if (IsBodyEqp(arr[i], setId)) arr.RemoveAt(i);
        arr.Add(new JsonObject
        {
            ["Type"] = "Eqp",
            ["Manipulation"] = new JsonObject { ["SetId"] = setId, ["Slot"] = "Body", ["Entry"] = bodyEntry & Eqp.BodyMask },
        });
        return c.File;
    }

    /// <summary>Every option that puts this file into the game (a hair's EST edit and skeleton files go in each).</summary>
    public List<Container> AllProviding(string relPath)
    {
        string want = Norm(relPath);
        return Containers().Where(c => c.Node["Files"] is JsonObject files
            && files.Any(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(Norm(s), want, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Puts a file into the game from this option: Files[game path] = path inside the mod (as Penumbra writes it, with
    /// backslashes). Returns its option file.</summary>
    public string SetFile(Container c, string gamePath, string relPath)
    {
        if (c.Node["Files"] is not JsonObject files) c.Node["Files"] = files = new JsonObject();
        foreach (var k in files.Select(kv => kv.Key).Where(k => string.Equals(Norm(k), Norm(gamePath), StringComparison.OrdinalIgnoreCase)).ToList()) files.Remove(k);
        files[gamePath] = relPath.Replace('/', '\\');
        return c.File;
    }

    /// <summary>The file (inside the mod) an option of this mod puts at a game path, if any.</summary>
    public string? FileFor(string gamePath)
    {
        foreach (var c in Containers())
            if (c.Node["Files"] is JsonObject files)
                foreach (var kv in files)
                    if (string.Equals(Norm(kv.Key), Norm(gamePath), StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue v && v.TryGetValue<string>(out var r) && r.Length > 0)
                        return Norm(r);
        return null;
    }

    public byte[] Bytes(string file)
    {
        // meta.json carries the mod's name and description: keep them readable (♥, é ...) the way Penumbra writes them
        var jo = new JsonSerializerOptions { WriteIndented = true };
        if (OneFile) jo.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var text = Files[file].ToJsonString(jo);
        var body = Encoding.UTF8.GetBytes(text);
        return _bom.GetValueOrDefault(file) ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }
}

/// <summary>0.1.2: switches that builds before 0.1.2 saved where a current Penumbra no longer looks. On a one-file mod they
/// wrote a default_mod.json of their own (nothing in it but EQP edits); this finds those and moves the edits into the
/// mod's meta.json, where they finally count. The stray file is left alone - Penumbra ignores it.</summary>
public static class StraySwitches
{
    /// <summary>The edits waiting in a mod's stray default_mod.json: only if the mod is one-file, the file is one a build of this engine wrote
    /// (no files, no swaps, only EQP / EST edits) and meta.json doesn't hold the same edits already.</summary>
    public static List<JsonObject> Waiting(string modRoot)
    {
        var res = new List<JsonObject>();
        try
        {
            var stray = Path.Combine(modRoot, ModOptions.DefaultFile);
            if (!File.Exists(stray) || !ModOptions.IsOneFile(modRoot)) return res;
            var raw = File.ReadAllBytes(stray);
            int skip = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
            if (JsonNode.Parse(Encoding.UTF8.GetString(raw, skip, raw.Length - skip)) is not JsonObject o) return res;
            if (o["Files"] is JsonObject f && f.Count > 0) return res;
            if (o["FileSwaps"] is JsonObject fs && fs.Count > 0) return res;
            if (o["Manipulations"] is not JsonArray arr || arr.Count == 0) return res;
            var mine = new List<JsonObject>();
            foreach (var n in arr)
            {
                if (n is not JsonObject mo || mo["Manipulation"] is not JsonObject) return new List<JsonObject>();
                string type = mo["Type"]?.GetValue<string>() ?? "";
                if (type != "Eqp" && type != "Est") return new List<JsonObject>();      // not one of these files
                mine.Add(mo);
            }
            var opts = new ModOptions(modRoot);
            var have = opts.Containers().SelectMany(c => c.Node["Manipulations"] is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
                           .Select(x => x.ToJsonString()).ToHashSet();
            foreach (var mo in mine) if (!have.Contains(mo.ToJsonString())) res.Add(mo);
        }
        catch { res.Clear(); }
        return res;
    }

    /// <summary>meta.json with the waiting edits moved in - (new bytes, current bytes) - or null when nothing waits. An EQP
    /// edit goes to the option that brings a model of that set (as it is saved now), else to the default option; an edit for
    /// the same thing already there is replaced.</summary>
    public static (byte[] now, byte[] before)? Fix(string modRoot)
    {
        var waiting = Waiting(modRoot);
        if (waiting.Count == 0) return null;
        var opts = new ModOptions(modRoot);
        foreach (var mo in waiting)
        {
            var man = (JsonObject)mo["Manipulation"]!;
            string type = mo["Type"]!.GetValue<string>();
            ModOptions.Container? c = null;
            if (man["SetId"] is JsonValue sv && sv.TryGetValue<int>(out var set))
            {
                string needle = type == "Eqp" ? $"/e{set:D4}/model/" : $"/h{set:D4}/model/";
                c = opts.Containers().FirstOrDefault(x => x.Node["Files"] is JsonObject files && files.Any(kv => kv.Key.Replace('\\', '/').Contains(needle, StringComparison.OrdinalIgnoreCase)));
            }
            c ??= opts.Default()!;
            if (c.Node["Manipulations"] is not JsonArray arr) c.Node["Manipulations"] = arr = new JsonArray();
            // the same switch (type, set, slot / race) already there: the stray one is the newer wish
            string Key(JsonObject x) => $"{x["Type"]}|{x["Manipulation"]?["SetId"]}|{x["Manipulation"]?["Slot"]}|{x["Manipulation"]?["Race"]}|{x["Manipulation"]?["Gender"]}";
            for (int i = arr.Count - 1; i >= 0; i--) if (arr[i] is JsonObject old && Key(old) == Key(mo)) arr.RemoveAt(i);
            arr.Add(mo.DeepClone());
        }
        var path = Path.Combine(modRoot, ModOptions.MetaFile);
        return (opts.Bytes(ModOptions.MetaFile), File.ReadAllBytes(path));
    }
}

/// <summary>Breast physics for a worn top: what the game does with it, and the fix.</summary>
public static class BreastPhysics
{
    public sealed record State(int SetId, ulong Vanilla, ulong? ModValue, string Option)
    {
        /// <summary>The body switches as this mod leaves them (its own edit, else the game's).</summary>
        public ulong Body => ModValue ?? (Vanilla & Eqp.BodyMask);
        public bool Off => (Body & Eqp.BodyDisableBreastPhysics) != 0;
    }

    /// <summary>For a top (by its game paths) from a mod folder; null if it isn't a top or can't be read.</summary>
    public static State? Read(byte[] eqpFile, string modRoot, string relPath, IEnumerable<string> gamePaths)
    {
        int set = Eqp.TopSetId(gamePaths);
        if (set < 0) return null;
        var opts = new ModOptions(modRoot);
        var c = opts.Providing(relPath);
        return new State(set, Eqp.Entry(eqpFile, set), opts.BodyEqp(set, c), c?.Name ?? "Default");
    }

    /// <summary>The mod's option file with breast physics turned on for the top (the rest of the game's switches kept):
    /// (file name, new bytes, current bytes). Null if it's already on.</summary>
    public static (string file, byte[] now, byte[] before)? TurnOn(byte[] eqpFile, string modRoot, string relPath, IEnumerable<string> gamePaths)
        => Set(eqpFile, modRoot, relPath, gamePaths, true);

    /// <summary>Turned on or off (0.1.15: off = the top keeps its size but never bounces). Null if it already is.</summary>
    public static (string file, byte[] now, byte[] before)? Set(byte[] eqpFile, string modRoot, string relPath, IEnumerable<string> gamePaths, bool on)
    {
        int set = Eqp.TopSetId(gamePaths);
        if (set < 0) return null;
        var opts = new ModOptions(modRoot);
        // the option that brings the piece; else the mod's default option (made if it has none)
        var c = opts.Providing(relPath) ?? opts.Default()!;
        ulong body = opts.BodyEqp(set, c) ?? (Eqp.Entry(eqpFile, set) & Eqp.BodyMask);
        bool isOff = (body & Eqp.BodyDisableBreastPhysics) != 0;
        if (isOff != on) return null;
        var file = opts.SetBodyEqp(c, set, on ? body & ~Eqp.BodyDisableBreastPhysics : body | Eqp.BodyDisableBreastPhysics);
        var path = Path.Combine(modRoot, file);
        return (file, opts.Bytes(file), File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());
    }
}
