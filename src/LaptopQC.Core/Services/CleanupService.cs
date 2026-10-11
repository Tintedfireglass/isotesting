#if WINDOWS
using System.Security.Principal;
using System.Threading;
using System.Text.Json;

namespace LaptopQC.Core.Services;

public class CleanupService
{
    public CleanupScanResult Scan(IProgress<CleanupProgress>? progress = null)
    {
        var categories = new List<CleanupCategory>();
        var definitions = GetDefinitions();
        int totalDefs = definitions.Count;
        int defIndex = 0;

        foreach (var def in definitions)
        {
            defIndex++;
            progress?.Report(new CleanupProgress
            {
                Phase = "scan",
                Category = def.Name,
                Message = $"Scanning {def.Name} ({defIndex}/{totalDefs})"
            });

            var category = new CleanupCategory
            {
                Id = def.Id,
                Name = def.Name,
                Description = def.Description,
                RequiresAdmin = def.RequiresAdmin,
                IsRecycleBin = def.IsRecycleBin
            };

            if (def.IsRecycleBin)
            {
                var (count, size) = GetRecycleBinStats();
                category.FileCount = count;
                category.SizeBytes = size;
                category.IsAvailable = count > 0 || size > 0;
            }
            else
            {
                foreach (var target in def.GetTargets())
                {
                    if (string.IsNullOrWhiteSpace(target.Path))
                        continue;

                    if (!Directory.Exists(target.Path))
                        continue;

                    category.Targets.Add(target);
                }

                if (category.Targets.Count == 0)
                {
                    category.IsAvailable = false;
                    category.FileCount = 0;
                    category.SizeBytes = 0;
                }
                else
                {
                    try
                    {
                        foreach (var target in category.Targets)
                        {
                            progress?.Report(new CleanupProgress
                            {
                                Phase = "scan",
                                Category = def.Name,
                                Target = target.Path,
                                Message = $"Scanning {def.Name}: {target.Path}"
                            });
                            var (count, size, approx) = GetTargetStats(target);
                            category.FileCount += count;
                            category.SizeBytes += size;
                            if (approx)
                                category.IsApproximate = true;
                        }
                        category.IsAvailable = category.FileCount > 0 || category.SizeBytes > 0;
                    }
                    catch
                    {
                        category.IsAvailable = false;
                    }
                }
            }

            categories.Add(category);
        }

        return new CleanupScanResult
        {
            Categories = categories
        };
    }

