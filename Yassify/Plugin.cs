// Yassify - put YAS jiggle on the gear you're wearing (copied from the YAS body you have on), refit Neolithe
// pieces to another of the body's shapes, see it live, and save into the mod with a backup first.
// Its engine is Tailor's (Husk & Bone), unchanged: the same weights, the same refit, the same backups.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Yassify.Core;
using Yassify.Ui;

namespace Yassify;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PI { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework FrameworkService { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;

    const string Command = "/yassify";

    public Configuration Config;
    public readonly PenumbraBridge Penumbra;
    public readonly BackupStore Backups;
    public readonly Session Session;
    public readonly string ConfigDir;
    public IFramework Framework => FrameworkService;
    /// <summary>The big cute title font (the game's own Jupiter).</summary>
    public readonly IFontHandle TitleFont;

    public BodySurface? Body;
    /// <summary>The body's mods, for the chip under the title ("Neolithe YAS AIO").</summary>
    public string BodyName = "";
    public string BodyStatus = "No body yet.";
    public string SettingsNote = "";                 // shown at the top of the window when the settings file can't be read / saved
    public bool BodyLoading;
    public List<string> BodyRaces = new();           // c0201 ... the loaded body models are for
    public List<WornBodyFile> WornBody = new();      // when using the body your character has on
    bool _bodyRetry;
    DateTime _bodyRetryAt = DateTime.MinValue;
    bool _startPending = true;                       // the first frame does what needs the game (see the constructor)

    readonly WindowSystem _windows = new("Yassify");
    readonly MainWindow _main;
    public readonly HelpWindow Help;

