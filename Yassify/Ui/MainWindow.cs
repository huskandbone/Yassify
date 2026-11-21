// Yassify - the window: a cute banner, your outfit on the left, the piece you picked on the right (Jiggle / Refit /
// Parts), and tabs for your body and the backups.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Yassify.Core;

namespace Yassify.Ui;

public sealed class MainWindow : Window
{
    readonly Plugin _p;
    List<WornModel> _worn = new();
    readonly WornStatus _status = new();       // per piece: YAS already? saved with Yassify?
    string _wornNote = "";
    string _selectTab = "";                  // a tab to bring to the front next frame
    string _selectSub = "";                  // the same for the piece's own tabs
    bool _confirmSave;

    // my body tab
    string _modFilter = "";
    string _bodyMod = "";
    List<BodyCandidate> _cands = new();
    HashSet<string> _picked = new();
    string _scanNote = "";

    // backups tab
    bool _allBackups;
    BackupEntry? _confirmRestore;

    static float S => ImGuiHelpers.GlobalScale;

    static readonly (YasRegion r, string label, FontAwesomeIcon icon, string tip)[] MainAreas =
    {
        (YasRegion.Butt, "Butt", FontAwesomeIcon.Heart, "How much the piece bounces with your butt."),
        (YasRegion.Chest, "Chest", FontAwesomeIcon.HeartCirclePlus,
            "How much the piece follows your breasts (and pecs, if your body has them). 100% = like the skin.\n" +
            "The breast bone also carries their SIZE, so lower = the top follows your bust size less too\n" +
            "(a big bust can poke through a tight top). For a stiff top that keeps its size, use\n" +
            "\"Keep my chest still in this top\" instead."),
        (YasRegion.Belly, "Belly", FontAwesomeIcon.Cookie, "Tummy jiggle. Give a top and its bottoms the same Belly value so they meet nicely at the waist."),
        (YasRegion.Thighs, "Thighs", FontAwesomeIcon.Star, "Thigh jiggle."),
        (YasRegion.Arms, "Upper arms", FontAwesomeIcon.HandSparkles, "Upper-arm jiggle (sleeves)."),
    };

    static readonly (YasRegion r, string label, FontAwesomeIcon icon, string tip)[] MoreAreas =
    {
        (YasRegion.Fingers, "Fingertips", FontAwesomeIcon.HandPaper, "For gloves."),
        (YasRegion.Toes, "Toes", FontAwesomeIcon.ShoePrints, "For shoes and socks."),
        (YasRegion.Groin, "Groin", FontAwesomeIcon.Venus, "Off (0%) to start - turn it up only for tight bottoms."),
        (YasRegion.Other, "Other YAS bones", FontAwesomeIcon.Gem, "Anything else your body has."),
    };

    static readonly (ClothFit fit, string label, FontAwesomeIcon icon, string blurb, string tip)[] Fits =
    {
        (ClothFit.AsMade, "As made", FontAwesomeIcon.Tshirt,
            "Keeps the mod's own weights and adds your body's jiggle on top.",
            "Keeps the mod's own weights and adds the body's jiggle on top. Good for most things."),
        (ClothFit.Tight, "Hugs the skin", FontAwesomeIcon.HandHoldingHeart,
            "Near the skin, the cloth copies the skin under it exactly - nothing pokes through when you bounce.",
            "Shorts, leggings, bodysuits, straps: near the skin, every point copies the weights of the skin that's actually\n" +
            "drawn under it (this piece's own skin, or the top / bottoms you're wearing), so cloth and skin move as one.\n" +
            "Cloth further out eases back to \"As made\" (see Loose cloth)."),
        (ClothFit.Soft, "Flowy", FontAwesomeIcon.Wind,
            "Each spot takes the jiggle of the skin around it, so it drapes instead of hugging every curve.",
            "Dresses, skirts, baggy pants: each spot takes the jiggle of the skin around it, not just the spot under it,\n" +
            "so the cloth hangs over the butt and between the legs instead of hugging every curve."),
    };

