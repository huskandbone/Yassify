// Yassify - the piece being yassified: its original file, the edits (YAS jiggle, fit, refit), the live preview (a
// Penumbra temporary mod) and saving into the mod with a backup first. Every build starts from the original bytes, so
// sliders never pile up rounding.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yassify.Core;

namespace Yassify;

public sealed class Session : IDisposable
{
    readonly Plugin _p;
    public WornModel? Item;
    public byte[] Original = Array.Empty<byte>();     // the file as opened (edits start from this)
    public string DiskSha = "";                       // what the file on disk is now
    public MdlFile? Model;                            // parsed original, for the parts list
    public EditSettings Edit = new();
    public EditResult? Last;
    public string Status = "";
    public string Error = "";
    public bool ShowingOriginal;
    public DateTime? SavedAt;                         // saved into the mod since it was opened
    public bool Busy => _task != null && !_task.IsCompleted;
    /// <summary>The skin of the other body piece you're wearing (your top, for a legs piece; your legs piece, for a top),
    /// as it is in its mod - "Hugs the skin" and "follow the upper / lower body" copy its weights.</summary>
    public BodySurface? OtherSkin;
    public string OtherSkinLabel = "";

    /// <summary>The refit: your body mod's shapes for this piece's slot (Neolithe's Chest XS - L, Almond / Hazelnut...,
    /// Default / Neobelly / Buff; Legs Small - Large), the one your body has on, and the ones the piece fits best (by its
    /// skin, else its cloth) - best first.</summary>
    public List<SizeOption> Sizes = new();
    public string SizesMod = "", SizesNote = "";
    public int YourSize = -1;
    public List<(int idx, double score, string kind)> MadeFor = new();
    public int SizesDone;
    public bool SizesBusy;
    string _sizesRoot = "";
    RefitField? _refit;
    string _refitWant = "";
    public bool RefitLoading => Edit.RefitOn && Refit == null;
    /// <summary>The refit for the shapes picked now (null while it loads, or when none).</summary>
    public RefitField? Refit => Edit.RefitOn && _refit != null && _refit.Key == RefitKey(Edit) ? _refit : null;
    static string RefitKey(EditSettings e) => e.RefitFrom + ">" + e.RefitTo;
    public bool CanRefit => Item?.SlotName is "Chest" or "Legs";
    IReadOnlyList<WornModel> _wearing = Array.Empty<WornModel>();

    readonly NearestCache _cache = new();
    BodySurface? _cacheBody;
    Task? _task;
    CancellationTokenSource? _cts;
    DateTime _changedAt = DateTime.MaxValue;
    int _previewN;
    readonly string _previewDir;

    public Session(Plugin p)
    {
        _p = p;
        _previewDir = Path.Combine(p.ConfigDir, "preview");
        Directory.CreateDirectory(_previewDir);
        foreach (var f in Directory.EnumerateFiles(_previewDir)) { try { File.Delete(f); } catch { } }
    }

    public void Open(WornModel item, IReadOnlyList<WornModel>? wearing = null)
    {
        Close();
        if (wearing != null) _wearing = wearing.ToList();
        OtherSkin = null; OtherSkinLabel = "";
        Sizes = new(); SizesMod = ""; SizesNote = ""; YourSize = -1; MadeFor = new(); SizesDone = 0; SizesBusy = false;
        try
        {
            Original = File.ReadAllBytes(item.FullPath);
            Model = MdlFile.Read(Original);
            DiskSha = BackupStore.Sha(Original);
            Item = item;
            Edit = NewEdit(item);
            Last = null; Error = ""; Status = $"Opened {item.Label}."; SavedAt = null;
            _cache.Clear();
            _p.BodyFor(item.Race);
            LoadOtherSkin(item);
            LoadSizes(item);
        }
        catch (Exception e)
        {
            Item = null; Model = null;
            Error = "Can't open this model: " + e.Message;
        }
    }

