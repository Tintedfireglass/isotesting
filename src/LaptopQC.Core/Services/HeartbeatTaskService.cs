using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LaptopQC.Core.Services;

/// <summary>
/// Registers/manages the Windows Scheduled Task for periodic heartbeats.
/// The task runs the main app with --heartbeat to check in with the server and refresh last_seen.
/// - Runs every 4 hours.
/// - Uses XML-based registration so StartWhenAvailable=true is supported
///   (catches up if the machine was off at the trigger time).
/// - Runs at highest available privilege level (required because app.manifest demands admin).
/// On macOS: no-op for v1 (launchd support planned for a future release).
/// </summary>
public static class HeartbeatTaskService
{
    private const string TaskName = "PramaanHeartbeat";

    public static void EnsureRegistered()
    {
        // Windows-only: macOS uses launchd instead (not yet implemented for v1)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            // Always re-register on startup so updates to the exe path or task settings
            // are applied automatically. The /F flag forces overwrite of any existing task.
            var exePath = FindAppExe();
            if (exePath == null)
                return;

            RegisterTask(exePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Heartbeat task registration failed: {ex.Message}");
        }
    }

    private static bool IsTaskRegistered()
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
        catch
        {
            return false;
        }
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

    private static void RegisterTask(string appExePath)
    {
        // Start time: now + 5 minutes
        var startBoundary = DateTime.Now.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss");
        var xmlContent = BuildTaskXml(appExePath, startBoundary);

        var tempXml = Path.Combine(Path.GetTempPath(), "PramaanHeartbeat.xml");
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
                Debug.WriteLine($"Heartbeat task registered (every 4 hours): {appExePath}");
            else
            {
                var err = process?.StandardError.ReadToEnd();
                Debug.WriteLine($"Heartbeat task registration failed (exit {process?.ExitCode}): {err}");
            }
        }
        finally
        {
            try { File.Delete(tempXml); } catch { }
        }
    }

    private static string BuildTaskXml(string exePath, string startBoundary)
    {
        var taskCommand = $"\"{exePath}\"";

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Pramaan periodic heartbeat — keeps device last_seen timestamp fresh on the server</Description>
    <Author>Pramaan</Author>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition>
        <Interval>PT4H</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>{startBoundary}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
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
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{taskCommand}</Command>
      <Arguments>--heartbeat</Arguments>
    </Exec>
  </Actions>
</Task>";
    }
}
