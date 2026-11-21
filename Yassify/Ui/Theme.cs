// Yassify - the look: soft pinks and lilac on a dark plum, rounded everything. Pushed around each of the plugin's own
// windows (PreDraw / PostDraw), so the rest of the game's UI is left alone.
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Yassify.Ui;

public static class Theme
{
    // the palette
    public static readonly Vector4 Pink = Rgb(0xFF8FC8);
    public static readonly Vector4 HotPink = Rgb(0xF2559E);
    public static readonly Vector4 Rose = Rgb(0xFF6F91);
    public static readonly Vector4 Lilac = Rgb(0xC9A7FF);
    public static readonly Vector4 Mint = Rgb(0x8FF0C4);
    public static readonly Vector4 Peach = Rgb(0xFFC58F);
    public static readonly Vector4 Cream = Rgb(0xFFF4FA);
    public static readonly Vector4 Muted = Rgb(0xB9A3BF);
    public static readonly Vector4 Plum = Rgb(0x211826);
    public static readonly Vector4 PlumLight = Rgb(0x2C2033);
    public static readonly Vector4 PlumFrame = Rgb(0x3B2A44);

    public static Vector4 Rgb(uint hex, float a = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);
    public static Vector4 A(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
    public static uint U(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    static readonly (ImGuiCol col, Vector4 v)[] Colors =
    {
        (ImGuiCol.Text, Cream),
        (ImGuiCol.TextDisabled, Muted),
        (ImGuiCol.WindowBg, A(Plum, 0.97f)),
        (ImGuiCol.ChildBg, A(PlumLight, 0.85f)),
        (ImGuiCol.PopupBg, A(PlumLight, 0.98f)),
        (ImGuiCol.Border, A(Pink, 0.28f)),
        (ImGuiCol.FrameBg, PlumFrame),
        (ImGuiCol.FrameBgHovered, Rgb(0x4A3356)),
        (ImGuiCol.FrameBgActive, Rgb(0x573B66)),
        (ImGuiCol.TitleBg, Rgb(0x3A1F3A)),
        (ImGuiCol.TitleBgActive, Rgb(0x8E3B78)),
        (ImGuiCol.TitleBgCollapsed, Rgb(0x3A1F3A, 0.8f)),
        (ImGuiCol.ScrollbarBg, A(Plum, 0.4f)),
        (ImGuiCol.ScrollbarGrab, A(Pink, 0.35f)),
        (ImGuiCol.ScrollbarGrabHovered, A(Pink, 0.55f)),
        (ImGuiCol.ScrollbarGrabActive, A(Pink, 0.75f)),
        (ImGuiCol.CheckMark, Pink),
        (ImGuiCol.SliderGrab, Pink),
        (ImGuiCol.SliderGrabActive, Rgb(0xFFC2E2)),
        (ImGuiCol.Button, A(HotPink, 0.75f)),
        (ImGuiCol.ButtonHovered, A(Pink, 0.9f)),
        (ImGuiCol.ButtonActive, Rgb(0xFFB3D9)),
        (ImGuiCol.Header, A(Lilac, 0.28f)),
        (ImGuiCol.HeaderHovered, A(Lilac, 0.42f)),
        (ImGuiCol.HeaderActive, A(Lilac, 0.55f)),
        (ImGuiCol.Separator, A(Pink, 0.25f)),
        (ImGuiCol.SeparatorHovered, A(Pink, 0.5f)),
        (ImGuiCol.SeparatorActive, Pink),
        (ImGuiCol.ResizeGrip, A(Pink, 0.25f)),
        (ImGuiCol.ResizeGripHovered, A(Pink, 0.6f)),
        (ImGuiCol.ResizeGripActive, Pink),
        (ImGuiCol.Tab, Rgb(0x3A2742)),
        (ImGuiCol.TabHovered, A(Pink, 0.85f)),
        (ImGuiCol.TabActive, A(HotPink, 0.85f)),
        (ImGuiCol.TabUnfocused, Rgb(0x2E2034)),
        (ImGuiCol.TabUnfocusedActive, A(HotPink, 0.55f)),
        (ImGuiCol.TableHeaderBg, Rgb(0x3A2742)),
        (ImGuiCol.TableBorderStrong, A(Pink, 0.25f)),
        (ImGuiCol.TableBorderLight, A(Pink, 0.12f)),
        (ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0)),
        (ImGuiCol.TableRowBgAlt, A(Lilac, 0.05f)),
        (ImGuiCol.TextSelectedBg, A(Pink, 0.35f)),
        (ImGuiCol.ModalWindowDimBg, Rgb(0x120A14, 0.6f)),
        (ImGuiCol.PlotHistogram, Pink),
    };

    static readonly (ImGuiStyleVar v, float f)[] Floats =
    {
        (ImGuiStyleVar.WindowRounding, 12f),
        (ImGuiStyleVar.ChildRounding, 10f),
        (ImGuiStyleVar.FrameRounding, 8f),
        (ImGuiStyleVar.PopupRounding, 10f),
        (ImGuiStyleVar.GrabRounding, 8f),
        (ImGuiStyleVar.TabRounding, 8f),
        (ImGuiStyleVar.ScrollbarRounding, 8f),
        (ImGuiStyleVar.GrabMinSize, 12f),
        (ImGuiStyleVar.WindowBorderSize, 1f),
        (ImGuiStyleVar.ChildBorderSize, 1f),
    };

    static readonly (ImGuiStyleVar v, Vector2 f)[] Vecs =
    {
        (ImGuiStyleVar.WindowPadding, new Vector2(12, 12)),
        (ImGuiStyleVar.FramePadding, new Vector2(8, 5)),
        (ImGuiStyleVar.ItemSpacing, new Vector2(8, 6)),
        (ImGuiStyleVar.CellPadding, new Vector2(6, 4)),
    };

    public static void Push()
    {
        foreach (var (c, v) in Colors) ImGui.PushStyleColor(c, v);
        foreach (var (v, f) in Floats) ImGui.PushStyleVar(v, f * (v is ImGuiStyleVar.WindowBorderSize or ImGuiStyleVar.ChildBorderSize ? 1f : ImGuiHelpers.GlobalScale));
        foreach (var (v, f) in Vecs) ImGui.PushStyleVar(v, f * ImGuiHelpers.GlobalScale);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(Floats.Length + Vecs.Length);
        ImGui.PopStyleColor(Colors.Length);
    }

    // ------------------------------------------------------------------ little drawing helpers

    /// <summary>A FontAwesome icon, inline with the text after it.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (color is { } c) ImGui.TextColored(c, icon.ToIconString()); else ImGui.TextUnformatted(icon.ToIconString());
        ImGui.PopFont();
    }