    public MainWindow(Plugin p) : base("Yassify###YassifyMain")
    {
        _p = p;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 520), MaximumSize = new Vector2(2400, 2000) };
        Size = new Vector2(920, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            IconOffset = new Vector2(1.5f, 1f),
            Click = _ => _p.Help.IsOpen = !_p.Help.IsOpen,
            ShowTooltip = () => ImGui.SetTooltip("How to yassify"),
        });
    }

    public override void PreDraw() => Theme.Push();
    public override void PostDraw() => Theme.Pop();

    public override void OnOpen()
    {
        RefreshWorn();
        if (_bodyMod.Length == 0) _bodyMod = _p.Config.BodyModDir;
        if (!_p.Config.SeenHelp)
        {
            _p.Help.IsOpen = true;
            _p.Config.SeenHelp = true;
            _p.SaveConfig();
        }
    }

    public override void OnClose() => _p.SaveConfig();

    void RefreshWorn()
    {
        // while a preview is on, our own file stands in for the piece - keep the list as it was
        if (_p.Session.Item != null && _worn.Count > 0) { _wornNote = "Close the piece to look again."; return; }
        _wornNote = "Looking...";
        // Penumbra reads the character from game memory: ask on the game's thread
        _p.Framework.RunOnFrameworkThread(() =>
        {
            if (!_p.Penumbra.Available) { _worn = new(); _wornNote = "Penumbra isn't running (or is too old)."; return; }
            _worn = _p.Penumbra.WornModels().Where(w => w.SlotName is not ("Hair" or "Face" or "Tail"))
                      .OrderBy(w => SlotOrder(w.SlotName)).ThenBy(w => w.ModName, StringComparer.OrdinalIgnoreCase).ToList();
            _status.Refresh(_worn, _p.Backups, _p.EqpTable(), _p.Penumbra.ModDirectory());
            _wornNote = _worn.Count == 0 ? "Nothing from a mod on your character right now. Put an outfit on and look again!" : "";
        });
    }

    static int SlotOrder(string slot) => slot switch
    {
        "Chest" => 0, "Legs" => 1, "Hands" => 2, "Feet" => 3, "Body" => 4, "Head" => 5,
        "Earrings" => 6, "Necklace" => 7, "Bracelets" => 8, "Ring" => 9, _ => 10,
    };

    static bool BodySlot(string slot) => slot is "Chest" or "Legs" or "Hands" or "Feet" or "Body";

    static FontAwesomeIcon SlotIcon(string slot) => slot switch
    {
        "Chest" => FontAwesomeIcon.Tshirt,
        "Legs" => FontAwesomeIcon.Socks,
        "Hands" => FontAwesomeIcon.Mitten,
        "Feet" => FontAwesomeIcon.ShoePrints,
        "Body" => FontAwesomeIcon.PersonHalfDress,
        "Head" => FontAwesomeIcon.HatWizard,
        "Earrings" or "Necklace" or "Bracelets" or "Ring" => FontAwesomeIcon.Gem,
        _ => FontAwesomeIcon.Star,
    };

    // ------------------------------------------------------------------ the window
    public override void Draw()
    {
        if (_p.SettingsNote.Length > 0)
        {
            Theme.Wrapped(_p.SettingsNote, Theme.Peach);
            if (ImGui.SmallButton("OK##settingsnote")) _p.SettingsNote = "";
        }
        DrawBanner();
        DrawBodyChip();
        ImGui.Spacing();
        if (ImGui.BeginTabBar("##ytabs"))
        {
            TabItem("Dress up", "dress", DrawDressUp);
            TabItem("My body", "body", DrawBody);
            TabItem("Backups", "backups", DrawBackups);
            ImGui.EndTabBar();
        }
    }

    void TabItem(string label, string id, Action draw)
    {
        var flags = _selectTab == id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label + "###tab" + id, flags)) return;
        if (_selectTab == id) _selectTab = "";
        draw();
        ImGui.EndTabItem();
    }

    /// <summary>The pink banner: the name in the game's own Jupiter font, a tagline, floating bubbles and hearts.</summary>
    void DrawBanner()
    {
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X, h = 64 * S;
        var p1 = p0 + new Vector2(w, h);
        dl.AddRectFilled(p0, p1, Theme.U(Theme.Rgb(0x9C3D84)), 12 * S);
        dl.PushClipRect(p0 + new Vector2(6 * S, 0), p1 - new Vector2(6 * S, 0), true);
        // soft bubbles (fixed spots, so nothing flickers)
        (float x, float y, float r, uint c)[] dots =
        {
            (0.62f, 0.25f, 26, 0xFFC9A7FF), (0.74f, 0.80f, 34, 0xFFFF8FC8), (0.86f, 0.18f, 22, 0xFFFFC2E2),
            (0.93f, 0.70f, 30, 0xFFC9A7FF), (0.52f, 0.95f, 18, 0xFFFFC2E2), (0.12f, 1.05f, 30, 0xFFFF8FC8),
        };
        foreach (var (x, y, r, c) in dots)
        {
            var col = Theme.Rgb(c & 0xFFFFFF, 0.18f);
            dl.AddCircleFilled(p0 + new Vector2(w * x, h * y), r * S, Theme.U(col));
        }
        dl.PopClipRect();
        // sparkles and hearts
        var iconFont = UiBuilder.IconFont;
        float isz = ImGui.GetFontSize();
        (float x, float y, FontAwesomeIcon icon, float scale, uint c)[] deco =
        {
            (0.70f, 0.22f, FontAwesomeIcon.Star, 0.8f, 0xFFF4FA), (0.80f, 0.52f, FontAwesomeIcon.Heart, 1.0f, 0xFFC2E2),
            (0.89f, 0.20f, FontAwesomeIcon.Star, 0.6f, 0xFFF4FA), (0.95f, 0.55f, FontAwesomeIcon.Heart, 0.75f, 0xFF8FC8),
            (0.60f, 0.62f, FontAwesomeIcon.Star, 0.55f, 0xFFC2E2),
        };
        foreach (var (x, y, icon, sc, c) in deco)
            dl.AddText(iconFont, isz * sc, p0 + new Vector2(w * x, h * y - isz * sc / 2), Theme.U(Theme.Rgb(c, 0.85f)), icon.ToIconString());
        // the name
        var title = "Yassify";
        float pad = 14 * S;
        ImGui.SetCursorScreenPos(p0 + new Vector2(pad, 6 * S));
        using (_p.TitleFont.Push())
        {
            var tp = ImGui.GetCursorScreenPos();
            dl.AddText(tp + new Vector2(2, 2) * S, Theme.U(Theme.Rgb(0x4A1440, 0.7f)), title);
            ImGui.TextColored(Theme.Rgb(0xFFFFFF), title);
        }
        ImGui.SetCursorScreenPos(new Vector2(p0.X + pad, ImGui.GetCursorScreenPos().Y - 2 * S));
        ImGui.TextColored(Theme.Rgb(0xFFE3F2), "make your outfits jiggle like you do");
        ImGui.SameLine(0, 5 * S);
        Theme.Icon(FontAwesomeIcon.Heart, Theme.Rgb(0xFFC2E2));
        ImGui.SetCursorScreenPos(new Vector2(p0.X, p1.Y + 6 * S));
        ImGui.Dummy(new Vector2(w, 0));
    }

    /// <summary>The body the jiggle comes from: a green chip when it's ready, a nudge when it isn't.</summary>
    void DrawBodyChip()
    {
        if (_p.BodyLoading) { Theme.IconText(FontAwesomeIcon.Sync, "Finding your body...", Theme.Lilac, Theme.Muted); return; }
        if (_p.Body != null)
        {
            Theme.IconText(FontAwesomeIcon.CheckCircle, "Jiggle comes from: " + (_p.BodyName.Length > 0 ? _p.BodyName : "your body"), Theme.Mint, Theme.Cream);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_p.BodyStatus);
            return;
        }
        Theme.IconText(FontAwesomeIcon.HeartCircleExclamation, "No body yet -", Theme.Peach, Theme.Peach);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_p.BodyStatus);
        ImGui.SameLine(0, 6 * S);
        if (ImGui.SmallButton("Use the body I'm wearing##chip") && !_p.BodyLoading) UseWornBody();
        if (_p.BodyStatus.Length > 0 && _p.BodyStatus != "No body yet.") Theme.Wrapped(_p.BodyStatus, Theme.Muted);
    }

    void UseWornBody()
    {
        _p.Config.BodyWorn = true;
        _p.SaveConfig();
        _p.LoadBody(_p.Session.Item?.Race ?? "");
    }

    // ------------------------------------------------------------------ Dress up: your outfit + the piece
    void DrawDressUp()
    {
        float left = 255 * S;
        if (ImGui.BeginChild("##outfit", new Vector2(left, 0), true)) DrawOutfit();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##editor", new Vector2(0, 0), false))
        {
            if (_p.Session.Item == null) DrawEmpty(); else DrawPiece();
        }
        ImGui.EndChild();
    }

    void DrawOutfit()
    {
        ImGui.AlignTextToFramePadding();
        Theme.IconText(FontAwesomeIcon.Tshirt, "Your outfit", Theme.Pink);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight()));
        if (ImGuiComponents.IconButton("refresh", FontAwesomeIcon.Sync)) RefreshWorn();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Look again at what you're wearing.");
        if (_wornNote.Length > 0) Theme.Wrapped(_wornNote, Theme.Muted);
        ImGui.Spacing();
        bool others = false;
        foreach (var w in _worn)
        {
            if (!BodySlot(w.SlotName) && !others)
            {
                others = true;
                ImGui.Spacing();
                ImGui.TextDisabled("Accessories & more");
            }
            DrawCard(w);
        }
    }

    /// <summary>One piece you're wearing, as a little card: its slot, its mod, and tags for YAS / saved.</summary>
    void DrawCard(WornModel w)
    {
        var s = _p.Session;
        var st = _status.Get(w);
        bool isOpen = s.Item != null && string.Equals(s.Item.FullPath, w.FullPath, StringComparison.OrdinalIgnoreCase);
        var pos = ImGui.GetCursorScreenPos();
        float cw = ImGui.GetContentRegionAvail().X;
        float line = ImGui.GetTextLineHeight();
        float ch = line * 2 + 12 * S;
        // the card draws its own background (rounded), so the selectable's square one is hidden
        ImGui.PushStyleColor(ImGuiCol.Header, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Vector4.Zero);
        if (ImGui.Selectable("##card" + w.FullPath, isOpen, ImGuiSelectableFlags.None, new Vector2(cw, ch)) && !isOpen) OpenPiece(w);
        ImGui.PopStyleColor(3);
        bool hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetTooltip(w.ModName + "\n" + w.RelPath + "\n" + string.Join("\n", w.GamePaths) + StatusTip(st));
        var dl = ImGui.GetWindowDrawList();
        var bg = isOpen ? Theme.A(Theme.HotPink, 0.30f) : hovered ? Theme.A(Theme.Lilac, 0.16f) : Theme.A(Theme.Lilac, 0.07f);
        dl.AddRectFilled(pos, pos + new Vector2(cw, ch), Theme.U(bg), 8 * S);
        if (isOpen) dl.AddRect(pos, pos + new Vector2(cw, ch), Theme.U(Theme.A(Theme.Pink, 0.8f)), 8 * S, ImDrawFlags.None, 1.5f * S);
        // icon
        float isz = ImGui.GetFontSize() * 1.25f;
        dl.AddText(UiBuilder.IconFont, isz, pos + new Vector2(10 * S, (ch - isz) / 2), Theme.U(BodySlot(w.SlotName) ? Theme.Pink : Theme.Lilac), SlotIcon(w.SlotName).ToIconString());
        float tx = 10 * S + isz + 10 * S;
        // tags on the right
        float right = pos.X + cw - 8 * S;
        if (st?.Saved != null)
        {
            right -= Theme.PillWidth("saved");
            Theme.Pill(dl, new Vector2(right, pos.Y + 6 * S), "saved", Theme.A(Theme.Lilac, 0.35f), Theme.Cream);
            right -= 4 * S;
        }
        if (st?.Yas is { Has: true })
        {
            right -= Theme.PillWidth("YAS");
            Theme.Pill(dl, new Vector2(right, pos.Y + 6 * S), "YAS", Theme.A(Theme.Mint, 0.85f), Theme.Plum);
        }
        float maxW = right - (pos.X + tx) - 6 * S;
        dl.AddText(pos + new Vector2(tx, 6 * S), Theme.U(Theme.Cream), Theme.Fit(w.SlotName, Math.Max(20, maxW)));
        dl.AddText(pos + new Vector2(tx, 6 * S + line + 2 * S), Theme.U(Theme.Muted), Theme.Fit(w.ModName, cw - tx - 10 * S));
        ImGui.Dummy(new Vector2(0, 2 * S));
    }

    /// <summary>The worn list's hover text: what the piece's weights and the backups say.</summary>
    static string StatusTip(WornStatus.Info? st)
    {
        if (st == null) return "\n\nReading the piece...";
        if (st.Yas == null) return "\n\nCouldn't read it: " + st.Error;
        string t = "\n\nYAS already: " + st.Yas.Detail + ".";
        if (st.BreastOff) t += "\nThe game keeps the chest still in this top.";
        t += st.Saved is { } e ? $"\nSaved with Yassify {e.Saved:d MMM HH:mm}: {e.Note}" : "";
        return t;
    }

    void OpenPiece(WornModel w)
    {
        _p.Session.Open(w, _worn);
        _bp = null; _bpFor = ""; _bpNote = "";
        _selectSub = "jiggle";
    }

    void DrawEmpty()
    {
        if (_p.Session.Error.Length > 0) Theme.IconText(FontAwesomeIcon.HeartCircleExclamation, _p.Session.Error, Theme.Rose, Theme.Rose);
        ImGui.Dummy(new Vector2(0, 30 * S));
        float w = ImGui.GetContentRegionAvail().X;
        var big = FontAwesomeIcon.WandMagicSparkles.ToIconString();
        float isz = ImGui.GetFontSize() * 3f;
        var dl = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos() + new Vector2((w - isz) / 2, 0);
        dl.AddText(UiBuilder.IconFont, isz, at, Theme.U(Theme.Pink), big);
        ImGui.Dummy(new Vector2(0, isz + 10 * S));
        Centered("Pick a piece on the left to yassify it!", Theme.Cream);
        Centered("It changes live on your character - nothing is saved until you say so.", Theme.Muted);
        ImGui.Dummy(new Vector2(0, 16 * S));
        float bw = Math.Min(460 * S, w - 20 * S);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (w - bw) / 2);
        if (ImGui.BeginChild("##steps", new Vector2(bw, 150 * S), true))
        {
            Step(1, "Wear your YAS body (and the YAS skeleton) and the outfit, in Penumbra.");
            Step(2, "Click a piece - tops, bottoms, gloves and shoes first.");
            Step(3, "Add YAS jiggle, pick how it fits, and play with the sliders.");
            Step(4, "Happy? Save into my mod. The old file is kept in Backups.");
        }
        ImGui.EndChild();
        ImGui.Dummy(new Vector2(0, 8 * S));
        float hb = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.QuestionCircle, "How it works");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (w - hb) / 2);
        if (Theme.SoftButton(FontAwesomeIcon.QuestionCircle, "How it works")) _p.Help.IsOpen = true;
    }

    static void Centered(string text, Vector4 color)
    {
        float w = ImGui.GetContentRegionAvail().X, tw = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (w - tw) / 2));
        ImGui.TextColored(color, text);
    }

    static void Step(int n, string text)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float r = ImGui.GetTextLineHeight() * 0.55f;
        dl.AddCircleFilled(p + new Vector2(r, ImGui.GetTextLineHeight() / 2), r, Theme.U(Theme.HotPink));
        var num = n.ToString();
        var ns = ImGui.CalcTextSize(num);
        dl.AddText(p + new Vector2(r - ns.X / 2, (ImGui.GetTextLineHeight() - ns.Y) / 2), Theme.U(Theme.Rgb(0xFFFFFF)), num);
        ImGui.SetCursorScreenPos(p + new Vector2(r * 2 + 8 * S, 0));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
    }

    // ------------------------------------------------------------------ the open piece
    DateTime? _seenSave;

    void DrawPiece()
    {
        var s = _p.Session;
        var item = s.Item!;
        if (s.SavedAt != _seenSave)
        {
            // just saved: the tags in the list (YAS, saved) read the file again
            _seenSave = s.SavedAt;
            if (s.SavedAt != null) _status.Refresh(_worn, _p.Backups, _p.EqpTable(), _p.Penumbra.ModDirectory());
        }
        // header: icon, slot, mod
        float isz = ImGui.GetFontSize() * 1.6f;
        var dl = ImGui.GetWindowDrawList();
        var hp = ImGui.GetCursorScreenPos();
        dl.AddText(UiBuilder.IconFont, isz, hp + new Vector2(2 * S, 2 * S), Theme.U(Theme.Pink), SlotIcon(item.SlotName).ToIconString());
        ImGui.SetCursorScreenPos(hp + new Vector2(isz + 12 * S, 0));
        ImGui.BeginGroup();
        ImGui.TextColored(Theme.Cream, item.SlotName);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Pink, "from");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Cream, item.ModName);
        if (_status.Get(item) is { Yas: { } yi } sti)
        {
            string done = yi.Has ? "Already has YAS on: " + yi.AreaList + "." : "No YAS jiggle yet.";
            if (sti.Saved is { } se) done += $"  Saved with Yassify {se.Saved:d MMM HH:mm}.";
            ImGui.TextColored(yi.Has || sti.Saved != null ? Theme.Mint : Theme.Muted, done);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(StatusTip(sti).TrimStart('\n') + "\n(as the file was when you opened it)\n\n" + item.RelPath);
        }
        else ImGui.TextDisabled(item.RelPath);
        ImGui.EndGroup();
        if (_p.Body != null && !_p.BodyLoading && item.Race.Length > 0 && _p.BodyRaces.Count > 0 && !_p.BodyRaces.Contains(item.Race))
            Theme.Wrapped($"This piece is for {item.Race}; your body is {string.Join("/", _p.BodyRaces)} - the jiggle may land a little off.", Theme.Peach);
        // live preview, peek, close
        bool live = _p.Config.LivePreview;
        if (ImGui.Checkbox("Live on my character", ref live))
        {
            _p.Config.LivePreview = live; _p.SaveConfig();
            if (live) s.Changed(); else { _p.Penumbra.StopPreview(); _p.Penumbra.RedrawPlayer(); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every change shows on you a moment later (a temporary Penumbra mod - nothing is written).");
        ImGui.SameLine();
        if (Theme.SoftButton(s.ShowingOriginal ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, s.ShowingOriginal ? "Back to my edit" : "Peek at the original")) s.ToggleOriginal();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flip between the piece as it is in the mod and your edit, to compare.");
        ImGui.SameLine();
        if (Theme.SoftButton(FontAwesomeIcon.Times, "Close")) { s.Close(); RefreshWorn(); return; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Close the piece. Anything not saved is thrown away.");
        ImGui.Spacing();

        float footer = ImGui.GetFrameHeightWithSpacing() * 2.2f + ImGui.GetTextLineHeightWithSpacing() * 2f;
        if (ImGui.BeginTabBar("##piecetabs"))
        {
            SubTab("Jiggle", "jiggle", () => DrawJiggle(s, item), footer);
            if (s.CanRefit) SubTab("Refit", "refit", () => DrawRefit(s), footer);
            SubTab("Parts", "parts", () => DrawParts(s), footer);
            ImGui.EndTabBar();
        }
        DrawFooter(s, item);
    }

    void SubTab(string label, string id, Action draw, float footer)
    {
        var flags = _selectSub == id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label + "###sub" + id, flags)) return;
        if (_selectSub == id) _selectSub = "";
        if (ImGui.BeginChild("##sub" + id, new Vector2(0, -footer), false)) draw();
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    void DrawFooter(Session s, WornModel item)
    {
        ImGui.Separator();
        if (s.Error.Length > 0) Theme.IconText(FontAwesomeIcon.HeartCircleExclamation, s.Error, Theme.Rose, Theme.Rose);
        else if (s.Busy) Theme.IconText(FontAwesomeIcon.Sync, "Working on it...", Theme.Lilac, Theme.Muted);
        else Theme.Wrapped(s.Status, s.Status.StartsWith("Saved") ? Theme.Mint : Theme.Muted);
        if (s.Last != null && s.Last.Report.Count > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", s.Last.Report));
        ImGui.Spacing();
        if (Theme.BigButton(FontAwesomeIcon.WandMagicSparkles, "Save into my mod", !s.Busy)) _confirmSave = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip($"Writes the piece into {item.ModName}. The file as it is now is backed up first (Backups tab).");
        ImGui.SameLine();
        float y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + 3 * S);
        if (Theme.SoftButton(FontAwesomeIcon.Undo, "Start over")) s.StartOver();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every edit back to none - the piece as it is in the mod.");
        if (_confirmSave) { ImGui.OpenPopup("Save it?###savepop"); _confirmSave = false; }
        bool open = true;
        if (ImGui.BeginPopupModal("Save it?###savepop", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Theme.IconText(FontAwesomeIcon.WandMagicSparkles, $"Write this into {item.ModName}?", Theme.Pink);
            ImGui.TextDisabled(item.RelPath);
            ImGui.Text("The file as it is now is backed up first - the Backups tab puts it back.");
            if (!s.Edit.Any) Theme.Wrapped("(You haven't changed anything yet - it would save the piece as it is.)", Theme.Peach);
            ImGui.Spacing();
            if (Theme.BigButton(FontAwesomeIcon.Heart, "Yes, save it")) { s.Save(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * S);
            if (Theme.SoftButton(FontAwesomeIcon.Times, "Not yet")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    // ------------------------------------------------------------------ Jiggle
    void DrawJiggle(Session s, WornModel item)
    {
        var y = s.Edit.Yas;
        bool changed = false;
        DrawBreastPhysics(item);
        Theme.Section(FontAwesomeIcon.WandMagicSparkles, "YAS jiggle");
        int mode = s.Edit.RemoveYas ? 2 : y.Enabled ? 1 : 0;
        if (Theme.Segmented("mode", new[] { "Leave as is", "Add YAS jiggle", "Take YAS off" }, ref mode,
                new[] { "The piece's weights as the mod made them.",
                        "Copy the jiggle from your body onto this piece. If it already had YAS, yours replaces it.",
                        "Every YAS bone's weight goes back to the bone it hangs from - it moves like a non-YAS piece." },
                new[] { FontAwesomeIcon.Leaf, FontAwesomeIcon.WandMagicSparkles, FontAwesomeIcon.Broom }))
        {
            y.Enabled = mode == 1;
            s.Edit.RemoveYas = mode == 2;
            changed = true;
        }
        if (mode == 0)
        {
            ImGui.Spacing();
            Theme.Wrapped("Pick \"Add YAS jiggle\" to copy the jiggle from the YAS body you're wearing onto this piece.", Theme.Muted);
        }
        else if (mode == 2)
        {
            ImGui.Spacing();
            Theme.Wrapped("The YAS jiggle comes off: each YAS bone's weight goes back to the bone it hangs from.", Theme.Muted);
        }
        else
        {
            if (_p.Body == null) Theme.Wrapped("Needs your body first - see the line under the title (or the My body tab).", Theme.Peach);
            changed |= DrawFit(s);
            Theme.Section(FontAwesomeIcon.SlidersH, "How much jiggle");
            changed |= Slider("Overall", FontAwesomeIcon.Heart, ref y.Master,
                "The whole piece - every area but the chest (the chest has its own slider, because the breast bone carries their size too).\n" +
                "100% = like the skin (the most), 0% = not at all. How bouncy the jiggle itself is comes from your skeleton / physics mod.");
            ImGui.Spacing();
            bool pecs = s.Model?.BoneNames.Any(b => b.StartsWith("iv_kyokin", StringComparison.Ordinal)) == true || (_p.Body?.HasBone("iv_kyokin") ?? false);
            changed |= AreaSliders(y, MainAreas, pecs);
            if (ImGui.TreeNode("More areas##more"))
            {
                changed |= AreaSliders(y, MoreAreas, pecs);
                ImGui.TreePop();
            }
            if (ImGui.TreeNode("Loose cloth##fade"))
            {
                Theme.Wrapped("Jiggle fades out as the cloth gets further from your skin - so loose skirts, coats and sleeves don't bounce like skin.", Theme.Muted);
                float a = y.FadeStart * 100, b = y.FadeEnd * 100;
                ImGui.SetNextItemWidth(220 * S);
                if (ImGui.SliderFloat("Full jiggle up to (cm)", ref a, 0, 20, "%.1f")) { y.FadeStart = a / 100; if (y.FadeEnd < y.FadeStart) y.FadeEnd = y.FadeStart; changed = true; }
                ImGui.SetNextItemWidth(220 * S);
                if (ImGui.SliderFloat("No jiggle past (cm)", ref b, 0, 30, "%.1f")) { y.FadeEnd = b / 100; if (y.FadeStart > y.FadeEnd) y.FadeStart = y.FadeEnd; changed = true; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Loose bits still bouncing with the skin? Lower this.");
                ImGui.TreePop();
            }
            if (ImGui.TreeNode("Extras##extras"))
            {
                bool a8 = y.Allow8;
                if (ImGui.Checkbox("Let a point hold 8 bones", ref a8)) { y.Allow8 = a8; changed = true; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Older pieces hold 4 bones per point; YAS often needs more. On: the piece is changed to 8 (the game supports it). Off: the 4 strongest are kept.");
                changed |= DrawFollowOtherBody(s);
                ImGui.TreePop();
            }
            ImGui.Spacing();
            if (ImGui.SmallButton("All sliders back to 100%"))
            {
                var d = new YasSettings();
                y.Strength = new Dictionary<YasRegion, float>(d.Strength);
                y.Master = d.Master;
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every area at 100% (like the skin) - Groin back to 0%.");
        }
        if (changed) s.Changed();
    }

    /// <summary>A slider row: icon, name, then the slider filling the line. Values are 0 - 1, shown as %.</summary>
    static bool Slider(string label, FontAwesomeIcon icon, ref float value, string tip)
    {
        ImGui.AlignTextToFramePadding();
        Theme.Icon(icon, Theme.Pink);
        ImGui.SameLine(0, 6 * S);
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        ImGui.SameLine(160 * S);
        ImGui.SetNextItemWidth(Math.Max(120 * S, ImGui.GetContentRegionAvail().X - 8 * S));
        float v = Math.Min(1f, value) * 100f;
        bool ch = ImGui.SliderFloat("##" + label, ref v, 0, 100, "%.0f%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip + "\nCtrl+click to type a number.");
        if (ch) value = Math.Clamp(v / 100f, 0f, 1f);
        return ch;
    }

    static bool AreaSliders(YasSettings y, (YasRegion r, string label, FontAwesomeIcon icon, string tip)[] areas, bool pecs)
    {
        bool changed = false;
        foreach (var (r, label, icon, tip) in areas)
        {
            float v = y.Strength.GetValueOrDefault(r, 1f);
            if (Slider(r == YasRegion.Chest && pecs ? "Chest / pecs" : label, icon, ref v, tip)) { y.Strength[r] = v; changed = true; }
        }
        return changed;
    }

    bool DrawFit(Session s)
    {
        var y = s.Edit.Yas;
        bool changed = false;
        Theme.Section(FontAwesomeIcon.Ribbon, "How it fits");
        int pick = Array.FindIndex(Fits, f => f.fit == (y.Fit == ClothFit.Same ? ClothFit.AsMade : y.Fit));
        if (Theme.Segmented("fit", Fits.Select(f => f.label).ToArray(), ref pick, Fits.Select(f => f.tip).ToArray(), Fits.Select(f => f.icon).ToArray()))
        {
            y.Fit = Fits[pick].fit;
            changed = true;
        }
        Theme.Wrapped(Fits[Math.Max(0, pick)].blurb, Theme.Muted);
        if (y.Fit == ClothFit.Soft)
        {
            float sv = y.Softness * 100f;
            ImGui.SetNextItemWidth(220 * S);
            if (ImGui.SliderFloat("How flowy##soft", ref sv, 10, 100, "%.0f%%"))
            {
                y.Softness = Math.Clamp(MathF.Round(sv / 10f) / 10f, 0.1f, 1f);
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"How wide each spot's jiggle is spread over the skin around it: {GearEditor.SoftRadius(y.Softness) * 100:0} cm now (100% = 12 cm).\nThe first time a width is used it takes a second or so to work out.");
        }
        if (s.Last is { } lr)
        {
            if (y.Fit == ClothFit.Tight || s.Edit.AnyTight)
                ImGui.TextDisabled(lr.TightPoints > 0
                    ? $"{lr.TightPoints:N0} points hug the skin" + (s.OtherSkinLabel.Length > 0 && lr.OtherSkinPoints > 0 ? $" ({lr.OtherSkinPoints:N0} on {s.OtherSkinLabel})." : ".")
                    : "Nothing of this piece is close to the skin.");
            else if (y.Fit == ClothFit.Soft && lr.SoftPoints > 0)
                ImGui.TextDisabled($"{lr.SoftPoints:N0} points are flowy.");
        }
        return changed;
    }

    /// <summary>Straps above the waist: a legs piece that reaches onto the upper body follows it there; a top that reaches
    /// down over the hips follows the lower body there.</summary>
    bool DrawFollowOtherBody(Session s)
    {
        string slot = s.Edit.Slot;
        if (slot != "Legs" && slot != "Chest") return false;
        string label = slot == "Legs" ? "Above the waist, follow my upper body" : "Below the waist, follow my lower body";
        bool on = s.Edit.FollowOtherBody, changed = false;
        if (ImGui.Checkbox(label + "##otherbody", ref on)) { s.Edit.FollowOtherBody = on; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip((slot == "Legs" ? "Straps, high waistbands, belts: wherever this piece lies on your upper body's skin"
                                                : "A long top's hem: wherever this piece lies on your lower body's skin") +
                             ", it takes that skin's weights exactly\n(eased in over 2 cm past the waist), so it moves with your " +
                             (slot == "Legs" ? "waist and back instead of your hips." : "hips and thighs instead of your waist.") +
                             "\nNeeds the body you're wearing (My body tab).");
        if (on && s.Last is { } lr)
            ImGui.TextDisabled(lr.OtherBodyPoints == 0 ? "Nothing of this piece lies past the waist." : $"{lr.OtherBodyPoints:N0} points follow the other half of your body.");
        return changed;
    }

    // ------------------------------------------------------------------ breast physics on a top (the game's switch, EQP)
    BreastPhysics.State? _bp; string _bpFor = "", _bpNote = ""; bool _bpBusy;

    void DrawBreastPhysics(WornModel item)
    {
        if (Eqp.TopSetId(item.GamePaths) < 0) return;
        if (_bpFor != item.FullPath && !_bpBusy)
        {
            _bpFor = item.FullPath; _bp = null; _bpBusy = true;
            string modRoot = Path.Combine(_p.Penumbra.ModDirectory(), item.ModDir);
            Task.Run(() =>
            {
                try { var t = _p.EqpTable(); _bp = t == null ? null : BreastPhysics.Read(t, modRoot, item.RelPath, item.GamePaths); }
                catch (Exception e) { _bpNote = "Couldn't read the game's chest switch: " + e.Message; }
                finally { _bpBusy = false; }
            });
        }
        if (_bpNote.Length > 0) Theme.Wrapped(_bpNote, _bpNote.StartsWith("Done") ? Theme.Mint : Theme.Peach);
        if (_bp is not { } bp) return;
        bool busy = _bpBusy;      // read once: the click sets it before EndDisabled
        if (!bp.Off)
        {
            if (busy) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Keep my chest still in this top##bpoff")) SetBreastPhysics(item, false);
            if (busy) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("For a stiff top (corset, armour, a snug bra): your breasts don't bounce while you wear it, and keep their size -\n" +
                                 "unlike a low Chest slider, which also stops the top following your bust size.\n" +
                                 $"Adds an EQP edit to \"{bp.Option}\" in {item.ModName} (the game's switches for e{bp.SetId:D4}).\n" +
                                 "The file is backed up first (Backups tab). Every outfit using this item gets it.");
            return;
        }
        Theme.IconText(FontAwesomeIcon.HeartCircleExclamation, $"The game keeps your chest still in this top (item e{bp.SetId:D4}) - it won't bounce, whatever its weights.", Theme.Peach, Theme.Peach);
        if (busy) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Let it bounce##bp")) SetBreastPhysics(item, true);
        if (busy) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Adds an EQP edit to \"{bp.Option}\" in {item.ModName}: the game's switches for e{bp.SetId:D4} with\n" +
                             "'Disable Breast Physics' off (the same as Penumbra's Advanced Editing > Meta Manipulations).\n" +
                             "The file is backed up first (Backups tab). Every outfit using this item gets it.");
    }

    void SetBreastPhysics(WornModel item, bool on)
    {
        _bpBusy = true; _bpNote = "";
        string modRoot = Path.Combine(_p.Penumbra.ModDirectory(), item.ModDir);
        Task.Run(() =>
        {
            try
            {
                var t = _p.EqpTable() ?? throw new IOException("the game's equipment table couldn't be read");
                var fix = BreastPhysics.Set(t, modRoot, item.RelPath, item.GamePaths, on);
                if (fix is { } f)
                {
                    if (f.before.Length > 0)
                        _p.Backups.SaveSet(modRoot, item.ModDir, item.ModName, new Dictionary<string, byte[]> { [f.file] = f.now },
                                           new Dictionary<string, string> { [f.file] = BackupStore.Sha(f.before) }, on ? "chest bounce turned on" : "chest kept still");
                    else BackupStore.WriteAtomic(Path.Combine(modRoot, f.file), f.now);
                }
                _bp = BreastPhysics.Read(t, modRoot, item.RelPath, item.GamePaths);
                _p.Framework.RunOnFrameworkThread(() =>
                {
                    _p.Penumbra.ReloadMod(item.ModDir);
                    _p.Penumbra.RedrawPlayer();
                    _bpNote = _bp is { } now && now.Off != on ? (on ? "Done - this top bounces now (saved into its mod)." : "Done - your chest stays still in this top (saved into its mod).")
                                                              : "It didn't change - another mod may set this item's switches.";
                    _status.Refresh(_worn, _p.Backups, _p.EqpTable(), _p.Penumbra.ModDirectory());
                });
            }
            catch (Exception e) { _bpNote = "Couldn't change the chest switch: " + e.Message; }
            finally { _bpBusy = false; }
        });
    }

    // ------------------------------------------------------------------ Refit (Neolithe shapes)
    void DrawRefit(Session s)
    {
        Theme.Section(FontAwesomeIcon.RulerCombined, "Refit to another body shape");
        Theme.Wrapped("Made for one Neolithe shape but you wear another? Refit moves the piece onto the new shape: its skin lands exactly on yours, " +
                      "and the cloth keeps its distance over it. Refit and jiggle work together - set both, then save once.", Theme.Muted);
        ImGui.Spacing();
        if (s.Sizes.Count < 2)
        {
            Theme.Wrapped(s.SizesBusy ? "Reading your body mod's shapes..." : s.SizesNote.Length > 0 ? s.SizesNote
                : "Refit works on tops and bottoms, with the shapes of the body mod you're wearing (My body tab: \"Use the body I'm wearing\").", Theme.Peach);
            return;
        }
        var e = s.Edit;
        ImGui.TextDisabled($"{s.Sizes.Count} shapes in {s.SizesMod} for {(s.Item?.SlotName == "Chest" ? "the chest" : "the legs")}." +
                           (s.YourSize >= 0 ? $" You wear {s.Sizes[s.YourSize].Label}." : ""));
        ImGui.Spacing();
        // a combo over the shapes, under their sections
        bool Pick(string id, string current, bool allowNone, out string picked)
        {
            picked = current;
            bool changed = false;
            ImGui.SetNextItemWidth(Math.Min(380 * S, ImGui.GetContentRegionAvail().X - 120 * S));
            string shown = current.Length == 0 ? (allowNone ? "Don't refit" : "(pick)") : s.SizeLabel(current);
            if (ImGui.BeginCombo(id, shown, ImGuiComboFlags.HeightLarge))
            {
                if (allowNone && ImGui.Selectable("Don't refit", current.Length == 0)) { picked = ""; changed = true; }
                string section = "\0";
                for (int i = 0; i < s.Sizes.Count; i++)
                {
                    var o = s.Sizes[i];
                    if (o.Section != section) { section = o.Section; if (section.Length > 0) ImGui.TextColored(Theme.Pink, "  " + section); }
                    string tag = i == s.YourSize ? "   (you wear this)" : "";
                    var mf = s.MadeFor.Count > 0 && s.MadeFor[0].idx == i ? "   (made for)" : "";
                    if (ImGui.Selectable($"{o.Name}{tag}{mf}##{id}{i}", string.Equals(o.RelPath, current, StringComparison.OrdinalIgnoreCase)))
                    { picked = o.RelPath; changed = true; }
                }
                ImGui.EndCombo();
            }
            return changed;
        }
        ImGui.AlignTextToFramePadding();
        Theme.IconText(FontAwesomeIcon.Tshirt, "Made for", Theme.Lilac);
        ImGui.SameLine(110 * S);
        if (Pick("##rf", e.RefitFrom, false, out var from)) s.SetRefit(from, e.RefitTo);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The body shape this piece was made on. Yassify works it out from the piece's own skin\n(or, a piece with no skin, how its cloth sits) - change it if you know better.");
        if (s.SizesBusy) ImGui.TextDisabled($"Working out which shape it was made for... {s.SizesDone} / {s.Sizes.Count}");
        else if (s.MadeFor.Count > 0)
        {
            var (bi, bsc, kind) = s.MadeFor[0];
            ImGui.TextDisabled($"Best guess: {s.Sizes[bi].Label}" + (kind == "skin" ? $" (its skin is {bsc * 1000:0.00} mm off that shape)." : " (by how its cloth sits - it has no skin of its own)."));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Closest shapes:\n" + string.Join("\n", s.MadeFor.Take(6).Select(x => $"{s.Sizes[x.idx].Label}: {(x.kind == "skin" ? $"{x.score * 1000:0.00} mm" : $"{x.score * 1000:0.0}")}")));
        }
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        Theme.IconText(FontAwesomeIcon.WandMagicSparkles, "Refit to", Theme.Pink);
        ImGui.SameLine(110 * S);
        if (Pick("##rt", e.RefitTo, true, out var to)) s.SetRefit(e.RefitFrom, to);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The shape to move the piece to - your own, or any other (like Hazelnut).");
        if (s.YourSize >= 0 && !string.Equals(e.RefitTo, s.Sizes[s.YourSize].RelPath, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("My shape##rfyou")) s.SetRefit(e.RefitFrom, s.Sizes[s.YourSize].RelPath);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Refit to the shape you wear: {s.Sizes[s.YourSize].Label}.");
        }
        ImGui.Spacing();
        if (e.RefitTo.Length > 0 && string.Equals(e.RefitTo, e.RefitFrom, StringComparison.OrdinalIgnoreCase))
            Theme.Wrapped("It's made for that shape already - nothing to do!", Theme.Mint);
        else if (s.RefitLoading) ImGui.TextDisabled("Loading the two shapes...");
        else if (e.RefitOn && s.Last is { } lr)
            Theme.Wrapped(lr.RefitPoints > 0 ? $"{lr.RefitPoints:N0} points moved, up to {lr.RefitMost * 100:0.0} cm. Save into my mod to keep it." : "The two shapes are the same where this piece is.", Theme.Mint);
    }

    // ------------------------------------------------------------------ Parts
    void DrawParts(Session s)
    {
        var m = s.Model;
        if (m == null) return;
        Theme.Section(FontAwesomeIcon.PuzzlePiece, "Each part");
        Theme.Wrapped("Stiff bits (belts, buckles, armour plates, jewellery): set their jiggle to 0%. A dress with a tight top and a flowy skirt: " +
                      "give each part its own fit. Hover a part's name to see its material.", Theme.Muted);
        if (!s.Edit.Yas.Enabled && !s.Edit.RemoveYas) Theme.Wrapped("These work with \"Add YAS jiggle\" on the Jiggle tab.", Theme.Peach);
        ImGui.Spacing();
        var lod0 = m.LodMeshes(0).ToList();
        bool changed = false;
        if (ImGui.BeginTable("##parts", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Jiggle", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Fit", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26 * S);
            ImGui.TableHeadersRow();
            for (int k = 0; k < lod0.Count; k++)
            {
                int mi = lod0[k];
                var me = m.Meshes[mi];
                string mat = me.MaterialIndex < m.MaterialNames.Count ? m.MaterialNames[me.MaterialIndex] : "";
                bool skin = BodySurface.IsSkinMaterial(mat);
                s.Edit.Meshes.TryGetValue(k, out var e);
                ImGui.PushID(k);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"Part {k + 1}" + (skin ? " (skin)" : ""));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{Path.GetFileName(mat)}\n{me.VertexCount:N0} points");
                ImGui.TableNextColumn();
                float j = e == null ? 100f : e.NoYas ? 0f : Math.Min(1f, e.Jiggle) * 100f;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##j", ref j, 0, 100, "%.0f%%"))
                {
                    e ??= s.Edit.Meshes[k] = new MeshEdit();
                    e.Jiggle = Math.Clamp(j / 100f, 0f, 1f); e.NoYas = false; changed = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much this part follows the body's jiggle, on top of the sliders.\n0% = none - any jiggle it had comes off. 100% = like the skin.");
                ImGui.TableNextColumn();
                if (skin) ImGui.TextDisabled("keeps its own");
                else
                {
                    var cur = e?.Fit ?? ClothFit.Same;
                    string FitName(ClothFit f) => f == ClothFit.Same ? "Like the rest" : Fits.First(x => x.fit == f).label;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##fit", FitName(cur)))
                    {
                        foreach (var f in new[] { ClothFit.Same, ClothFit.AsMade, ClothFit.Tight, ClothFit.Soft })
                        {
                            if (ImGui.Selectable(FitName(f), cur == f)) { e ??= s.Edit.Meshes[k] = new MeshEdit(); e.Fit = f; changed = true; }
                            if (f != ClothFit.Same && ImGui.IsItemHovered()) ImGui.SetTooltip(Fits.First(x => x.fit == f).tip);
                        }
                        ImGui.EndCombo();
                    }
                }
                ImGui.TableNextColumn();
                if (e != null && (e.Jiggle != 1f || e.NoYas || e.Fit != ClothFit.Same))
                {
                    if (ImGuiComponents.IconButton("partreset", FontAwesomeIcon.Undo)) { s.Edit.Meshes.Remove(k); changed = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("This part back to how the rest of the piece is.");
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (changed) s.Changed();
    }

    // ------------------------------------------------------------------ My body
    void DrawBody()
    {
        Theme.Section(FontAwesomeIcon.PersonHalfDress, "The body the jiggle comes from");
        Theme.Wrapped("Yassify copies the jiggle from a YAS body - normally the one you're wearing (your Neolithe YAS AIO, for example). " +
                      "Wear it in Penumbra with the YAS skeleton, then click below. Click again after you change your body mod or its options.", Theme.Muted);
        ImGui.Spacing();
        bool worn = _p.Config.BodyWorn;
        if (Theme.BigButton(FontAwesomeIcon.PersonHalfDress, worn && _p.Body != null ? "Look again at the body I'm wearing" : "Use the body I'm wearing", !_p.BodyLoading))
            UseWornBody();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Asks Penumbra which chest, legs, hands and feet body models your character has on right now -\n" +
            "whatever body mod and options are turned on.");
        ImGui.Spacing();
        Theme.Wrapped(_p.BodyStatus, _p.Body != null ? Theme.Mint : Theme.Peach);
        if (worn && _p.WornBody.Count > 0 && ImGui.BeginTable("##wornbody", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 70 * S);
            ImGui.TableSetupColumn("Mod");
            ImGui.TableSetupColumn("File");
            ImGui.TableHeadersRow();
            foreach (var f in _p.WornBody)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(f.Slot);
                ImGui.TableNextColumn(); ImGui.Text(f.ModName);
                ImGui.TableNextColumn(); ImGui.TextDisabled(Path.GetFileName(f.File));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(f.GamePath + "\n" + f.File);
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Or pick the body models by hand", worn ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextWrapped("Pick the mod with your YAS body, then tick its chest, legs, hands and feet models.");
        var mods = _p.Penumbra.ModList();
        ImGui.SetNextItemWidth(300 * S);
        ImGui.InputTextWithHint("##modsearch", "Search mods...", ref _modFilter, 128);
        if (ImGui.BeginChild("##mods", new Vector2(0, 150 * S), true))
        {
            foreach (var (dir, name) in mods.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (_modFilter.Length > 0 && !name.Contains(_modFilter, StringComparison.OrdinalIgnoreCase) && !dir.Contains(_modFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable($"{name}##{dir}", dir == _bodyMod)) { _bodyMod = dir; Scan(); }
            }
        }
        ImGui.EndChild();
        if (_bodyMod.Length == 0) return;
        ImGui.Text("Models in " + mods.GetValueOrDefault(_bodyMod, _bodyMod) + ":");
        if (_cands.Count == 0 && _scanNote.Length == 0) Scan();
        bool scanning = _scanNote.StartsWith("Looking");
        if (_scanNote.Length > 0) ImGui.TextWrapped(_scanNote);
        if (ImGui.BeginChild("##cands", new Vector2(0, 150 * S), true))
        {
            foreach (var c in _cands)
            {
                bool on = _picked.Contains(c.RelPath);
                if (ImGui.Checkbox($"{c.Slot}: {c.RelPath}##{c.RelPath}", ref on)) { if (on) _picked.Add(c.RelPath); else _picked.Remove(c.RelPath); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{c.Points:N0} points, {c.YasBones} YAS bones{(c.Race.Length > 0 ? ", " + c.Race : "")}");
            }
        }
        ImGui.EndChild();
        bool can = _picked.Count > 0 && !_p.BodyLoading && !scanning;
        if (!can) ImGui.BeginDisabled();
        if (ImGui.Button($"Use these ({_picked.Count})##usepicked"))
        {
            _p.Config.BodyModDir = _bodyMod;
            _p.Config.BodyFiles = _picked.ToList();
            _p.Config.BodyWorn = false;
            _p.WornBody = new();
            _p.SaveConfig();
            _p.LoadBody();
        }
        if (!can) ImGui.EndDisabled();
    }

    void Scan()
    {
        _cands = new(); _picked = new(); _scanNote = "Looking through the mod...";
        var mod = _bodyMod;
        var root = Path.Combine(_p.Penumbra.ModDirectory(), mod);
        bool same = mod == _p.Config.BodyModDir;
        var saved = _p.Config.BodyFiles.ToList();
        Task.Run(() =>
        {
            var problems = new List<string>();
            var cands = BodyFiles.Scan(root, problems.Add);
            var picked = new HashSet<string>();
            foreach (var f in same ? saved : BodyFiles.DefaultPick(cands))
                if (cands.Any(c => c.RelPath == f)) picked.Add(f);
            string note = cands.Count == 0 ? "No body models with YAS bones in this mod." : "";
            if (problems.Count > 0) note += (note.Length > 0 ? " " : "") + $"{problems.Count} model(s) couldn't be read.";
            _p.Framework.RunOnFrameworkThread(() =>
            {
                if (_bodyMod != mod) return;
                _cands = cands; _picked = picked; _scanNote = note;
            });
        });
    }

    // ------------------------------------------------------------------ Backups
    void DrawBackups()
    {
        Theme.Section(FontAwesomeIcon.History, "Backups");
        Theme.Wrapped("Every save keeps the file as it was before. Restore puts it back (and backs up the file it replaces, so you can change your mind again).", Theme.Muted);
        var item = _p.Session.Item;
        if (item != null) ImGui.Checkbox("Show every backup (not just this piece)", ref _allBackups);
        else _allBackups = true;
        var list = _allBackups || item == null ? _p.Backups.List() : _p.Backups.List(item.ModDir, item.RelPath);
        int keep = _p.Config.KeepBackups;
        ImGui.SetNextItemWidth(110 * S);
        if (ImGui.InputInt("Backups kept per file", ref keep)) { _p.Config.KeepBackups = Math.Clamp(keep, 1, 200); _p.SaveConfig(); }
        ImGui.TextDisabled(_p.Backups.Root);
        if (list.Count == 0) { ImGui.Spacing(); Theme.Wrapped("No backups yet - one is made every time you save.", Theme.Lilac); return; }
        bool openRestore = false;
        if (ImGui.BeginTable("##bk", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH, new Vector2(0, 0)))
        {
            ImGui.TableSetupColumn("Saved", ImGuiTableColumnFlags.WidthFixed, 120 * S);
            ImGui.TableSetupColumn("Mod / file");
            ImGui.TableSetupColumn("What changed after", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90 * S);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var e in list)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(e.Saved.ToString("d MMM HH:mm"));
                ImGui.TableNextColumn(); ImGui.Text(e.ModName);
                if (e.IsSet) ImGui.TextDisabled($"{e.Files.Count} files: " + string.Join(", ", e.Files.Take(3).Select(Path.GetFileName)) + (e.Files.Count > 3 ? ", ..." : ""));
                else ImGui.TextDisabled(e.File);
                ImGui.TableNextColumn(); ImGui.TextWrapped(e.Note);
                ImGui.TableNextColumn();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, $"Restore##{e.Folder}")) { _confirmRestore = e; openRestore = true; }
            }
            ImGui.EndTable();
        }
        if (openRestore) ImGui.OpenPopup("Put it back?###restorepop");
        bool open = true;
        if (_confirmRestore != null && ImGui.BeginPopupModal("Put it back?###restorepop", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var e = _confirmRestore;
            ImGui.Text(e.IsSet ? $"Put back {e.Files.Count} files in {e.ModName}" : $"Put back {e.File} in {e.ModName}");
            ImGui.Text($"as it was before {e.Saved:g}?");
            ImGui.TextDisabled("The file as it is now is backed up first.");
            ImGui.Spacing();
            if (Theme.BigButton(FontAwesomeIcon.Undo, "Put it back"))
            {
                try
                {
                    var root = Path.Combine(_p.Penumbra.ModDirectory(), e.Mod);
                    _p.Backups.Restore(e, root);
                    _p.Penumbra.StopPreview();
                    _p.Penumbra.ReloadMod(e.Mod);
                    _p.Penumbra.RedrawPlayer();
                    var restored = e.IsSet ? e.Files : new List<string> { e.File };
                    var s = _p.Session;
                    if (s.Item != null && s.Item.ModDir == e.Mod && restored.Any(r => string.Equals(r.Replace('\\', '/'), s.Item.RelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                        s.Reload();
                    s.Status = e.IsSet ? $"Put back {e.Files.Count} files in {e.ModName}." : $"Put back {e.File}.";
                    _status.Refresh(_worn, _p.Backups, _p.EqpTable(), _p.Penumbra.ModDirectory());
                    _bpFor = "";
                }
                catch (Exception ex) { _p.Session.Error = "Restore failed: " + ex.Message; }
                _confirmRestore = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * S);
            if (Theme.SoftButton(FontAwesomeIcon.Times, "Cancel")) { _confirmRestore = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }
}