    public Plugin()
    {
        Configuration? cfg = null;
        try { cfg = PI.GetPluginConfig() as Configuration; }
        catch (Exception e)
        {
            // a damaged settings file (Dalamud reads it with no fallback) would otherwise stop the plugin loading
            Log.Error(e, "Yassify: couldn't read its settings");
            SettingsNote = "Yassify's settings file couldn't be read, so it started with fresh settings.";
        }
        Config = cfg ?? new Configuration();
        ConfigDir = PI.GetPluginConfigDirectory();
        Directory.CreateDirectory(ConfigDir);
        Penumbra = new PenumbraBridge(PI);
        Backups = new BackupStore(Path.Combine(ConfigDir, "Backups")) { KeepPerFile = Config.KeepBackups };
        Session = new Session(this);
        TitleFont = PI.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Jupiter23));
        _main = new MainWindow(this);
        Help = new HelpWindow(this);
        _windows.AddWindow(_main);
        _windows.AddWindow(Help);
        PI.UiBuilder.Draw += DrawUi;
        PI.UiBuilder.OpenMainUi += OpenMain;
        PI.UiBuilder.OpenConfigUi += OpenMain;      // the installer's settings button: the settings live in the window
        Commands.AddHandler(Command, new CommandInfo((_, args) => OnCommand(args))
        {
            HelpMessage = "Open Yassify (YAS jiggle and Neolithe refits for the outfit you're wearing). /yassify help - the how-to.",
        });
        // Nothing here may read the game: Dalamud builds plugins off the game's main thread, and reading
        // your character there throws ("Not on main thread!"). The body loads on the first frame instead.
        FrameworkService.Update += OnUpdate;
    }

    void OnUpdate(IFramework _)
    {
        if (_startPending)
        {
            _startPending = false;
            try
            {
                if (Config.BodyWorn || (Config.BodyModDir.Length > 0 && Config.BodyFiles.Count > 0)) LoadBody();
            }
            catch (Exception e)
            {
                Log.Error(e, "Yassify: start");
                BodyLoading = false;
                BodyStatus = "Couldn't load the body: " + e.Message;
            }
        }
        // the body you're wearing needs you logged in and Penumbra up: try again every few seconds
        if (_bodyRetry && Config.BodyWorn && DateTime.UtcNow >= _bodyRetryAt)
        {
            _bodyRetryAt = DateTime.UtcNow.AddSeconds(3);
            if (Objects.LocalPlayer != null && Penumbra.Available) LoadBody();
        }
        Session.Update();
    }

    byte[]? _eqp;
    /// <summary>The game's equipment table (EQP), read once; null if it can't be read.</summary>
    public byte[]? EqpTable()
    {
        if (_eqp != null) return _eqp;
        try { _eqp = Data.GetFile(Eqp.GamePath)?.Data; } catch (Exception e) { Log.Error(e, "Yassify: reading the equipment table"); }
        return _eqp;
    }

    public void SaveConfig()
    {
        try
        {
            PI.SavePluginConfig(Config);
            if (SettingsNote.StartsWith("Couldn't save")) SettingsNote = "";
        }
        catch (Exception e)   // e.g. the disk is full: say so instead of breaking the window
        {
            Log.Error(e, "Yassify: couldn't save its settings");
            SettingsNote = "Couldn't save Yassify's settings: " + e.Message;
        }
    }

    // ------------------------------------------------------------------ the body you're wearing
    public sealed record WornBodyFile(string Slot, string Race, string GamePath, string File, string ModName);

    static readonly (string code, string slot)[] BodySlots = { ("top", "Chest"), ("dwn", "Legs"), ("glv", "Hands"), ("sho", "Feet") };

    /// <summary>c0201 etc. for the character, from race / sex / tribe; "" if unknown.</summary>
    public static string RaceCode(ReadOnlySpan<byte> cz, out bool female)
    {
        female = cz.Length > 1 && cz[1] == 1;
        if (cz.Length < 5) return "";
        int race = cz[0], tribe = cz[4];
        int b = race switch
        {
            1 => tribe == 2 ? 3 : 1,   // Hyur: Highlander / Midlander
            2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17,
            _ => 0,
        };
        return b == 0 ? "" : $"c{b + (female ? 1 : 0):00}01";
    }

    /// <summary>The smallclothes (e0000) chest, legs, hands and feet your character has on, as Penumbra resolves them -
    /// whatever body mod and options are on. Per slot: <paramref name="forRace"/> first (the piece being edited), then your
    /// race, then the base body (c0201 / c0101). Game thread.</summary>
    public List<WornBodyFile> FindWornBody(string forRace, out string note)
    {
        note = "";
        var res = new List<WornBodyFile>();
        var me = Objects.LocalPlayer;
        if (me == null) { note = "Waiting for your character..."; return res; }
        string race = RaceCode(me.Customize, out bool female);
        var tries = new List<string>();
        if (IsRace(forRace)) tries.Add(forRace);
        if (race.Length > 0 && !tries.Contains(race)) tries.Add(race);
        string baseRace = female ? "c0201" : "c0101";
        if (!tries.Contains(baseRace)) tries.Add(baseRace);
        string own = Path.GetFullPath(ConfigDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var (code, slot) in BodySlots)
            foreach (var rc in tries)
            {
                var gp = $"chara/equipment/e0000/model/{rc}e0000_{code}.mdl";
                var f = Penumbra.ResolvePlayer(gp);
                if (f.Length == 0) continue;
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }
                if (full.StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue;   // our own preview file
                if (!File.Exists(full)) continue;
                var mod = Penumbra.ModNameOf(full);
                res.Add(new WornBodyFile(slot, rc, gp, full, mod.Length > 0 ? mod : Path.GetFileName(full)));
                break;
            }
        if (res.Count == 0) note = "No body mod is changing your character's body right now (checked " + string.Join(", ", tries) + ").";
        return res;
    }

    static bool IsRace(string r) => r.Length == 5 && r[0] == 'c' && r.Skip(1).All(char.IsDigit);

    /// <summary>When a piece for another race is opened (e.g. a c0201 piece on a Viera), switch to that race's body if your
    /// body mod has one - the piece sits in that shape.</summary>
    public void BodyFor(string race)
    {
        if (!Config.BodyWorn || BodyLoading || !IsRace(race) || BodyRaces.Contains(race)) return;
        try
        {
            if (Objects.LocalPlayer == null || !Penumbra.Available) return;
            var found = FindWornBody(race, out _);
            if (found.Any(f => f.Race == race)) LoadBody(race);
        }
        catch (Exception e) { Log.Warning(e, "Yassify: body for race"); }
    }

    public void LoadBody(string forRace = "")
    {
        _bodyRetry = false;
        string root;
        string[] files;
        var slotOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // body file -> its slot
        List<string> races;
        bool worn = Config.BodyWorn;
        if (worn)
        {
            if (!Penumbra.Available) { BodyStatus = "Penumbra isn't running."; _bodyRetry = true; return; }
            if (Objects.LocalPlayer == null) { BodyStatus = "Waiting for your character..."; _bodyRetry = true; return; }
            var found = FindWornBody(forRace, out var note);
            WornBody = found;
            if (found.Count == 0) { Body = null; BodyRaces = new(); BodyStatus = note; return; }
            root = "";
            files = found.Select(f => f.File).ToArray();
            foreach (var f in found) slotOf[f.File] = f.Slot;
            races = found.Select(f => f.Race).Distinct().ToList();
        }
        else
        {
            var dir = Penumbra.ModDirectory();
            if (dir.Length == 0) { BodyStatus = "Penumbra isn't running."; return; }
            root = Path.Combine(dir, Config.BodyModDir);
            files = Config.BodyFiles.ToArray();
            races = files.Select(f => Path.GetFileName(f).ToLowerInvariant()).Where(n => n.Length >= 5 && IsRace(n.Substring(0, 5)))
                         .Select(n => n.Substring(0, 5)).Distinct().ToList();
        }
        string name = worn ? string.Join(" + ", WornBody.Select(w => w.ModName).Distinct())
                           : Penumbra.ModList().GetValueOrDefault(Config.BodyModDir, Config.BodyModDir);
        BodyLoading = true;
        BodyStatus = "Finding your body...";
        Task.Run(() =>
        {
            try
            {
                var b = BodyFiles.Load(root, files, out var problems);
                for (int i = 0; i < b.Sources.Count; i++)
                {
                    var sl = slotOf.TryGetValue(b.Sources[i], out var known) ? known : BodySurface.SlotOfName(b.Sources[i]);
                    if (sl.Length > 0) b.SourceSlot[i] = sl;
                }
                int yas = 0;
                foreach (var w in b.W) foreach (var k in w.Keys) if (GearEditor.IsYas(k)) { yas++; break; }
                FrameworkService.RunOnFrameworkThread(() =>
                {
                    Body = b.Empty ? null : b;
                    BodyRaces = b.Empty ? new() : races;
                    BodyName = b.Empty ? "" : name;
                    BodyLoading = false;
                    BodyStatus = b.Empty ? "None of those models had skin to copy from." :
                        $"{files.Length} model(s), {b.P.Count:N0} points of skin ({yas:N0} with YAS jiggle)" +
                        (worn ? $", {string.Join("/", races)}." : ".") +
                        (yas == 0 ? " No YAS bones in this body - is your YAS body mod on?" : "") +
                        (problems.Count > 0 ? " Problems: " + string.Join("; ", problems) : "");
                    Session.Changed();
                });
            }
            catch (Exception e)
            {
                FrameworkService.RunOnFrameworkThread(() => { BodyLoading = false; BodyStatus = "Couldn't load the body: " + e.Message; });
            }
        });
    }

    void OpenMain() => _main.IsOpen = true;

    void OnCommand(string args)
    {
        var a = args.Trim().ToLowerInvariant();
        if (a is "help" or "howto" or "how") { Help.IsOpen = true; return; }
        _main.Toggle();
    }

    void DrawUi() => _windows.Draw();

    public void Dispose()
    {
        PI.UiBuilder.OpenMainUi -= OpenMain;
        PI.UiBuilder.OpenConfigUi -= OpenMain;
        FrameworkService.Update -= OnUpdate;
        Commands.RemoveHandler(Command);
        PI.UiBuilder.Draw -= DrawUi;
        _windows.RemoveAllWindows();
        Session.Dispose();
        TitleFont.Dispose();
        SaveConfig();
    }
}
