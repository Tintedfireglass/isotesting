using System.Diagnostics;

namespace Pramaan.CLI.Diagnostics;

/// <summary>
/// Runs Linux CLI commands and captures their output.
/// All diagnostic implementations use this helper.
/// </summary>
internal static class LinuxCommandRunner
{
    /// <summary>Runs a command and returns stdout, throws on failure.</summary>
    public static string Run(string command, string arguments, int timeoutMs = 15000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{command} timed out after {timeoutMs} ms");
        }
        // smartctl uses non-zero exit codes to report real health warnings.
        // Retain both streams instead of throwing away diagnostic evidence.
        return output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult();
    }

    /// <summary>Runs a command and returns stdout, or empty string on any failure.</summary>
    public static string TryRun(string command, string arguments, int timeoutMs = 15000)
    {
        try { return Run(command, arguments, timeoutMs); }
        catch { return string.Empty; }
    }

    /// <summary>Reads a file from the filesystem, returns empty on failure.</summary>
    public static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Reads all lines from a sysfs file, returns empty on failure.</summary>
    public static string[] ReadLines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Runs a command and returns a single trimmed line.</summary>
    public static string RunSingleLine(string command, string arguments)
        => TryRun(command, arguments).Trim().Split('\n')[0].Trim();

    /// <summary>Checks if a command is available on PATH.</summary>
    public static bool IsCommandAvailable(string command)
    {
        var result = TryRun("which", command);
        return !string.IsNullOrWhiteSpace(result);
    }
}
