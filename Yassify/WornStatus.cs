// Yassify - per worn piece, worked out in the background: its YAS areas and whether the file
// is one Yassify saved. Files are read again only when their size or time changes.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yassify.Core;

namespace Yassify;

public sealed class WornStatus
{
    public sealed record Info(YasInfo? Yas, BackupEntry? Saved, string Error, bool BreastOff = false);

    readonly ConcurrentDictionary<string, (long size, DateTime time, YasInfo yas, string sha)> _scan = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Info> _info = new(StringComparer.OrdinalIgnoreCase);
    int _gen;

    public Info? Get(WornModel w) => _info.TryGetValue(w.FullPath, out var i) ? i : null;

    /// <param name="eqp">the game's equipment table (for tops: is breast physics off?); null = don't check</param>
    public void Refresh(IReadOnlyList<WornModel> worn, BackupStore backups, byte[]? eqp = null, string modDirectory = "")
    {
        int gen = Interlocked.Increment(ref _gen);
        var list = worn.ToList();
        Task.Run(() =>
        {
            foreach (var w in list)
            {
                if (gen != Volatile.Read(ref _gen)) return;          // a newer refresh took over
                try
                {
                    var fi = new FileInfo(w.FullPath);
                    if (!fi.Exists) { _info[w.FullPath] = new Info(null, null, "the file isn't there"); continue; }
                    if (!_scan.TryGetValue(w.FullPath, out var c) || c.size != fi.Length || c.time != fi.LastWriteTimeUtc)
                    {
                        var bytes = File.ReadAllBytes(w.FullPath);
                        c = (fi.Length, fi.LastWriteTimeUtc, Yasified.Scan(MdlFile.Read(bytes)), BackupStore.Sha(bytes));
                        _scan[w.FullPath] = c;
                    }
                    bool off = false;
                    if (eqp != null && modDirectory.Length > 0 && Eqp.TopSetId(w.GamePaths) >= 0)
                        try { off = BreastPhysics.Read(eqp, Path.Combine(modDirectory, w.ModDir), w.RelPath, w.GamePaths)?.Off == true; } catch { }
                    _info[w.FullPath] = new Info(c.yas, Yasified.SavedBy(backups, w.ModDir, w.RelPath, c.sha), "", off);
                }
                catch (Exception e) { _info[w.FullPath] = new Info(null, null, e.Message); }
            }
        });
    }
}
