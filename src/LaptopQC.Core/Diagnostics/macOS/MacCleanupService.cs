using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LaptopQC.Core.Diagnostics.macOS;

/// <summary>
/// macOS equivalent of Windows CleanupService.
/// Scans and removes common junk file categories using only built-in
/// macOS tools and standard filesystem paths — no root access required.
/// </summary>
public class MacCleanupService
{
    public record CleanupCategory(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<string> Paths,
        bool IsApproximate = false);

    public record ScanResult(
        string Id,
        string Name,
        string Description,
        long SizeBytes,
        int FileCount,
        bool IsApproximate,
        bool IsAvailable,
        IReadOnlyList<string> Paths);

    public record CleanResult(
        int DeletedFiles,
        long FreedBytes,
        int LockedFiles,
        int Errors);

    // ── Category definitions ──────────────────────────────────────────────────

    public static IReadOnlyList<CleanupCategory> GetCategories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new List<CleanupCategory>
        {
            new("user_caches",    "User Caches",
                "App-specific caches stored in ~/Library/Caches",
                new[] { Path.Combine(home, "Library", "Caches") }),

            new("user_logs",      "User Logs",
                "App log files stored in ~/Library/Logs",
                new[] { Path.Combine(home, "Library", "Logs") }),

            new("trash",          "Trash",
                "Files in your Trash (~/.Trash)",
                new[] { Path.Combine(home, ".Trash") }),

            new("tmp",            "Temporary Files",
                "System and app temporary files in /tmp and $TMPDIR",
                new[]
                {
                    Path.GetTempPath(),
                    "/private/tmp",
                    Path.Combine(home, "Library", "Application Support", "CrashReporter")
                }),

            new("quicklook",      "QuickLook Thumbnails",
                "QuickLook thumbnail cache (safe to delete — macOS rebuilds on demand)",
                new[]
                {
                    Path.Combine(home, "Library", "Caches", "com.apple.QuickLook.thumbnailcache"),
                    "/private/var/folders"   // glob resolved below
                },
                IsApproximate: true),

            new("downloads_ds",   "Download Metadata (.DS_Store)",
                ".DS_Store and .localized metadata files in ~/Downloads",
                new[] { Path.Combine(home, "Downloads") }),

            new("ios_simulator",  "iOS Simulator Data",
                "Xcode iOS Simulator runtime data (safe on refurbished dev Macs)",
                new[]
                {
                    Path.Combine(home, "Library", "Developer", "CoreSimulator", "Devices"),
                    Path.Combine(home, "Library", "Developer", "Xcode", "DerivedData")
                }),

            new("npm_cache",      "npm / Yarn Cache",
                "Node.js package manager caches",
                new[]
                {
                    Path.Combine(home, ".npm", "_cacache"),
                    Path.Combine(home, ".yarn", "cache"),
                }),

            new("system_logs",    "System Logs",
                "System-wide logs in /private/var/log (read-only — shows size only)",
                new[] { "/private/var/log" },
                IsApproximate: true),
        };
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScanResult>> ScanAsync(
        IProgress<string>? progress = null,
        IEnumerable<CleanupCategory>? categories = null)
    {
        var cats = categories ?? GetCategories();
        var results = new List<ScanResult>();

        foreach (var cat in cats)
        {
            progress?.Report($"Scanning {cat.Name}...");
            var (size, count) = await Task.Run(() => MeasurePaths(cat.Paths, cat.Id));
            results.Add(new ScanResult(
                cat.Id, cat.Name, cat.Description,
                size, count, cat.IsApproximate,
                IsAvailable: size > 0,
                cat.Paths));
        }

        return results;
    }

    // ── Clean ─────────────────────────────────────────────────────────────────

