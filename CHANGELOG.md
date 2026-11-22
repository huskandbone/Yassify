# Changelog

## 0.1.2

Fixes the chest switch ("Let it bounce" / "Keep my chest still in this top") doing nothing on a current Penumbra. Please update, then press "Put them right" if Yassify shows it.

What happened

You flipped the chest switch on a top, Yassify said it was saved, and in game nothing changed.

What we found

Penumbra used to keep a mod's options in several files: a default_mod.json and one group file per option group. A current Penumbra keeps them all in the mod's meta.json, and leaves the old files behind renamed .bak. Yassify only knew the older layout. On a mod in the new one it found no options, started a default_mod.json of its own and put the switch there - a file Penumbra no longer opens. So the switch was saved, and never read.

Jiggle, fits, sliders and refits were not affected: those change the model file itself.

What 0.1.2 changes

- The chest switch is saved into meta.json, in the option that brings the top (or the mod's default option). Everything else in meta.json stays as it was.
- Switches an older Yassify left in a default_mod.json are found when you open the window. One button, "Put them right", moves them into each mod's own options, with a backup. Nothing is deleted - the old file stays where it is, Penumbra ignores it.
- Refit reads your body's shapes from meta.json only on such mods, never from a leftover old file.
- Mods still in the older layout work as before.

What to do

1. Update Yassify (Dalamud does it for you, or /xlplugins > Installed > update).
2. If a line about chest switches shows under the banner, press "Put them right".
3. No line means nothing was waiting.

This was checked on the option files themselves: a test mod in the new layout, and 23 real mods read and written back unchanged. It hasn't had a full run in game yet - if a switch still doesn't take, please tell us which top.

## 0.1.1

Fixes creased skin after "Add YAS jiggle". Please update, then re-save the pieces you made with 0.1.0 (see "What to do").

What happened

After adding YAS jiggle to a top, the skin over the chest came out creased in game. The same top without YAS was fine. The culprit turned out to be the option "Let a point hold 8 bones" (Jiggle > Extras), which is on by default.

What we found

Every point of a model keeps two lists: the bones it follows, and how strongly it follows each one (its weights). Many pieces keep 4 of each per point. "Let a point hold 8 bones" changes both lists to 8, so YAS bones have room next to the ones already there.

Models store those 4 weights in one of two formats. Yassify 0.1.0 only recognised one of them. On a piece that uses the other, it changed the bone list to 8 and left the weights at 4. A model whose two lists don't match isn't something the game is built to read, and in game it showed up as creased skin - on the chest first, because that's where physics moves the bones even when you stand still.

Two more things made it hard to spot: Yassify still said "Points can now hold 8 bones", and the piece didn't even get the extra room - only the 4 strongest bones were kept, the same as with the option off.

It isn't rare. Of the 52 models we tested against (tops in many body sizes, bottoms, hair), 30 came out half-changed with 0.1.0. With 0.1.1: none.

What 0.1.1 changes

- Both lists now go to 8 together, whichever format the weights are in. The piece's shape and the weights it already had stay exactly as they were.
- Pieces that 0.1.0 saved half-changed get repaired. Open one and Yassify tells you; "Save into my mod" puts it right, with or without other changes. The file as it was goes to Backups first, like every save.
- Nothing else is different. With the option off, and on pieces 0.1.0 already handled correctly, you get the same file as before.

What to do

1. Update Yassify (Dalamud does it for you, or /xlplugins > Installed > update).
2. Open each piece you saved with 0.1.0. If the status line says "This piece was saved by an older Yassify that left its 8-bone change half done", press "Save into my mod".
3. No message means that piece was fine all along.

Prefer a clean start? Backups > Restore puts the piece back to how it was before Yassify, and you can add the jiggle again.

This was checked on the model files themselves (before and after on all 52, and by reading the results back with a separate tool). If a piece still creases after you re-save it, please tell us which one.

## 0.1.0

First release: YAS jiggle for the outfit you're wearing, three fits, sliders per area and per part, Neolithe shape refits, live preview, saving into the mod with backups.
