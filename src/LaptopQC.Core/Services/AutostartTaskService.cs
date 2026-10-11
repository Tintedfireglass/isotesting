using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LaptopQC.Core.Services;

/// <summary>
/// Registers/manages the Windows Scheduled Task that launches Pramaan in background
/// (system tray mode) automatically on user logon.
/// - Trigger: ONLOGON for the current user.
/// - Passes --background flag so the app starts silently with a tray icon and no UI.
/// - Runs at highest available privilege level.
/// - StartWhenAvailable=true so it catches up if logon happened while offline.
/// On macOS: no-op for v1 (launchd support planned for a future release).
/// </summary>
public static class AutostartTaskService
{
    private const string TaskName = "PramaanAutostart";

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
            Debug.WriteLine($"Autostart task registration failed: {ex.Message}");
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
        var xmlContent = BuildTaskXml(appExePath);
        var tempXml = Path.Combine(Path.GetTempPath(), "PramaanAutostart.xml");

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
                Debug.WriteLine($"Autostart task registered: {appExePath}");
            else
            {
                var err = process?.StandardError.ReadToEnd();
                Debug.WriteLine($"Autostart task registration failed (exit {process?.ExitCode}): {err}");
            }
        }
        finally
        {
            try { File.Delete(tempXml); } catch { }
        }
    }

    private static string BuildTaskXml(string exePath)
    {
        var taskCommand = $"\"{exePath}\"";

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Launches Pramaan silently at logon so heartbeat and auto-QC tasks can run on schedule</Description>
    <Author>Pramaan</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT30S</Delay>
    </LogonTrigger>
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
    <AllowHardTerminate>false</AllowHardTerminate>
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
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{taskCommand}</Command>
      <Arguments>--background</Arguments>
    </Exec>
  </Actions>
</Task>";
    }
}