    public async Task<CleanResult> CleanAsync(
        IEnumerable<ScanResult> selected,
        IProgress<string>? progress = null)
    {
        int deleted = 0, locked = 0, errors = 0;
        long freed = 0;

        foreach (var cat in selected)
        {
            progress?.Report($"Cleaning {cat.Name}...");
            var result = await Task.Run(() => CleanCategory(cat));
            deleted += result.DeletedFiles;
            freed   += result.FreedBytes;
            locked  += result.LockedFiles;
            errors  += result.Errors;
        }

        return new CleanResult(deleted, freed, locked, errors);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static (long SizeBytes, int FileCount) MeasurePaths(
        IReadOnlyList<string> paths, string categoryId)
    {
        long total = 0;
        int count  = 0;

        foreach (var root in paths)
        {
            if (!Directory.Exists(root) && !File.Exists(root))
                continue;

            try
            {
                // For system_logs and iOS simulator, just estimate with du to avoid long waits
                if (categoryId == "system_logs" || categoryId == "ios_simulator")
                {
                    var du = RunDu(root);
                    total += du;
                    count += du > 0 ? 1 : 0;
                    continue;
                }

                // For quicklook, only measure the user-accessible path
                if (categoryId == "quicklook" && root.StartsWith("/private/var"))
                    continue;

                // .DS_Store / .localized only — enumerate selectively
                if (categoryId == "downloads_ds")
                {
                    foreach (var f in SafeEnumerateFiles(root, "*.DS_Store")
                                       .Concat(SafeEnumerateFiles(root, ".localized")))
                    {
                        try { var fi = new FileInfo(f); total += fi.Length; count++; } catch { }
                    }
                    continue;
                }

                // General: walk all children
                foreach (var f in SafeEnumerateFiles(root, "*"))
                {
                    try { var fi = new FileInfo(f); total += fi.Length; count++; } catch { }
                }
            }
            catch { /* read-only or permission denied */ }
        }

        return (total, count);
    }

    private static CleanResult CleanCategory(ScanResult cat)
    {
        int deleted = 0, locked = 0, errors = 0;
        long freed = 0;

        // System logs are read-only — skip actual deletion, report 0.
        if (cat.Id == "system_logs")
            return new CleanResult(0, 0, 0, 0);

        foreach (var root in cat.Paths)
        {
            if (!Directory.Exists(root) && !File.Exists(root))
                continue;

            // QuickLook: run system qlmanage -r cache instead of raw delete
            if (cat.Id == "quicklook")
            {
                try
                {
                    Process.Start(new ProcessStartInfo("qlmanage", "-r cache")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    })?.WaitForExit(5000);
                    deleted++;
                }
                catch { errors++; }
                continue;
            }

            // Trash: use osascript to empty trash properly (handles locked items)
            if (cat.Id == "trash")
            {
                try
                {
                    var before = Directory.Exists(root)
                        ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
                              .Sum(f => { try { return f.Length; } catch { return 0L; } })
                        : 0L;

                    Process.Start(new ProcessStartInfo("osascript", "-e 'tell application \"Finder\" to empty trash'")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    })?.WaitForExit(30000);

                    freed  += before;
                    deleted++;
                }
                catch { errors++; }
                continue;
            }

            // .DS_Store / metadata files only
            if (cat.Id == "downloads_ds")
            {
                foreach (var f in SafeEnumerateFiles(root, "*.DS_Store")
                                   .Concat(SafeEnumerateFiles(root, ".localized")))
                {
                    try
                    {
                        var len = new FileInfo(f).Length;
                        File.Delete(f);
                        freed += len; deleted++;
                    }
                    catch (IOException) { locked++; }
                    catch { errors++; }
                }
                continue;
            }

            // iOS Simulator / DerivedData: delete whole subdirectory trees
            if (cat.Id == "ios_simulator")
            {
                foreach (var sub in SafeEnumerateDirectories(root))
                {
                    try
                    {
                        var size = new DirectoryInfo(sub)
                            .EnumerateFiles("*", SearchOption.AllDirectories)
                            .Sum(f => { try { return f.Length; } catch { return 0L; } });
                        Directory.Delete(sub, recursive: true);
                        freed += size; deleted++;
                    }
                    catch (IOException) { locked++; }
                    catch { errors++; }
                }
                continue;
            }

            // General: delete all files, then prune empty sub-dirs
            foreach (var f in SafeEnumerateFiles(root, "*"))
            {
                try
                {
                    var len = new FileInfo(f).Length;
                    File.Delete(f);
                    freed += len; deleted++;
                }
                catch (IOException) { locked++; }
                catch { errors++; }
            }

            // Remove now-empty sub-directories (leave root itself)
            foreach (var dir in SafeEnumerateDirectories(root).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* ignore */ }
            }
        }

        return new CleanResult(deleted, freed, locked, errors);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                : Enumerable.Empty<string>();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static long RunDu(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("du", $"-sk \"{path}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(5000);
            // du -sk outputs "<KB>\t<path>"
            if (long.TryParse(output.Split('\t')[0].Trim(), out long kb))
                return kb * 1024;
        }
        catch { }
        return 0;
    }
}