    public CleanupResult Clean(IEnumerable<CleanupCategory> categories, IProgress<CleanupProgress>? progress = null)
    {
        var result = new CleanupResult
        {
            StartedAt = DateTime.UtcNow
        };

        var isAdmin = IsRunningAsAdmin();

        foreach (var category in categories)
        {
            progress?.Report(new CleanupProgress
            {
                Phase = "clean",
                Category = category.Name,
                Message = $"Cleaning {category.Name}..."
            });

            if (!category.IsAvailable)
            {
                result.SkippedCategories.Add(category.Name);
                continue;
            }

            if (category.RequiresAdmin && !isAdmin)
            {
                result.Errors.Add($"{category.Name}: Requires administrator privileges");
                continue;
            }

            if (category.IsRecycleBin)
            {
                try
                {
                    progress?.Report(new CleanupProgress
                    {
                        Phase = "clean",
                        Category = category.Name,
                        Message = "Emptying Recycle Bin..."
                    });
                    var (count, size) = GetRecycleBinStats();
                    if (count > 0 || size > 0)
                    {
                        EmptyRecycleBin();
                        result.DeletedFiles += count;
                        result.FreedBytes += size;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{category.Name}: {ex.Message}");
                }

                continue;
            }

            foreach (var target in category.Targets)
            {
                progress?.Report(new CleanupProgress
                {
                    Phase = "clean",
                    Category = category.Name,
                    Target = target.Path,
                    Message = $"Cleaning {category.Name}: {target.Path}"
                });
                var deleted = DeleteTargetContents(
                    target,
                    out var freedBytes,
                    out var errors,
                    progress,
                    category.Name,
                    out var lockedCount,
                    out var deniedCount,
                    out var lockedFiles);
                result.DeletedFiles += deleted;
                result.FreedBytes += freedBytes;
                result.LockedFiles += lockedCount;
                result.AccessDeniedFiles += deniedCount;

                if (lockedFiles.Count > 0)
                {
                    var retryDeleted = RetryLockedFiles(lockedFiles, out var retryFreed, out var retryRemaining);
                    if (retryDeleted > 0)
                    {
                        result.DeletedFiles += retryDeleted;
                        result.FreedBytes += retryFreed;
                        result.LockedFiles = Math.Max(0, result.LockedFiles - retryDeleted);
                    }

                    if (retryRemaining.Count > 0)
                    {
                        var scheduled = ScheduleDeleteOnReboot(retryRemaining);
                        result.ScheduledOnReboot += scheduled;
                        if (scheduled > 0)
                            result.LockedFiles = Math.Max(0, result.LockedFiles - scheduled);
                    }
                }
                foreach (var err in errors)
                {
                    result.Errors.Add($"{category.Name}: {err}");
                }
            }
        }

        result.CompletedAt = DateTime.UtcNow;
        WriteLog(result, categories);

        return result;
    }

    public List<CleanupCategory> RescanCategories(IEnumerable<CleanupCategory> categories, IProgress<CleanupProgress>? progress = null)
    {
        var updated = new List<CleanupCategory>();

        foreach (var category in categories)
        {
            progress?.Report(new CleanupProgress
            {
                Phase = "scan",
                Category = category.Name,
                Message = $"Rescanning {category.Name}..."
            });

            if (category.IsRecycleBin)
            {
                var (count, size) = GetRecycleBinStats();
                updated.Add(new CleanupCategory
                {
                    Id = category.Id,
                    Name = category.Name,
                    Description = category.Description,
                    RequiresAdmin = category.RequiresAdmin,
                    IsRecycleBin = true,
                    FileCount = count,
                    SizeBytes = size,
                    IsAvailable = count > 0 || size > 0,
                    IsApproximate = false
                });
                continue;
            }

            var refreshed = new CleanupCategory
            {
                Id = category.Id,
                Name = category.Name,
                Description = category.Description,
                RequiresAdmin = category.RequiresAdmin,
                IsRecycleBin = category.IsRecycleBin,
                Targets = category.Targets
            };

            if (refreshed.Targets.Count == 0)
            {
                refreshed.IsAvailable = false;
                refreshed.FileCount = 0;
                refreshed.SizeBytes = 0;
            }
            else
            {
                foreach (var target in refreshed.Targets)
                {
                    progress?.Report(new CleanupProgress
                    {
                        Phase = "scan",
                        Category = refreshed.Name,
                        Target = target.Path,
                        Message = $"Rescanning {refreshed.Name}: {target.Path}"
                    });

                    var (count, size, approx) = GetTargetStats(target);
                    refreshed.FileCount += count;
                    refreshed.SizeBytes += size;
                    if (approx)
                        refreshed.IsApproximate = true;
                }

                refreshed.IsAvailable = refreshed.FileCount > 0 || refreshed.SizeBytes > 0;
            }

            updated.Add(refreshed);
        }

        return updated;
    }

    public CleanupCategory DeepScanWindowsUpdate(IProgress<CleanupProgress>? progress = null)
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var targetPath = Path.Combine(windowsDir, "SoftwareDistribution", "Download");

        var category = new CleanupCategory
        {
            Id = "update_cache",
            Name = "Windows Update Cache",
            Description = "Downloaded Windows update files",
            RequiresAdmin = true
        };

        if (!Directory.Exists(targetPath))
        {
            category.IsAvailable = false;
            category.FileCount = 0;
            category.SizeBytes = 0;
            return category;
        }

        var target = CleanupTarget.Directory(targetPath);
        category.Targets.Add(target);

        progress?.Report(new CleanupProgress
        {
            Phase = "scan",
            Category = category.Name,
            Target = targetPath,
            Message = $"Deep scanning {category.Name}..."
        });

        var (count, size, _) = GetTargetStats(target);
        category.FileCount = count;
        category.SizeBytes = size;
        category.IsApproximate = false;
        category.IsAvailable = count > 0 || size > 0;

        return category;
    }

    private const int BrowserCacheMaxFiles = 200000;
    private const long BrowserCacheMaxBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    private const long LargeCacheMaxBytes = 3L * 1024 * 1024 * 1024; // 3 GB
    private const int BrowserCacheScanDepth = 3;

