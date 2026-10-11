using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace LaptopQC.Core.Services;

/// <summary>
/// Registers/manages the Windows Scheduled Task for weekly automated basic QC.
/// The task runs the main app with --auto-basic-qc.
/// - Scheduled WEEKLY, anchored to the time the license or trial was activated.
/// - Uses XML-based registration so StartWhenAvailable=true is supported
///   (catches up if the machine was off or offline at the trigger time).
/// - Runs at highest available privilege level (required because app.manifest demands admin).
/// </summary>
public static class AutoBasicQcTaskService
{
    private const string TaskName = "PramaanAutoBasicQC";

    private static readonly string ActivationStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pramaan", "auto_basic_qc_activation.json");
    private static readonly string LastAutoQcRunFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pramaan", "last_auto_basic_qc_run.txt");

    public static void RecordLicenseActivation(string licenseKey) =>
        RecordActivation("license", licenseKey);

    public static void RecordTrialActivation(string email) =>
        RecordActivation("trial", email);

    public static void EnsureRegistered()
    {
        try
        {
            var exePath = FindAppExe();
            if (exePath == null)
                return;

            if (!TryGetActivationState(out var activationState))
                return;

            var taskExists = TaskExists();
            var desiredAnchor = activationState.ActivatedAtUtc;

            if (taskExists && TryReadStoredAnchor(out var storedAnchor) && storedAnchor == desiredAnchor)
                return;

            RegisterTask(exePath, desiredAnchor);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Auto basic QC task registration failed: {ex.Message}");
        }
    }

    private static void RecordActivation(string source, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return;

        try
        {
            if (TryGetActivationState(out var existing) &&
                existing.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                existing.Identifier.Equals(identifier, StringComparison.Ordinal))
            {
                return;
            }

            var state = new ActivationState
            {
                Source = source,
                Identifier = identifier,
                ActivatedAtUtc = DateTimeOffset.UtcNow
            };

            WriteActivationState(state);
            EnsureRegistered();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Auto basic QC activation recording failed: {ex.Message}");
        }
    }

    private static bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? FindAppExe()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        var appDir = AppContext.BaseDirectory;
        var appExe = Path.Combine(appDir, "LaptopQC.App.exe");
        if (File.Exists(appExe))
            return appExe;

        return null;
    }

    /// <summary>
    /// Returns the activation time for the weekly schedule.
    /// If no activation has been recorded yet, the weekly AutoQC remains unregistered.
    /// </summary>
    private static bool TryGetActivationState(out ActivationState state)
    {
        state = default!;

        try
        {
            if (!File.Exists(ActivationStateFile))
                return false;

            var json = File.ReadAllText(ActivationStateFile);
            var loaded = JsonSerializer.Deserialize<ActivationState>(json);
            if (loaded == null || string.IsNullOrWhiteSpace(loaded.Source) || string.IsNullOrWhiteSpace(loaded.Identifier))
                return false;

            state = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterTask(string appExePath, DateTimeOffset anchor)
    {
        var localAnchor = anchor.LocalDateTime;
        var dayOfWeek = localAnchor.DayOfWeek.ToString(); // e.g. "Monday"
        var startTime = localAnchor.ToString("HH:mm:ss");

        // Build the ISO 8601 start boundary (required for XML triggers)
        var startBoundary = localAnchor.ToString("yyyy-MM-ddTHH:mm:ss");

        var xmlContent = BuildTaskXml(appExePath, dayOfWeek, startTime, startBoundary);

        // Write XML to a temp file and register via schtasks /Create /XML
        var tempXml = Path.Combine(Path.GetTempPath(), "PramaanAutoBasicQC.xml");
        try
        {
            File.WriteAllText(tempXml, xmlContent, Encoding.Unicode);

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(15000);

            if (process?.ExitCode == 0)
            {
                Debug.WriteLine($"Auto basic QC task registered (weekly on {dayOfWeek} at {startTime}): {appExePath}");
            }
            else
            {
                var err = process?.StandardError.ReadToEnd();
                Debug.WriteLine($"Auto basic QC task registration failed (exit {process?.ExitCode}): {err}");
            }
        }
        finally
        {
            try { File.Delete(tempXml); } catch { }
        }
    }

    private static bool TryReadStoredAnchor(out DateTimeOffset anchor)
    {
        anchor = default;

        try
        {
            if (!File.Exists(ActivationStateFile))
                return false;

            var state = JsonSerializer.Deserialize<ActivationState>(File.ReadAllText(ActivationStateFile));
            if (state == null)
                return false;

            anchor = state.ActivatedAtUtc;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteActivationState(ActivationState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActivationStateFile)!);
            File.WriteAllText(
                ActivationStateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort only; the task itself was already registered successfully.
        }
    }

    public static DateTimeOffset? GetActivationAnchor()
    {
        return TryGetActivationState(out var state) ? state.ActivatedAtUtc : null;
    }

    public static bool IsAutoQcDue(TimeSpan interval)
    {
        if (!TryGetActivationState(out var state))
            return false;

        if (TryReadLastAutoQcRun(out var lastRun))
            return DateTimeOffset.UtcNow - lastRun >= interval;

        return DateTimeOffset.UtcNow - state.ActivatedAtUtc >= interval;
    }

    public static void MarkAutoQcRunCompleted()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastAutoQcRunFile)!);
            File.WriteAllText(LastAutoQcRunFile, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static bool TryReadLastAutoQcRun(out DateTimeOffset lastRun)
    {
        lastRun = default;

        try
        {
            if (!File.Exists(LastAutoQcRunFile))
                return false;

            return DateTimeOffset.TryParse(
                File.ReadAllText(LastAutoQcRunFile).Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out lastRun);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildTaskXml(string exePath, string dayOfWeek, string startTime, string startBoundary)
    {
        // Map .NET DayOfWeek name to the Task Scheduler XML day element name.
        // The XML uses full day names: Monday, Tuesday, Wednesday, etc.
        var taskCommand = $"\"{exePath}\"";
        var taskArgs = "--auto-basic-qc";

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Pramaan weekly automated basic QC health check</Description>
    <Author>Pramaan</Author>
  </RegistrationInfo>
  <Triggers>
    <WeeklyTrigger>
      <StartBoundary>{startBoundary}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByWeek>
        <WeeksInterval>1</WeeksInterval>
        <DaysOfWeek>
          <{dayOfWeek} />
        </DaysOfWeek>
      </ScheduleByWeek>
    </WeeklyTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{taskCommand}</Command>
      <Arguments>{taskArgs}</Arguments>
    </Exec>
  </Actions>
	</Task>";
    }

    private sealed class ActivationState
    {
        public string Source { get; set; } = "";
        public string Identifier { get; set; } = "";
        public DateTimeOffset ActivatedAtUtc { get; set; }
    }
}
