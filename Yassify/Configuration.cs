using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Yassify;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // where the jiggle is copied from: whatever body your character has on (Penumbra says which models),
    // or a body mod's models picked by hand (mod folder + paths inside it)
    public bool BodyWorn = true;
    public string BodyModDir = "";
    public List<string> BodyFiles = new();

    public bool LivePreview = true;
    public int KeepBackups = 20;

    // the last YAS settings, as the starting point for the next piece
    public float FadeStartCm = 1.5f, FadeEndCm = 8f;
    public bool Allow8 = true;
    public Dictionary<string, float> Strength = new();
    public float MasterJiggle = 1f;

    /// <summary>The how-to pops up by itself the first time.</summary>
    public bool SeenHelp;
}
