// Yassify engine (shared with Tailor) - writing into a mod, with a backup of the file first.
//
// Backups live in <root>/<mod folder>/<time>/ : the file as it was
// ("before.mdl") and a manifest.json saying where it goes back. The newest
// KeepPerFile backups of each file are kept. A change to several files at once
// (giving a mod's toggles their own names) is one backup set: before_0.bin,
// before_1.bin ... listed in the manifest's Files, restored together.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yassify.Core;

public sealed class BackupEntry
{
    public string Mod { get; set; } = "";        // mod folder name
    public string ModName { get; set; } = "";
    public string File { get; set; } = "";       // path inside the mod folder
    public DateTime Saved { get; set; }
    public string Note { get; set; } = "";
    public string ShaBefore { get; set; } = "";
    public string ShaAfter { get; set; } = "";
    public List<string> Files { get; set; } = new();       // a set: every file's path inside the mod folder
    public List<string> ShasBefore { get; set; } = new();
    public List<string> ShasAfter { get; set; } = new();
    public List<string> Created { get; set; } = new();     // 0.1.18: files of the set that weren't there before (restoring takes them out)
    public SavedYas? Settings { get; set; }               // 0.1.13: the YAS settings this save was made with
    public string Folder = "";                   // this backup's folder (not saved)
    public string BackupFile => Path.Combine(Folder, "before.mdl");
    [JsonIgnore] public bool IsSet => Files.Count > 0;
    public string SetFile(int i) => Path.Combine(Folder, $"before_{i}.bin");
}

public sealed class BackupStore
{
    public readonly string Root;
    public int KeepPerFile = 20;

    public BackupStore(string root) { Root = root; Directory.CreateDirectory(root); }

    public static string Sha(byte[] b) => Convert.ToHexString(SHA1.HashData(b));

    static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>Backs up the file (as it is on disk now) and writes the new
    /// bytes in its place. expectedSha: what the file should be now - if it
    /// changed on disk since it was opened, nothing is written.</summary>
    public BackupEntry SaveInto(string modRoot, string modDir, string modName, string relPath, byte[] newBytes, string expectedSha, string note,
                                SavedYas? settings = null)
    {
        string target = Path.Combine(modRoot, relPath);
        var current = System.IO.File.ReadAllBytes(target);
        if (expectedSha.Length > 0 && Sha(current) != expectedSha)
            throw new IOException("The file changed on disk since you opened it - open it again first.");
        var entry = Backup(modDir, modName, relPath, current, Sha(newBytes), note, settings);
        try { WriteAtomic(target, newBytes); }
        catch { DropIfUnchanged(entry, modRoot); throw; }   // e.g. a full disk: nothing changed, so no backup to list
        Prune(modDir, relPath);
        return entry;
    }

    /// <summary>Backs up several files of one mod together (one set), then writes the new bytes. expectedSha: what each
    /// file should be now - if any changed on disk since, nothing is written. If a write fails, the files already
    /// written are put back.</summary>
    public BackupEntry SaveSet(string modRoot, string modDir, string modName, IReadOnlyDictionary<string, byte[]> files,
                               IReadOnlyDictionary<string, string> expectedSha, string note)
    {
        var rels = files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var current = new List<byte[]?>();
        foreach (var rel in rels)
        {
            var path = Path.Combine(modRoot, rel);
            if (!System.IO.File.Exists(path))
            {
                // a new file (a hair's skeleton): nothing to back up, restoring takes it out again
                if (expectedSha.TryGetValue(rel, out var w0) && w0.Length > 0) throw new IOException($"{rel} isn't there any more - look again first.");
                current.Add(null);
                continue;
            }
            var cur = System.IO.File.ReadAllBytes(path);
            if (expectedSha.TryGetValue(rel, out var want) && want.Length > 0 && Sha(cur) != want)
                throw new IOException($"{rel} changed on disk since it was looked at - look again first.");
            current.Add(cur);
        }
        var entry = BackupSet(modDir, modName, rels, current, rels.Select(r => Sha(files[r])).ToList(), note);
        try { WriteAll(modRoot, rels, rels.Select(r => (byte[]?)files[r]).ToList(), current); }
        catch { DropIfUnchanged(entry, modRoot); throw; }
        return entry;
    }

