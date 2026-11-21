<p align="center"><img src="images/icon.png" width="128" alt="Yassify"></p>

<h1 align="center">Yassify ♡</h1>

<p align="center"><b>Make your outfits jiggle like your YAS body does - and refit Neolithe pieces to your size.</b><br>
A Dalamud plugin for FFXIV, made to share with friends.</p>

---

## What it does

- **Adds YAS jiggle to the outfit you're wearing.** Pick a piece and Yassify copies the jiggle from the YAS body you have on, so the cloth bounces with your skin instead of floating over it.
- **Sliders for every area:** butt, chest, belly, thighs, upper arms, plus fingertips, toes and more. 100% moves like your skin, 0% doesn't jiggle at all.
- **Three fits:** *As made* (most things), *Hugs the skin* (leggings, shorts, bodysuits, straps - nothing pokes through when you bounce) and *Flowy* (skirts, dresses, baggy pants - they drape instead of hugging every curve).
- **Per-part control:** belts, buckles and jewellery can be set to 0% jiggle; a dress can have a tight top and a flowy skirt.
- **Neolithe refits:** a top or bottoms made for one Neolithe shape can be moved to another - XS to L, Almond, Hazelnut, Macadamia, Pushup, Neobelly, Buff... It works out which shape the piece was made for by itself.
- **Live on your character.** Every change shows in game a moment later. Nothing is written until you press **Save into my mod**.
- **Backups.** The old file is kept every time you save, and the Backups tab puts it back.

## Install

1. In game, open Dalamud's settings (`/xlsettings`) and go to **Experimental**.
2. Under **Custom Plugin Repositories**, paste this and click **+**, then **Save**:
   ```
   https://raw.githubusercontent.com/huskandbone/Yassify/main/repo.json
   ```
3. Open the plugin installer (`/xlplugins`), search for **Yassify**, and install it.

You'll also need [Penumbra](https://github.com/xivdev/Penumbra), a YAS body (for example the Neolithe YAS AIO) and the YAS skeleton.

## How to yassify something

1. In Penumbra, turn on your YAS body, the YAS skeleton and the outfit. Wear the outfit.
2. `/yassify` - the line under the title should say where the jiggle comes from. If not, open **My body** and click **Use the body I'm wearing**.
3. Click a piece on the left (tops, bottoms, gloves and shoes first).
4. **Jiggle** tab: pick **Add YAS jiggle**, choose how it fits, and play with the sliders.
5. Run, stop, dance - watch it from the side and back. **Peek at the original** flips back to compare.
6. Happy? **Save into my mod**. Then the next piece.

`/yassify help` opens the how-to in game.

| Fit | For | What it does |
| --- | --- | --- |
| As made | most things | keeps the mod's own weights and adds your body's jiggle on top |
| Hugs the skin | leggings, shorts, bodysuits, straps | near the skin, the cloth copies the skin under it exactly |
| Flowy | skirts, dresses, baggy pants | each spot takes the jiggle of the skin around it, so it drapes |

**Refit:** open a top or bottoms, go to **Refit**, check **Made for**, and pick **Refit to** (or click **My shape**). Set the refit and the jiggle, then save once.

## Good to know

- Yassify never touches your body or physics files. How bouncy things are comes from your YAS skeleton / physics mod - Yassify decides how much of it each piece follows.
- Give a top and its bottoms the same **Belly** value so they meet nicely at the waist.
- The **Chest** slider also changes how a top follows your bust size. For a stiff top that keeps its size, use **Keep my chest still in this top** (it writes the game's own switch into that top's mod, with a backup).
- Backups live in Yassify's settings folder, under `Backups`.

## Credits

Made by **Husk & Bone**. Yassify's engine - the weights, the fits, the refit and the backups - is the same one as in Tailor, Husk & Bone's bigger gear editor; Yassify is the friendly little slice of it.

## Building

You need the .NET 10 SDK and Dalamud's dev files. On Windows with XIVLauncher installed, `dotnet build -c Release` in `Yassify/` just works. Anywhere else, unzip [Dalamud's latest.zip](https://goatcorp.github.io/dalamud-distrib/latest.zip) and point `DALAMUD_HOME` at it first. The package lands in `Yassify/bin/Release/Yassify/latest.zip`.

To release: raise `<Version>` in `Yassify/Yassify.csproj`, commit, and push a tag like `v0.1.1`. GitHub Actions builds it, attaches `latest.zip` to a release, and updates `repo.json` so everyone gets the update.