    /// <summary>An icon then a line of text, on one line.</summary>
    public static void IconText(FontAwesomeIcon icon, string text, Vector4? iconColor = null, Vector4? textColor = null)
    {
        Icon(icon, iconColor);
        ImGui.SameLine(0, 6 * ImGuiHelpers.GlobalScale);
        if (textColor is { } t) ImGui.TextColored(t, text); else ImGui.TextUnformatted(text);
    }

    /// <summary>A header with a pink icon and a thin line under it.</summary>
    public static void Section(FontAwesomeIcon icon, string title)
    {
        ImGui.Spacing();
        IconText(icon, title, Pink, Cream);
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(p, p + new Vector2(w, 0), U(A(Pink, 0.22f)), 1f);
        ImGui.Dummy(new Vector2(0, 2 * ImGuiHelpers.GlobalScale));
    }

    /// <summary>Wrapped text in a colour.</summary>
    public static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>A small rounded tag (YAS, saved...) drawn at a screen position; returns its width.</summary>
    public static float Pill(ImDrawListPtr dl, Vector2 at, string text, Vector4 bg, Vector4 fg)
    {
        var ts = ImGui.CalcTextSize(text);
        var pad = new Vector2(7, 2) * ImGuiHelpers.GlobalScale;
        var size = ts + pad * 2;
        dl.AddRectFilled(at, at + size, U(bg), size.Y / 2);
        dl.AddText(at + pad, U(fg), text);
        return size.X;
    }

    public static float PillWidth(string text) => ImGui.CalcTextSize(text).X + 14 * ImGuiHelpers.GlobalScale;

    /// <summary>Buttons in a row where one is picked (like radio buttons, but chunkier). Returns true when the pick changed.</summary>
    public static bool Segmented(string id, string[] labels, ref int picked, string[]? tips = null, FontAwesomeIcon[]? icons = null)
    {
        bool changed = false;
        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
            bool on = picked == i;
            ImGui.PushStyleColor(ImGuiCol.Button, on ? A(HotPink, 0.9f) : PlumFrame);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? A(Pink, 0.95f) : Rgb(0x4A3356));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, on ? Rgb(0xFFB3D9) : Rgb(0x573B66));
            ImGui.PushStyleColor(ImGuiCol.Text, on ? Rgb(0xFFFFFF) : Cream);
            bool clicked = icons != null && i < icons.Length
                ? Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icons[i], labels[i] + "##" + id + i)
                : ImGui.Button(labels[i] + "##" + id + i);
            ImGui.PopStyleColor(4);
            if (clicked && picked != i) { picked = i; changed = true; }
            if (tips != null && i < tips.Length && tips[i].Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tips[i]);
        }
        return changed;
    }

    /// <summary>A big friendly button in the main pink (Save).</summary>
    public static bool BigButton(FontAwesomeIcon icon, string label, bool enabled = true)
    {
        if (!enabled) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, HotPink);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Pink);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0xFFB3D9));
        ImGui.PushStyleColor(ImGuiCol.Text, Rgb(0xFFFFFF));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14, 8) * ImGuiHelpers.GlobalScale);
        bool r = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, label);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        if (!enabled) ImGui.EndDisabled();
        return r && enabled;
    }

    /// <summary>A quiet button (frame-coloured) with an icon.</summary>
    public static bool SoftButton(FontAwesomeIcon icon, string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, PlumFrame);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0x4A3356));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0x573B66));
        bool r = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, label);
        ImGui.PopStyleColor(3);
        return r;
    }

    /// <summary>Cuts text to fit a width, with "..." at the end.</summary>
    public static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string dots = "...";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text.Substring(0, mid) + dots).X <= width) lo = mid; else hi = mid - 1;
        }
        return text.Substring(0, Math.Max(0, lo)).TrimEnd() + dots;
    }
}