    BackupEntry BackupSet(string modDir, string modName, List<string> rels, List<byte[]?> current, List<string> shaAfter, string note)
    {
        var now = DateTime.Now;
        string baseDir = Path.Combine(Root, SafeName(modDir));
        string folder = Path.Combine(baseDir, now.ToString("yyyyMMdd-HHmmss-fff"));
        int n = 1;
        while (Directory.Exists(folder)) folder = Path.Combine(baseDir, now.ToString("yyyyMMdd-HHmmss-fff") + "-" + n++);
        Directory.CreateDirectory(folder);
        var e = new BackupEntry
        {
            Mod = modDir, ModName = modName, File = "", Saved = now, Note = note, Folder = folder,
            Files = rels.ToList(), ShasBefore = current.Select(c => Sha(c ?? Array.Empty<byte>())).ToList(), ShasAfter = shaAfter.ToList(),
            Created = rels.Where((r, i) => current[i] == null).ToList(),
        };
        try
        {
            for (int i = 0; i < rels.Count; i++) System.IO.File.WriteAllBytes(e.SetFile(i), current[i] ?? Array.Empty<byte>());
            System.IO.File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { DropFolder(folder); throw; }   // e.g. a full disk: no half backup left behind (the mod isn't touched yet)
        return e;
    }

    /// <summary>Writes each file (null: takes it out - a file that wasn't there before); on a failure the ones done are put back.</summary>
    static void WriteAll(string modRoot, List<string> rels, List<byte[]?> bytes, List<byte[]?> undo)
    {
        int done = 0;
        try
        {
            for (; done < rels.Count; done++) Put(Path.Combine(modRoot, rels[done]), bytes[done]);
        }
        catch
        {
            for (int i = 0; i < done; i++) { try { Put(Path.Combine(modRoot, rels[i]), undo[i]); } catch { } }
            throw;
        }
    }

    static void Put(string target, byte[]? bytes)
    {
        if (bytes == null) { if (System.IO.File.Exists(target)) System.IO.File.Delete(target); return; }
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteAtomic(target, bytes);
    }

    /// <summary>Puts a whole set back; the files as they are now are backed up first (as a set).</summary>
    public BackupEntry RestoreSet(BackupEntry e, string modRoot)
    {
        var old = new List<byte[]?>();
        for (int i = 0; i < e.Files.Count; i++)
        {
            var b = System.IO.File.ReadAllBytes(e.SetFile(i));
            if (i >= e.ShasBefore.Count || Sha(b) != e.ShasBefore[i]) throw new IOException("This backup is damaged (its files don't match what was saved).");
            old.Add(e.Created.Contains(e.Files[i]) ? null : b);       // a file that wasn't there before goes again
        }
        var now = e.Files.Select(r => { var t = Path.Combine(modRoot, r); return System.IO.File.Exists(t) ? System.IO.File.ReadAllBytes(t) : null; }).ToList();
        var before = BackupSet(e.Mod, e.ModName, e.Files, now, e.ShasBefore, "before restoring " + e.Saved.ToString("g"));
        try { WriteAll(modRoot, e.Files, old, now); }
        catch { DropIfUnchanged(before, modRoot); throw; }
        return e;
    }

    BackupEntry Backup(string modDir, string modName, string relPath, byte[] current, string shaAfter, string note, SavedYas? settings = null)
    {
        var now = DateTime.Now;
        string baseDir = Path.Combine(Root, SafeName(modDir));
        string folder = Path.Combine(baseDir, now.ToString("yyyyMMdd-HHmmss-fff"));
        int n = 1;
        while (Directory.Exists(folder)) folder = Path.Combine(baseDir, now.ToString("yyyyMMdd-HHmmss-fff") + "-" + n++);
        Directory.CreateDirectory(folder);
        var e = new BackupEntry
        {
            Mod = modDir, ModName = modName, File = relPath, Saved = now, Note = note,
            ShaBefore = Sha(current), ShaAfter = shaAfter, Folder = folder, Settings = settings,
        };
        try
        {
            System.IO.File.WriteAllBytes(e.BackupFile, current);
            System.IO.File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { DropFolder(folder); throw; }   // e.g. a full disk: no half backup left behind (the mod isn't touched yet)
        return e;
    }

    public static void WriteAtomic(string target, byte[] bytes)
    {
        string tmp = target + ".yassify.tmp";
        try { System.IO.File.WriteAllBytes(tmp, bytes); }
        catch { try { System.IO.File.Delete(tmp); } catch { } throw; }   // e.g. a full disk: the mod file is untouched, no half file left
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (System.IO.File.Exists(target)) System.IO.File.Replace(tmp, target, null);
                else System.IO.File.Move(tmp, target);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                System.Threading.Thread.Sleep(100 * (attempt + 1));   // the game may be reading it
            }
            catch
            {
                // gave up: the mod file is still the old one, so the temp copy can go (only then - never lose the only copy)
                if (System.IO.File.Exists(target)) { try { System.IO.File.Delete(tmp); } catch { } }
                throw;
            }
        }
    }