    private static List<CleanupDefinition> GetDefinitions()
    {
        var defs = new List<CleanupDefinition>();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        defs.Add(new CleanupDefinition
        {
            Id = "user_temp",
            Name = "User Temp Files",
            Description = "Temporary files for the current user",
            RequiresAdmin = false,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.GetTempPath())
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "windows_temp",
            Name = "Windows Temp Files",
            Description = "System temp files (admin recommended)",
            RequiresAdmin = true,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(windowsDir, "Temp"))
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "crash_dumps",
            Name = "App Crash Dumps",
            Description = "Application crash dump files",
            RequiresAdmin = false,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(localAppData, "CrashDumps"))
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "wer_reports",
            Name = "Windows Error Reports",
            Description = "Windows Error Reporting archives",
            RequiresAdmin = true,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive")),
                CleanupTarget.Directory(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"))
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "update_cache",
            Name = "Windows Update Cache",
            Description = "Downloaded Windows update files",
            RequiresAdmin = true,
            GetTargets = () => new[]
            {
                // Fast scan by default (approx). Use Deep Scan for exact size.
                CleanupTarget.Directory(Path.Combine(windowsDir, "SoftwareDistribution", "Download"))
                    .WithLimits(maxBytes: LargeCacheMaxBytes)
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "delivery_opt",
            Name = "Delivery Optimization Cache",
            Description = "Windows update delivery cache",
            RequiresAdmin = true,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(programData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"))
                    .WithLimits(maxBytes: LargeCacheMaxBytes)
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "directx_cache",
            Name = "DirectX Shader Cache",
            Description = "GPU shader cache",
            RequiresAdmin = false,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(localAppData, "D3DSCache"))
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "thumbnail_cache",
            Name = "Thumbnail Cache",
            Description = "Explorer thumbnail and icon caches",
            RequiresAdmin = false,
            GetTargets = () => new[]
            {
                CleanupTarget.FilePattern(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "thumbcache*.db"),
                CleanupTarget.FilePattern(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "iconcache*.db")
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "legacy_inet_cache",
            Name = "Legacy Internet Cache",
            Description = "Legacy WinINET cache",
            RequiresAdmin = false,
            GetTargets = () => new[]
            {
                CleanupTarget.Directory(Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"))
            }
        });

        defs.Add(new CleanupDefinition
        {
            Id = "browser_cache",
            Name = "Browser Cache",
            Description = "Chrome, Edge, and Firefox cache",
            RequiresAdmin = false,
            GetTargets = () => BuildBrowserCacheTargets(localAppData, appData)
        });

        defs.Add(new CleanupDefinition
        {
            Id = "recycle_bin",
            Name = "Recycle Bin",
            Description = "Empty the Recycle Bin",
            RequiresAdmin = false,
            IsRecycleBin = true,
            GetTargets = () => Array.Empty<CleanupTarget>()
        });

        return defs;
    }

    private static IEnumerable<CleanupTarget> BuildBrowserCacheTargets(string localAppData, string appData)
    {
        var targets = new List<CleanupTarget>();

        // Chrome
        var chromeRoot = Path.Combine(localAppData, "Google", "Chrome", "User Data");
        targets.AddRange(BuildChromiumProfileTargets(chromeRoot));

        // Edge
        var edgeRoot = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        targets.AddRange(BuildChromiumProfileTargets(edgeRoot));

        // Firefox
        var firefoxProfiles = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            foreach (var profile in Directory.GetDirectories(firefoxProfiles))
            {
                targets.Add(
                    CleanupTarget.Directory(Path.Combine(profile, "cache2"))
                        .WithLimits(maxFiles: BrowserCacheMaxFiles, maxBytes: BrowserCacheMaxBytes));
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTarget> BuildChromiumProfileTargets(string root)
    {
        var targets = new List<CleanupTarget>();
        if (!Directory.Exists(root))
            return targets;

        foreach (var profileDir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(profileDir);
            if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                continue;

            targets.Add(
                CleanupTarget.Directory(Path.Combine(profileDir, "Cache"))
                    .WithLimits(maxFiles: BrowserCacheMaxFiles, maxBytes: BrowserCacheMaxBytes)
                    .WithScanDepth(BrowserCacheScanDepth));
            targets.Add(
                CleanupTarget.Directory(Path.Combine(profileDir, "Code Cache"))
                    .WithLimits(maxFiles: BrowserCacheMaxFiles, maxBytes: BrowserCacheMaxBytes)
                    .WithScanDepth(BrowserCacheScanDepth));
            targets.Add(
                CleanupTarget.Directory(Path.Combine(profileDir, "GPUCache"))
                    .WithLimits(maxFiles: BrowserCacheMaxFiles, maxBytes: BrowserCacheMaxBytes)
                    .WithScanDepth(BrowserCacheScanDepth));
            // Skip CacheStorage for scanning due to massive size; cleaning still covers Cache/Code/GPU caches safely.
        }

        return targets;
    }

    private static (int Count, long Size, bool Approximate) GetTargetStats(CleanupTarget target)
    {
        if (!Directory.Exists(target.Path))
            return (0, 0, false);

        IEnumerable<string> files = EnumerateFiles(target);

        int count = 0;
        long size = 0;
        bool approx = false;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                size += info.Length;
                count++;

                if (target.MaxFiles.HasValue && count >= target.MaxFiles.Value)
                {
                    approx = true;
                    break;
                }

                if (target.MaxBytes.HasValue && size >= target.MaxBytes.Value)
                {
                    approx = true;
                    break;
                }
            }
            catch
            {
                // ignore
            }
        }

        return (count, size, approx);
    }

    private static IEnumerable<string> EnumerateFiles(CleanupTarget target)
    {
        if (target.ScanMaxDepth.HasValue && target.ScanMaxDepth.Value >= 0)
            return EnumerateFilesWithMaxDepth(target);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = target.Recurse,
            IgnoreInaccessible = true
        };

        return target.IsPattern
            ? Directory.EnumerateFiles(target.Path, target.Pattern ?? "*", options)
            : Directory.EnumerateFiles(target.Path, "*", options);
    }

