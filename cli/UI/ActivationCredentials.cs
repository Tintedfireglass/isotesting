using System.Text.Json;
using LaptopQC.Core.Models;

namespace Pramaan.CLI.UI;

/// <summary>Machine-local credential hints, never offline authorization or report data.</summary>
public sealed class ActivationCredentials
{
    readonly string path;
    public ActivationCredentials(string? path = null) => this.path = path ?? Path.Combine(AppPaths.AppDataDir, "linux_activation.json");
    public record Hint(bool Trial, string Credential, string Identity);
    public Hint? Read(string identity)
    {
        try
        {
            var hint = JsonSerializer.Deserialize<Hint>(File.ReadAllText(path));
            return hint != null && hint.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase) ? hint : null;
        }
        catch { return null; }
    }
    public void Save(bool trial, string credential, string identity)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            { JsonSerializer.Serialize(stream, new Hint(trial, credential.Trim(), identity)); stream.Flush(true); }
            File.Move(temp, Path.GetFullPath(path), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
