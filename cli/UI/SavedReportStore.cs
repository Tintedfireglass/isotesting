using System.Text.Json;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;

namespace Pramaan.CLI.UI;

public sealed class SavedReport
{
    public int Version { get; set; } = 1;
    public string ReportId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string HealthId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class SavedReportStore
{
    public string DirectoryPath { get; }
    public bool IsPersistent => Environment.GetEnvironmentVariable("PRAMAAN_REPORTS_PERSISTENT") == "1";
    public List<string> LoadErrors { get; } = new();

    public SavedReportStore(string? directory = null)
    {
        DirectoryPath = directory ?? Environment.GetEnvironmentVariable("PRAMAAN_REPORTS_DIR")
            ?? Path.Combine(AppPaths.AppDataDir, "SavedReports");
    }

    public static SavedReport Create(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new SavedReport
        {
            ReportId = root.GetProperty("reportId").GetString()!,
            MachineId = root.GetProperty("machineId").GetString()!,
            HealthId = root.GetProperty("healthId").GetString()!,
            Payload = payload
        };
    }

    public void Save(SavedReport report)
    {
        Validate(report);
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(DirectoryPath, $"{report.ReportId}.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, report);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public List<SavedReport> Load()
    {
        LoadErrors.Clear();
        var reports = new List<SavedReport>();
        if (!Directory.Exists(DirectoryPath)) return reports;
        try
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    var report = JsonSerializer.Deserialize<SavedReport>(File.ReadAllText(path))
                        ?? throw new InvalidDataException("Empty report file");
                    Validate(report);
                    reports.Add(report);
                }
                catch (Exception ex) { LoadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { LoadErrors.Add(ex.Message); }
        return reports.OrderByDescending(r => r.SavedAtUtc).ToList();
    }

    static void Validate(SavedReport report)
    {
        if (report.Version != 1 || !Guid.TryParse(report.ReportId, out _))
            throw new InvalidDataException("Unsupported report version or ID");
        var payload = Create(report.Payload);
        if (payload.ReportId != report.ReportId || payload.MachineId != report.MachineId || payload.HealthId != report.HealthId)
            throw new InvalidDataException("Saved report identity does not match its payload");
    }

    public async Task<SubmitResult> SubmitAsync(SavedReport report, QCSubmissionService service, string? token)
    {
        report.LastAttemptUtc = DateTime.UtcNow;
        report.Status = "Pending";
        report.LastError = "Upload interrupted; retry uses the same report ID.";
        Save(report); // Never send a report that has not been saved first.
        var result = await service.SubmitPreparedAsync(report.Payload, token);
        report.Status = result.Success ? "Submitted" : result.IsDuplicate ? "Verify duplicate" : "Pending";
        report.LastError = result.Success ? null : result.ErrorMessage;
        try { Save(report); }
        catch (Exception ex) { result.StorageError = $"Upload outcome could not be saved: {ex.Message}. Retained in memory."; }
        return result;
    }
}