    /// <summary>Fresh edits for a piece, starting from the last YAS sliders used.</summary>
    EditSettings NewEdit(WornModel item)
    {
        var e = new EditSettings { Slot = item.SlotName };
        var c = _p.Config;
        e.Yas.FadeStart = c.FadeStartCm / 100f;
        e.Yas.FadeEnd = c.FadeEndCm / 100f;
        e.Yas.Allow8 = c.Allow8;
        e.Yas.Master = Math.Clamp(c.MasterJiggle, 0f, 1f);
        foreach (var (k, v) in c.Strength)
            if (Enum.TryParse<YasRegion>(k, out var r)) e.Yas.Strength[r] = Math.Clamp(v, 0f, 1f);
        return e;
    }

    /// <summary>Every edit back to none (the piece as it is in the mod).</summary>
    public void StartOver()
    {
        if (Item == null) return;
        var keepFrom = Edit.RefitFrom;
        Edit = NewEdit(Item);
        Edit.Yas.Enabled = false;
        Edit.RefitFrom = keepFrom;             // what the piece was made for stays known
        Changed();
    }

    public void Close()
    {
        _cts?.Cancel();
        _p.Penumbra.StopPreview();
        if (Item != null) _p.Penumbra.RedrawPlayer();
        Item = null; Model = null; Last = null; ShowingOriginal = false;
    }

    /// <summary>Your body mod's shapes for this piece's slot, and which the piece fits best (in the background).</summary>
    void LoadSizes(WornModel item)
    {
        string code = item.SlotName == "Chest" ? "top" : item.SlotName == "Legs" ? "dwn" : "";
        if (code.Length == 0) return;
        string modDir = _p.Penumbra.ModDirectory();
        if (modDir.Length == 0) return;
        string root = "", rel = "", gamePath = "";
        if (_p.Config.BodyWorn)
        {
            var wb = _p.WornBody.Where(w => w.Slot == item.SlotName).OrderBy(w => w.Race == item.Race ? 0 : 1).FirstOrDefault();
            if (wb == null) { SizesNote = "Your body isn't loaded yet - My body tab: \"Use the body I'm wearing\"."; return; }
            var full = Path.GetFullPath(wb.File);
            var md = Path.GetFullPath(modDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(md, StringComparison.OrdinalIgnoreCase)) return;
            var inside = full.Substring(md.Length).Replace('\\', '/');
            int cut = inside.IndexOf('/');
            if (cut < 0) return;
            root = Path.Combine(modDir, inside.Substring(0, cut)); rel = inside.Substring(cut + 1); gamePath = wb.GamePath;
            SizesMod = wb.ModName;
        }
        else
        {
            var f = _p.Config.BodyFiles.FirstOrDefault(x => BodySurface.SlotOfName(x) == item.SlotName);
            if (f == null || _p.Config.BodyModDir.Length == 0) return;
            root = Path.Combine(modDir, _p.Config.BodyModDir); rel = f.Replace('\\', '/');
            gamePath = $"chara/equipment/e0000/model/{item.Race}e0000_{code}.mdl";
            SizesMod = _p.Config.BodyModDir;
        }
        var piece = Model;
        if (piece == null) return;
        SizesBusy = true;
        Task.Run(() =>
        {
            List<SizeOption> sizes;
            try { sizes = SizeOptions.For(root, gamePath); }
            catch (Exception e) { Plugin.Log.Warning(e, "Yassify: couldn't read the body mod's options"); sizes = new(); }
            int yours = sizes.FindIndex(o => string.Equals(o.RelPath, rel, StringComparison.OrdinalIgnoreCase));
            _p.Framework.RunOnFrameworkThread(() =>
            {
                if (Item != item) return;
                Sizes = sizes; YourSize = yours; _sizesRoot = root;
                SizesNote = sizes.Count < 2 ? $"{SizesMod} has no other shapes for this slot - refits need a body mod with sizes, like Neolithe." : "";
            });
            if (sizes.Count < 2) { _p.Framework.RunOnFrameworkThread(() => { if (Item == item) SizesBusy = false; }); return; }
            var scores = new List<(int, double, string)>();
            for (int i = 0; i < sizes.Count; i++)
            {
                try
                {
                    var shape = MdlFile.Read(File.ReadAllBytes(Path.Combine(root, sizes[i].RelPath)));
                    var (sc, kind) = RefitField.Fit(piece, shape);
                    scores.Add((i, sc, kind));
                }
                catch (Exception e) { Plugin.Log.Warning(e, $"Yassify: couldn't read the shape {sizes[i].Label}"); }
                int done = i + 1;
                _p.Framework.RunOnFrameworkThread(() => { if (Item == item) SizesDone = done; });
                if (Item != item) return;
            }
            var best = scores.OrderBy(x => x.Item2).ThenBy(x => x.Item1).ToList();
            _p.Framework.RunOnFrameworkThread(() =>
            {
                if (Item != item) return;
                MadeFor = best; SizesBusy = false;
                if (Edit.RefitFrom.Length == 0 && best.Count > 0) Edit.RefitFrom = Sizes[best[0].Item1].RelPath;
            });
        });
    }

