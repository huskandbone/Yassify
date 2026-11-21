// Yassify - the how-to: a small window with the steps, what the fits mean, refits, and what to do if something's off.
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Yassify.Ui;

public sealed class HelpWindow : Window
{
    readonly Plugin _p;
    static float S => ImGuiHelpers.GlobalScale;

    public HelpWindow(Plugin p) : base("How to yassify###YassifyHelp")
    {
        _p = p;
        Size = new Vector2(520, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 300), MaximumSize = new Vector2(1400, 2000) };
    }

    public override void PreDraw() => Theme.Push();
    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        Part(FontAwesomeIcon.Heart, "Before you start",
            "In Penumbra, turn on your YAS body (for example the Neolithe YAS AIO), the YAS skeleton, and the outfit. Wear the outfit.",
            "Yassify finds the body you're wearing by itself. If the line under the title isn't green, open My body and click \"Use the body I'm wearing\".");

        Part(FontAwesomeIcon.WandMagicSparkles, "Yassify a piece",
            "Dress up: click a piece of your outfit on the left (tops, bottoms, gloves, shoes).",
            "Jiggle: pick \"Add YAS jiggle\". Your body's jiggle is copied onto the piece and shows on you a moment later.",
            "Pick how it fits (below), then play with the sliders. 100% = moves like your skin, 0% = no jiggle.",
            "Check it: run and stop, or dance, and watch from the side and back. \"Peek at the original\" flips back to compare.",
            "Happy? \"Save into my mod\". Then do the next piece.");

        Part(FontAwesomeIcon.Ribbon, "How it fits",
            "As made - keeps the mod's own weights and adds the jiggle on top. Good for most things.",
            "Hugs the skin - shorts, leggings, bodysuits, straps. The cloth copies the skin under it, so nothing pokes through when you bounce.",
            "Flowy - dresses, skirts, baggy pants. They drape over the butt and between the legs instead of hugging every curve.",
            "Loose bits still bouncing like skin? Jiggle > Loose cloth > lower \"No jiggle past\".");

        Part(FontAwesomeIcon.PuzzlePiece, "Parts",
            "Stiff bits (belts, buckles, armour plates, jewellery): Parts tab, set that part's jiggle to 0%.",
            "A dress with a tight top and a flowy skirt: give each part its own fit.");

        Part(FontAwesomeIcon.RulerCombined, "Refit (Neolithe shapes)",
            "Tops and bottoms made for one body shape can be moved to another - XS to L, Almond, Hazelnut, Macadamia, Pushup, Neobelly, Buff...",
            "Refit tab: \"Made for\" is worked out for you (change it if you know better). Pick \"Refit to\" - or click \"My shape\".",
            "The piece's skin lands exactly on the new shape; cloth keeps its distance over it. Set the refit and the jiggle, then save once.",
            "It works with body mods whose sizes are the same mesh in different shapes, like Neolithe.");

        Part(FontAwesomeIcon.HandHoldingHeart, "Good to know",
            "Nothing is written into your mods until you press Save. The live view is a temporary Penumbra mod.",
            "Give a top and its bottoms the same Belly value so they meet nicely at the waist.",
            "The chest slider also changes how a top follows your bust size. For a stiff top that keeps its size, use \"Keep my chest still in this top\" instead.",
            "Yassify never changes your body or physics files - how bouncy things are comes from your YAS skeleton / physics mod.");

        Part(FontAwesomeIcon.Undo, "Oops",
            "Before saving: \"Start over\" clears every edit.",
            "After saving: Backups > Restore puts the old file back.",
            "Nothing in the list? Make sure the outfit comes from a Penumbra mod and you're wearing it, then click the refresh button.");

        ImGui.Spacing();
        ImGui.TextDisabled("Yassify by Husk & Bone  -  /yassify to open, /yassify help for this.");
    }

    static void Part(FontAwesomeIcon icon, string title, params string[] lines)
    {
        Theme.Section(icon, title);
        foreach (var l in lines)
        {
            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float h = ImGui.GetTextLineHeight();
            dl.AddCircleFilled(p + new Vector2(6 * S, h / 2), 2.5f * S, Theme.U(Theme.Pink));
            ImGui.SetCursorScreenPos(p + new Vector2(16 * S, 0));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextWrapped(l);
            ImGui.PopTextWrapPos();
        }
        ImGui.Spacing();
    }
}