    private static IEnumerable<string> EnumerateFilesWithMaxDepth(CleanupTarget target)
    {
        var maxDepth = target.ScanMaxDepth ?? 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((target.Path, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, target.Pattern ?? "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true
                });
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (!target.Recurse || depth >= maxDepth)
                continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    stack.Push((sub, depth + 1));
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static int DeleteTargetContents(
        CleanupTarget target,
        out long freedBytes,
        out List<string> errors,
        IProgress<CleanupProgress>? progress,
        string categoryName,
        out int lockedCount,
        out int deniedCount,
        out List<string> lockedFiles)
    {
        freedBytes = 0;
        errors = new List<string>();
        int deleted = 0;
        lockedCount = 0;
        deniedCount = 0;
        lockedFiles = new List<string>();

        if (!Directory.Exists(target.Path))
            return 0;

        IEnumerable<string> files = target.IsPattern
            ? Directory.EnumerateFiles(target.Path, target.Pattern ?? "*", new EnumerationOptions
            {
                RecurseSubdirectories = target.Recurse,
                IgnoreInaccessible = true
            })
            : Directory.EnumerateFiles(target.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = target.Recurse,
                IgnoreInaccessible = true
            });

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                var length = info.Length;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    info.Attributes = FileAttributes.Normal;
                info.Delete();
                deleted++;
                freedBytes += length;

                if (deleted % 1000 == 0)
                {
                    progress?.Report(new CleanupProgress
                    {
                        Phase = "clean",
                        Category = categoryName,
                        Target = target.Path,
                        Message = $"Cleaning {categoryName}: {deleted} files removed..."
                    });
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete file: {file} ({ex.Message})");
                if (IsLockedException(ex))
                {
                    lockedCount++;
                    lockedFiles.Add(file);
                }
                else if (IsAccessDenied(ex))
                {
                    deniedCount++;
                }
            }
        }