    public string SizeLabel(string rel) =>
        Sizes.FirstOrDefault(o => string.Equals(o.RelPath, rel, StringComparison.OrdinalIgnoreCase))?.Label ?? Path.GetFileNameWithoutExtension(rel);

    /// <summary>Picks the shapes to refit between (model paths in the body mod; "" = no refit) and loads them.</summary>
    public void SetRefit(string from, string to)
    {
        Edit.RefitFrom = from; Edit.RefitTo = to;
        Edit.RefitLabel = Edit.RefitOn ? $"{SizeLabel(from)} -> {SizeLabel(to)}" : "";
        Changed();
        if (!Edit.RefitOn || Refit != null) return;
        string key = RefitKey(Edit);
        if (_refitWant == key) return;
        _refitWant = key;
        var item = Item;
        string root = _sizesRoot;
        Task.Run(() =>
        {
            RefitField? f = null; string err = "";
            try { f = RefitField.Build(MdlFile.Read(File.ReadAllBytes(Path.Combine(root, from))), MdlFile.Read(File.ReadAllBytes(Path.Combine(root, to))), key); }
            catch (Exception e) { err = e.Message; }
            _p.Framework.RunOnFrameworkThread(() =>
            {
                if (Item != item) return;
                if (_refitWant == key) _refitWant = "";
                if (f == null) { Error = "Couldn't load the body shapes: " + err; return; }
                _refit = f;
                if (f.Paired < f.Points / 2) Error = $"Those shapes don't line up point for point ({f.Paired:N0} of {f.Points:N0}) - the refit only moves what does.";
                Changed();
            });
        });
    }