    /// <summary>After a write that failed: if the mod's files are exactly as they were, the backup just made for that
    /// write isn't needed (nothing changed). If they can't be read or differ, it stays - it may be the only good copy.</summary>
    static void DropIfUnchanged(BackupEntry e, string modRoot)
    {
        try
        {
            bool same = e.IsSet
                ? e.Files.Select((r, i) => e.Created.Contains(r) ? !System.IO.File.Exists(Path.Combine(modRoot, r))
                                          : i < e.ShasBefore.Count && Sha(System.IO.File.ReadAllBytes(Path.Combine(modRoot, r))) == e.ShasBefore[i]).All(x => x)
                : Sha(System.IO.File.ReadAllBytes(Path.Combine(modRoot, e.File))) == e.ShaBefore;
            if (same) DropFolder(e.Folder);
        }
        catch { }
    }

    static void DropFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    public List<BackupEntry> List(string? modDir = null, string? relPath = null)
    {
        var res = new List<BackupEntry>();
        if (!Directory.Exists(Root)) return res;
        foreach (var mf in Directory.EnumerateFiles(Root, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var e = JsonSerializer.Deserialize<BackupEntry>(System.IO.File.ReadAllText(mf));
                if (e == null) continue;
                e.Folder = Path.GetDirectoryName(mf)!;
                if (e.IsSet ? Enumerable.Range(0, e.Files.Count).Any(i => !System.IO.File.Exists(e.SetFile(i))) : !System.IO.File.Exists(e.BackupFile)) continue;
                if (modDir != null && !string.Equals(e.Mod, modDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (relPath != null && !(e.IsSet ? e.Files.Any(f => string.Equals(Norm(f), Norm(relPath), StringComparison.OrdinalIgnoreCase))
                                                 : string.Equals(Norm(e.File), Norm(relPath), StringComparison.OrdinalIgnoreCase))) continue;
                res.Add(e);
            }
            catch { }
        }
        return res.OrderByDescending(e => e.Saved).ToList();
    }

    static string Norm(string p) => p.Replace('\\', '/');

    /// <summary>Puts a backup back. The file as it is now is backed up first
    /// (so a restore can be undone too).</summary>
    public BackupEntry Restore(BackupEntry e, string modRoot)
    {
        if (e.IsSet) return RestoreSet(e, modRoot);
        string target = Path.Combine(modRoot, e.File);
        var old = System.IO.File.ReadAllBytes(e.BackupFile);
        if (Sha(old) != e.ShaBefore) throw new IOException("This backup is damaged (its file doesn't match what was saved).");
        BackupEntry? before = null;
        if (System.IO.File.Exists(target))
            before = Backup(e.Mod, e.ModName, e.File, System.IO.File.ReadAllBytes(target), e.ShaBefore, "before restoring " + e.Saved.ToString("g"));
        try { WriteAtomic(target, old); }
        catch { if (before != null) DropIfUnchanged(before, modRoot); throw; }
        Prune(e.Mod, e.File);
        return e;
    }

    void Prune(string modDir, string relPath)
    {
        foreach (var e in List(modDir, relPath).Where(x => !x.IsSet).Skip(Math.Max(1, KeepPerFile)))
        {
            try { Directory.Delete(e.Folder, true); } catch { }
        }
    }
}