        // Clean empty directories when we are deleting whole folders
        if (!target.IsPattern)
        {
            var dirOptions = new EnumerationOptions
            {
                RecurseSubdirectories = target.Recurse,
                IgnoreInaccessible = true
            };

            foreach (var dir in Directory.EnumerateDirectories(target.Path, "*", dirOptions)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, false);
                }
                catch
                {
                    // ignore
                }
            }
        }

        return deleted;
    }

    private static int RetryLockedFiles(List<string> lockedFiles, out long freedBytes, out List<string> remaining)
    {
        freedBytes = 0;
        int deleted = 0;
        remaining = new List<string>();

        var distinct = lockedFiles.Distinct().ToList();
        var firstPassRemaining = new List<string>();

        foreach (var file in distinct)
        {
            if (TryDeleteFile(file, out var freed))
            {
                deleted++;
                freedBytes += freed;
            }
            else
            {
                firstPassRemaining.Add(file);
            }
        }

        if (firstPassRemaining.Count == 0)
            return deleted;

        // Short backoff before a second retry pass.
        Thread.Sleep(200);

        foreach (var file in firstPassRemaining)
        {
            if (TryDeleteFile(file, out var freed))
            {
                deleted++;
                freedBytes += freed;
            }
            else
            {
                remaining.Add(file);
            }
        }

        return deleted;
    }

    private static int ScheduleDeleteOnReboot(List<string> files)
    {
        int scheduled = 0;
        foreach (var file in files.Distinct())
        {
            try
            {
                if (!File.Exists(file))
                    continue;
                if (NativeMethods.MoveFileEx(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                    scheduled++;
            }
            catch
            {
                // ignore
            }
        }
        return scheduled;
    }

    private static (int Count, long Size) GetRecycleBinStats()
    {
        // Prefer Windows shell API for accurate totals.
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
            };

            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            if (hr == 0)
            {
                return ((int)Math.Min(int.MaxValue, info.i64NumItems), info.i64Size);
            }
        }
        catch
        {
            // fall back to manual enumeration
        }

        int count = 0;
        long size = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (drive.DriveType != DriveType.Fixed) continue;

            var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(recyclePath)) continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            foreach (var file in Directory.EnumerateFiles(recyclePath, "*", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    size += info.Length;
                    count++;
                }
                catch
                {
                    // ignore
                }
            }
        }

        return (count, size);
    }

    private static void EmptyRecycleBin()
    {
        NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOSOUND);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteLog(CleanupResult result, IEnumerable<CleanupCategory> categories)
    {
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pramaan");
            Directory.CreateDirectory(appData);
            var logPath = Path.Combine(appData, "cleanup_log.jsonl");

            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                deletedFiles = result.DeletedFiles,
                freedBytes = result.FreedBytes,
                errors = result.Errors.Take(50).ToList(),
                categories = categories.Select(c => new { c.Id, c.Name, c.RequiresAdmin }).ToList()
            };

            var json = JsonSerializer.Serialize(entry);
            File.AppendAllText(logPath, json + Environment.NewLine);
        }
        catch
        {
            // best-effort logging
        }
    }

    private class CleanupDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool RequiresAdmin { get; set; }
        public bool IsRecycleBin { get; set; }
        public Func<IEnumerable<CleanupTarget>> GetTargets { get; set; } = () => Array.Empty<CleanupTarget>();
    }

    private static class NativeMethods
    {
        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOSOUND = 0x00000004;
        public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
    }

    private static bool TryDeleteFile(string file, out long freedBytes)
    {
        freedBytes = 0;
        try
        {
            if (!File.Exists(file))
                return false;
            var info = new FileInfo(file);
            var length = info.Length;
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes = FileAttributes.Normal;
            info.Delete();
            freedBytes = length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLockedException(Exception ex)
    {
        if (ex is IOException io)
        {
            const int sharingViolation = unchecked((int)0x80070020);
            const int lockViolation = unchecked((int)0x80070021);
            return io.HResult == sharingViolation || io.HResult == lockViolation;
        }
        return false;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return true;
        if (ex is IOException io)
        {
            const int accessDenied = unchecked((int)0x80070005);
            return io.HResult == accessDenied;
        }
        return false;
    }
}

public class CleanupScanResult
{
    public List<CleanupCategory> Categories { get; set; } = new();
}

public class CleanupCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RequiresAdmin { get; set; }
    public bool IsRecycleBin { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsApproximate { get; set; }
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public List<CleanupTarget> Targets { get; set; } = new();
}

public class CleanupTarget
{
    public string Path { get; set; } = "";
    public string? Pattern { get; set; }
    public bool Recurse { get; set; } = true;
    public int? MaxFiles { get; set; }
    public long? MaxBytes { get; set; }
    public int? ScanMaxDepth { get; set; }

    public bool IsPattern => !string.IsNullOrWhiteSpace(Pattern);

    public static CleanupTarget Directory(string path)
    {
        return new CleanupTarget { Path = path, Pattern = null, Recurse = true };
    }

    public static CleanupTarget FilePattern(string path, string pattern)
    {
        return new CleanupTarget { Path = path, Pattern = pattern, Recurse = false };
    }

    public CleanupTarget WithLimits(int? maxFiles = null, long? maxBytes = null)
    {
        MaxFiles = maxFiles;
        MaxBytes = maxBytes;
        return this;
    }

    public CleanupTarget WithScanDepth(int? maxDepth)
    {
        ScanMaxDepth = maxDepth;
        return this;
    }
}

public class CleanupResult
{
    public int DeletedFiles { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> SkippedCategories { get; set; } = new();
    public int LockedFiles { get; set; }
    public int AccessDeniedFiles { get; set; }
    public int ScheduledOnReboot { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}

public class CleanupProgress
{
    public string Phase { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Target { get; set; }
    public string Message { get; set; } = "";
}
#endif