    /// <summary>Reads the skin of the other body piece you're wearing (a legs piece: your top; a top: your legs piece), in the background.</summary>
    void LoadOtherSkin(WornModel item)
    {
        string other = item.SlotName == "Legs" ? "Chest" : item.SlotName == "Chest" ? "Legs" : "";
        if (other.Length == 0) return;
        var w = _wearing.FirstOrDefault(x => x.SlotName == other && !string.Equals(x.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        if (w == null) return;
        Task.Run(() =>
        {
            BodySurface? surf = null;
            try
            {
                var bs = new BodySurface();
                if (bs.Add(MdlFile.Read(File.ReadAllBytes(w.FullPath)), w.Label, (_, mat, _) => BodySurface.IsSkinMaterial(mat)) > 0)
                {
                    bs.Build();
                    if (!bs.Empty) surf = bs;
                }
            }
            catch (Exception e) { Plugin.Log.Warning(e, $"Yassify: couldn't read the skin of {w.Label}"); }
            _p.Framework.RunOnFrameworkThread(() =>
            {
                if (Item != item) return;
                OtherSkin = surf;
                OtherSkinLabel = surf != null ? w.Label : "";
                if (surf != null && (Edit.FollowOtherBody || Edit.AnyTight)) Changed();
            });
        });
    }

    /// <summary>Call after any edit: the preview updates a moment later. The YAS sliders become the next piece's start.</summary>
    public void Changed()
    {
        _changedAt = DateTime.UtcNow;
        var c = _p.Config;
        c.FadeStartCm = Edit.Yas.FadeStart * 100; c.FadeEndCm = Edit.Yas.FadeEnd * 100; c.Allow8 = Edit.Yas.Allow8;
        c.MasterJiggle = Edit.Yas.Master;
        c.Strength = Edit.Yas.Strength.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
    }

    /// <summary>Runs every frame.</summary>
    public void Update()
    {
        if (Item == null || Busy) return;
        if (_changedAt == DateTime.MaxValue || (DateTime.UtcNow - _changedAt).TotalMilliseconds < 250) return;
        _changedAt = DateTime.MaxValue;
        Build(preview: _p.Config.LivePreview && !ShowingOriginal);
    }

    void Build(bool preview)
    {
        var item = Item!;
        var settings = Edit.Clone();
        var bytes = Original;
        var body = _p.Body;
        var otherSkin = OtherSkin;
        var refit = Refit;
        if (settings.RefitOn && refit == null) { Status = "Loading the body shapes for the refit..."; return; }   // Changed() again once loaded
        if (!ReferenceEquals(body, _cacheBody)) { _cache.Clear(); _cacheBody = body; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Status = "Working...";
        _task = Task.Run(() =>
        {
            try
            {
                var r = GearEditor.Apply(bytes, settings, body, ct, _cache, otherSkin: otherSkin, refit: refit);
                if (ct.IsCancellationRequested) return;
                string? file = null;
                if (preview)
                {
                    int n = Interlocked.Increment(ref _previewN);
                    file = Path.Combine(_previewDir, $"preview_{n}.mdl");
                    try { File.WriteAllBytes(file, r.Bytes); }
                    catch { try { File.Delete(file); } catch { } throw; }   // e.g. a full disk: no half files left behind
                }
                _p.Framework.RunOnFrameworkThread(() =>
                {
                    if (Item != item) return;
                    Last = r; Error = "";
                    Status = settings.Any ? Summary(r) : "No changes yet.";
                    if (preview && file != null)
                    {
                        var map = item.GamePaths.ToDictionary(g => g, _ => file);
                        if (!settings.Any) { _p.Penumbra.StopPreview(); _p.Penumbra.RedrawPlayer(); }
                        else if (_p.Penumbra.Preview(map, out var err)) _p.Penumbra.RedrawPlayer();
                        else Error = "Preview failed: " + err;
                    }
                    CleanPreviews(file);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _p.Framework.RunOnFrameworkThread(() => { Error = e.Message; Status = ""; });
            }
        }, ct);
    }

    static string Summary(EditResult r)
    {
        var bits = new List<string>();
        if (r.PointsWithYas > 0) bits.Add($"{r.PointsWithYas:N0} points jiggle");
        if (r.TightPoints > 0) bits.Add($"{r.TightPoints:N0} hug the skin");
        if (r.SoftPoints > 0) bits.Add($"{r.SoftPoints:N0} flowy");
        if (r.RefitPoints > 0) bits.Add($"refit moved {r.RefitPoints:N0} points (up to {r.RefitMost * 100:0.0} cm)");
        if (r.OtherBodyPoints > 0) bits.Add($"{r.OtherBodyPoints:N0} follow the other half of your body");
        if (r.PointsReweighted > 0) bits.Add($"{r.PointsReweighted:N0} reweighted from the body");
        return bits.Count > 0 ? string.Join(", ", bits) + "." : "Nothing changed with these settings.";
    }

    void CleanPreviews(string? keep)
    {
        foreach (var f in Directory.EnumerateFiles(_previewDir))
        {
            if (keep != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch { }
        }
    }

    public void ToggleOriginal()
    {
        if (Item == null) return;
        ShowingOriginal = !ShowingOriginal;
        if (ShowingOriginal) { _p.Penumbra.StopPreview(); _p.Penumbra.RedrawPlayer(); }
        else Changed();
    }

    /// <summary>Writes the edited piece into the mod (backing the file up first).</summary>
    public void Save()
    {
        if (Item == null) return;
        if (Busy) { Status = "Still working on the preview - press Save again in a moment."; return; }
        var item = Item;
        var settings = Edit.Clone();
        var bytes = Original;
        var body = _p.Body;
        var expect = DiskSha;
        var otherSkin = OtherSkin;
        var refit = Refit;
        if (settings.RefitOn && refit == null) { Status = "Still loading the body shapes - press Save again in a moment."; return; }
        string modRoot = Path.Combine(_p.Penumbra.ModDirectory(), item.ModDir);
        Status = "Saving...";
        _cts = new CancellationTokenSource();
        _task = Task.Run(() =>
        {
            try
            {
                var r = GearEditor.Apply(bytes, settings, body, default, _cache, otherSkin: otherSkin, refit: refit);
                // the saved model must read back cleanly before it goes in the mod
                var check = MdlFile.Read(r.Bytes);
                if (!check.Write().AsSpan().SequenceEqual(r.Bytes)) throw new IOException("The edited model didn't read back the same - not saved.");
                _p.Backups.KeepPerFile = _p.Config.KeepBackups;
                _p.Backups.SaveInto(modRoot, item.ModDir, item.ModName, item.RelPath, r.Bytes, expect, Describe(settings), SavedYas.From(settings));
                _p.Framework.RunOnFrameworkThread(() =>
                {
                    // the edits stay, still worked out from the file as it was opened - so tweaking and saving again
                    // replaces this save instead of piling onto it
                    DiskSha = BackupStore.Sha(r.Bytes);
                    Last = r;
                    _p.Penumbra.StopPreview();
                    _p.Penumbra.ReloadMod(item.ModDir);
                    _p.Penumbra.RedrawPlayer();
                    Status = $"Saved into {item.ModName}! The old file is in Backups if you ever want it back.";
                    SavedAt = DateTime.UtcNow;
                    Error = "";
                });
            }
            catch (Exception e)
            {
                _p.Framework.RunOnFrameworkThread(() => { Error = "Not saved: " + e.Message; Status = ""; });
            }
        });
    }

    static string Describe(EditSettings s)
    {
        var bits = new List<string>();
        if (s.RefitOn) bits.Add("refit " + s.RefitLabel);
        if (s.Yas.Enabled) bits.Add("YAS added");
        if (s.Yas.Enabled && s.Yas.Fit == ClothFit.Soft) bits.Add($"flowy {s.Yas.Softness:P0}");
        if (s.Yas.Fit == ClothFit.Tight) bits.Add("hugs the skin");
        if (s.Meshes.Values.Any(m => m.Fit is ClothFit.Tight or ClothFit.Soft or ClothFit.AsMade)) bits.Add("own fit on some parts");
        if (s.Meshes.Values.Any(m => m.Jiggle < 1f)) bits.Add("less jiggle on some parts");
        if (s.RemoveYas) bits.Add("YAS taken off");
        if (s.FollowOtherBody) bits.Add("follows the other half of the body");
        return bits.Count > 0 ? string.Join(", ", bits) : "saved";
    }

    /// <summary>After a restore: start again from the file now on disk, edits cleared.</summary>
    public void Reload()
    {
        if (Item == null) return;
        Open(Item);
    }

    public void Dispose()
    {
        Close();
        CleanPreviews(null);
    }
}
