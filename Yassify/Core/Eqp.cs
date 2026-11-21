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

/// <summary>A Penumbra mod's option files (default_mod.json, group_*.json) as editable JSON.</summary>
public sealed class ModOptions
{
    public sealed record Container(string File, string Name, JsonObject Node);

    public readonly string Root;
    public readonly Dictionary<string, JsonNode> Files = new(StringComparer.OrdinalIgnoreCase);   // file name -> its JSON
    readonly Dictionary<string, bool> _bom = new(StringComparer.OrdinalIgnoreCase);

    public ModOptions(string modRoot)
    {
        Root = modRoot;
        var names = new List<string>();
        if (File.Exists(Path.Combine(modRoot, "default_mod.json"))) names.Add("default_mod.json");
        names.AddRange(Directory.EnumerateFiles(modRoot, "group_*.json").Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        foreach (var n in names)
        {
            var raw = File.ReadAllBytes(Path.Combine(modRoot, n));
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var node = JsonNode.Parse(Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0)));
            if (node is JsonObject) { Files[n] = node; _bom[n] = bom; }
        }
    }

    /// <summary>Every place that holds Files / Manipulations: the default option, each option of a group, each container of
    /// a combining group.</summary>
    public IEnumerable<Container> Containers()
    {
        foreach (var (file, root) in Files)
        {
            if (string.Equals(file, "default_mod.json", StringComparison.OrdinalIgnoreCase)) { yield return new Container(file, "Default", (JsonObject)root); continue; }
            string group = root["Name"]?.GetValue<string>() ?? file;
            foreach (var key in new[] { "Options", "Containers" })
                if (root[key] is JsonArray arr)
                    for (int i = 0; i < arr.Count; i++)
                        if (arr[i] is JsonObject o) yield return new Container(file, $"{group}: {o["Name"]?.GetValue<string>() ?? $"#{i + 1}"}", o);
        }
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
        var text = Files[file].ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(text);
        return _bom.GetValueOrDefault(file) ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
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
        var c = opts.Providing(relPath) ?? opts.Containers().FirstOrDefault(x => x.File.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase));
        if (c == null)
        {
            // a mod with no default_mod.json and the file in no option: start one
            opts.Files["default_mod.json"] = new JsonObject { ["Version"] = 0, ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
            c = opts.Containers().First(x => x.File.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase));
        }
        ulong body = opts.BodyEqp(set, c) ?? (Eqp.Entry(eqpFile, set) & Eqp.BodyMask);
        bool isOff = (body & Eqp.BodyDisableBreastPhysics) != 0;
        if (isOff != on) return null;
        var file = opts.SetBodyEqp(c, set, on ? body & ~Eqp.BodyDisableBreastPhysics : body | Eqp.BodyDisableBreastPhysics);
        var path = Path.Combine(modRoot, file);
        return (file, opts.Bytes(file), File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());
    }
}
