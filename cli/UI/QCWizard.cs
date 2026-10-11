using Spectre.Console;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;
using LaptopQC.Core.Diagnostics;
using Pramaan.CLI.Diagnostics;

namespace Pramaan.CLI.UI;

/// <summary>
/// Runs the Full QC wizard: Auth → Session Metadata → Automated Diagnostics 
/// → Interactive Manual Tests → Grade → Submit.
/// All UI output is via AnsiConsole (blocking, sequential).
/// </summary>
public class QCWizard
{
    private readonly DashboardState _state;
    private readonly bool _headless;
    private string? _lastMsg;

    public QCWizard(DashboardState state, bool headless = false)
    {
        _state = state;
        _headless = headless;
    }

    public async Task<QCReport?> RunAsync()
    {
        var report = new QCReport { Timestamp = DateTime.Now, AppVersion = "1.0.0L" };
        _state.UpdateFromReport(report);
        _state.FooterMessage = "";
        _state.ProgressCpu = _state.ProgressRam = _state.ProgressStorage = _state.ProgressGpu = _state.ProgressComp = 0;
        _state.Elapsed = "00:00:00";

        // ── Phase 1: Auth ─────────────────────────────────────────────────
        _state.StatusMessage = "Checking authentication...";
        Refresh();

        var authService = new AuthService();
        var trialService = new TrialService();
        string? activeToken = null;

        if (authService.IsLoggedIn)
        {
            _state.StatusMessage = "License session active.";
            activeToken = authService.Token;
            report.DeviceId = authService.MachineId ?? 0;
            _state.DeviceId = authService.MachineId ?? 0; // Show in dashboard
        }
        else if (trialService.IsTrialActive)
        {
            _state.StatusMessage = $"✓ Trial active: {trialService.CurrentTrial?.Email}";
            activeToken = trialService.CurrentTrial?.Token;
            report.DeviceId = trialService.CurrentTrial?.MachineId ?? 0;
            _state.DeviceId = report.DeviceId;
        }
        else
        {
            var choice = RunMenuPrompt("Activation Required", new[]
            {
                "Activate License Key",
                "Start 7-Day Free Trial",
                "Continue Unactivated (Limited)"
            });

            if (choice == 0)
            {
                var key = AnsiConsole.Prompt(new TextPrompt<string>("Enter [green]License Key[/]:").Secret());
                LoginResult? result = null;
                await AnsiConsole.Status().StartAsync("Activating...", async ctx =>
                {
                    var sysDiag = new LinuxSystemDiagnostic();
                    var si = sysDiag.GetInfo();
                    var serial = MachineIdentityService.GetBestIdentityKey(si.SerialNumber, si.MacAddress, si.ComputerName);
                    result = await authService.LoginWithLicenseAsync(key, serial, si.MacAddress, si.ComputerName);
                });
                if (result?.Success == true)
                {
                    activeToken = authService.Token;
                    report.DeviceId = authService.MachineId ?? 0;
                    _state.DeviceId = authService.MachineId ?? 0; // Show in dashboard
                    _state.StatusMessage = "✓ Activation successful!";
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Activation failed:[/] {(result?.Message ?? "Unknown error").EscapeMarkup()}");
                }
            }
            else if (choice == 1)
            {
                var email = AnsiConsole.Prompt(
                    new TextPrompt<string>("Enter [yellow]Email Address[/]:")
                        .Validate(e => e.Contains('@') ? ValidationResult.Success() : ValidationResult.Error("Invalid email")));
                TrialResult? result = null;
                await AnsiConsole.Status().StartAsync("Starting trial...", async ctx =>
                {
                    var sysDiag = new LinuxSystemDiagnostic();
                    var si = sysDiag.GetInfo();
                    var serial = MachineIdentityService.GetBestIdentityKey(si.SerialNumber, si.MacAddress, si.ComputerName);
                    result = await trialService.StartOrRefreshTrialAsync(email, serial, si.MacAddress, si.ComputerName);
                });
                if (result?.Success == true)
                {
                    activeToken = result.Token;
                    report.DeviceId = result.MachineId;
                    _state.DeviceId = report.DeviceId;
                    _state.StatusMessage = $"✓ Trial started! {result.DaysRemaining} days remaining.";
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Trial failed:[/] {(result?.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
                }
            }
            else
            {
                _state.StatusMessage = "Running unactivated (limited).";
            }
        }

        Refresh();

        // ── Phase 2: Session Metadata ──────────────────────────────────────
        AnsiConsole.MarkupLine("\n[bold cyan]── Session Details ──[/]");
        report.RefurbishId = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]Refurbish ID / Order ID[/] (optional):").AllowEmpty());
        report.TechnicianNotes = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]Technician Notes[/] (optional):").AllowEmpty());
        report.PhysicalCondition = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Select [green]Physical condition[/]:").AddChoices("A+", "A", "B", "C", "D", "E"));
        report.ScratchesAndDents = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Select [green]Scratches and dents[/]:").AddChoices("None", "Minor", "Major"));

        // ── Phase 3: Automated Diagnostics ────────────────────────────────
        _state.StatusMessage = "Running automated diagnostics...";
        
        if (!_headless) AnsiConsole.Clear();

        if (_headless)
        {
            await RunAutomatedDiagnostics(report);
        }
        else
        {
            await DashboardDisplay.RunAsync(_state, () => RunAutomatedDiagnostics(report));
        }

        if (!_headless) AnsiConsole.Clear();
        // ── Phase 4: Interactive Manual Tests ─────────────────────────────
        AnsiConsole.MarkupLine("\n[bold cyan]── Interactive Component Tests ──[/]");
        AnsiConsole.MarkupLine("[grey]Answer Y/N based on physical testing.[/]\n");

        _state.ProgressComp = 10; Refresh();

        report.KeyboardTest = ManualTest("Keyboard", "All keys register correctly");
        _state.ProgressComp = 30; Refresh();

        report.TrackpadTest = ManualTest("Trackpad", "Mouse movement and clicks work");
        _state.ProgressComp = 50; Refresh();

        // USB Ports Test
        AnsiConsole.MarkupLine("\n[bold cyan]Testing USB Ports...[/]");
        var usbDiag = new LinuxUsbPortDiagnostic();
        var (usbHealthy, usbMsg) = usbDiag.QuickValidation();
        report.UsbTest = new TestResult 
        { 
            Tested = true, 
            Passed = usbHealthy, 
            Message = usbMsg 
        };
        _state.ProgressComp = 65; Refresh();

        report.AudioVideoTest = ManualTest("Audio/Video", "Speakers and webcam work");
        _state.ProgressComp = 80; Refresh();

        report.AudioJackTest = ManualTest("Audio Jack", "Headphone jack outputs sound");
        _state.ProgressComp = 90; Refresh();

        // Ethernet Test
        AnsiConsole.MarkupLine("\n[bold cyan]Testing Ethernet...[/]");
        var ethDiag = new LinuxEthernetDiagnostic();
        var ethResult = ethDiag.RunDiagnostic();
        var ethOk = ethResult.HasWorkingPort;
        report.NetworkTest = new TestResult
        {
            Tested = true,
            Passed = ethOk || report.NetworkTest.Passed, // Keep WiFi result if already tested
            Message = ethOk ? ethResult.Summary : (report.NetworkTest.Passed ? report.NetworkTest.Message : "No network connection")
        };
        if (ethOk)
        {
            var connectedPort = ethResult.DetectedPorts.First(p => p.IsConnected);
            report.NetworkTest.Details.Add($"Ethernet: {connectedPort.InterfaceName} @ {connectedPort.LinkSpeed}");
        }
        _state.ProgressComp = 100; Refresh();

        // ── Phase 5: Grade & Submit ────────────────────────────────────────
        _state.StatusMessage = "Grading and submitting...";
        Refresh();

        var gradingService = new GradingService();
        var configService = new PramaanConfigService();
        var liveConfig = await configService.GetActiveConfigAsync();
        gradingService.GradeReport(report, liveConfig);

        var submitService = new QCSubmissionService();
        SubmitResult submitResult;
        try
        {
            var saved = SavedReportStore.Create(submitService.PrepareReport(report, authService.GetTechnicianId()));
            _state.SavedReports.Insert(0, saved); // Retain in memory even if saving fails.
            submitResult = await _state.Store.SubmitAsync(saved, submitService, activeToken);
        }
        catch (Exception ex)
        {
            submitResult = new SubmitResult { ErrorMessage = $"Report storage/preparation failed: {ex.Message}. Report retained in memory; open Saved reports to retry." };
        }

        _state.UpdateFromReport(report);

        if (submitResult.Success)
        {
            var apiConfig = new ApiConfiguration();
            var domain = apiConfig.ApiUrl.Replace("/api", "").TrimEnd('/');
            var url = $"{domain}/verify/{report.HealthId}";
            _state.FooterMessage = $"✓ Submitted! Report: {url}";
            _state.RecentReports.Insert(0, new RecentReportEntry(
                report.HealthId[..8].ToUpper(),
                DateTime.Now.ToString("MMM d, HH:mm"),
                $"{report.PramaanResult?.OverallHealthScore ?? report.OverallScore}/100",
                "Verified"));
            _state.SaveRecentReports();
        }
        else
        {
            _state.FooterMessage = $"Submission failed: {submitResult.ErrorMessage}. Open Saved reports / Retry; Messages shows full error.";
        }
        if (submitResult.StorageError != null) _state.FooterMessage += " | " + submitResult.StorageError;

        _state.StatusMessage = $"QC Complete! Score: {_state.OverallScore}/100 ({_state.GradeLabel})";
        Refresh();

        return report;
    }

    async Task RunAutomatedDiagnostics(QCReport report)
    {
        // System Info
        _state.StatusMessage = "Gathering system info...";
        Refresh();
        var sysDiag = new LinuxSystemDiagnostic();
        report.SystemInfo = sysDiag.GetInfo();
        report.MacAddress = report.SystemInfo?.MacAddress ?? "";
        _state.SystemInfo = report.SystemInfo;

        // CPU
        _state.StatusMessage = "Gathering CPU info...";
        Refresh();
        var cpuDiag = new LinuxCpuDiagnostic();
        report.CpuDetails = cpuDiag.GetInfo();
        var cpuVal = cpuDiag.ValidateCpu(report.CpuDetails);
        report.CpuTest = new TestResult { Tested = true, Passed = cpuVal.IsHealthy, Message = cpuVal.Message };
        report.CpuTest.Details.Add(report.CpuDetails.Name);
        report.CpuTest.Details.Add($"{report.CpuDetails.Cores} cores / {report.CpuDetails.Threads} threads");

        // RAM
        _state.StatusMessage = "Gathering RAM info...";
        Refresh();
        var ramDiag = new LinuxRamDiagnostic();
        report.RamDetails = ramDiag.GetInfo();
        var ramVal = ramDiag.ValidateRam(report.RamDetails);
        report.RamTest = new TestResult { Tested = true, Passed = ramVal.IsHealthy, Message = ramVal.Message };
        report.RamTest.Details.Add($"{report.RamDetails.TotalCapacityGB} GB Total");

        // Storage
        _state.StatusMessage = "Gathering storage info...";
        Refresh();
        var storageDiag = new LinuxStorageDiagnostic();
        report.StorageDetails = storageDiag.GetInfo();
        var storVal = storageDiag.ValidateStorage(report.StorageDetails);
        report.StorageTest = new TestResult { Tested = true, Passed = storVal.IsHealthy, Message = storVal.Message };

        // Battery
        _state.StatusMessage = "Gathering battery info...";
        Refresh();
        var battDiag = new LinuxBatteryDiagnostic();
        report.BatteryDetails = battDiag.GetInfo();
        var batVal = battDiag.ValidateBattery(report.BatteryDetails);
        report.BatteryTest = new TestResult { Tested = true, Passed = batVal.IsHealthy, Message = batVal.Message };

        // Devices
        _state.StatusMessage = "Gathering device info...";
        Refresh();
        var devDiag = new LinuxDeviceDiagnostic();
        report.DeviceDetails = devDiag.GetInfo();
        var wifiOk = report.DeviceDetails.NetworkDevices.Any(n => n.AdapterType == "WiFi" && n.IsConnected);
        var ethOk = report.DeviceDetails.NetworkDevices.Any(n => n.AdapterType == "Ethernet" && n.IsConnected);
        report.NetworkTest = new TestResult
        {
            Tested = true,
            Passed = wifiOk || ethOk,
            Message = (wifiOk || ethOk) ? "Network OK" : "No active connection"
        };

        _state.UpdateFromReport(report);
        _state.ProgressStorage = 40;
        Refresh();

        // SMART
        _state.StatusMessage = "Running SMART health checks...";
        Refresh();
        var smartSvc = new LinuxSmartTestService();
        if (smartSvc.IsAvailable)
        {
            var hc = smartSvc.QuickHealthCheck();
            foreach (var dev in hc.Devices)
            {
                report.StorageTest.Details.Add($"[SMART] {dev.Model}: {dev.HealthStatus} ({dev.HealthScore}%)");
                // Match priority:
                //  1. Exact DeviceId match        (/dev/nvme0n1 == /dev/nvme0n1)
                //  2. NVMe prefix match            (/dev/nvme0n1 starts with /dev/nvme0)
                //  3. Model substring match
                //  4. Single-drive fallback        (only 1 SMART device + 1 storage device)
                var sd = report.StorageDetails?.Devices.FirstOrDefault(d =>
                    (d.DeviceId != null && d.DeviceId.Equals(dev.DevicePath, StringComparison.OrdinalIgnoreCase)) ||
                    (d.DeviceId != null && d.DeviceId.StartsWith(dev.DevicePath, StringComparison.OrdinalIgnoreCase)) ||
                    d.Model.Contains(dev.Model, StringComparison.OrdinalIgnoreCase) ||
                    dev.Model.Contains(d.Model, StringComparison.OrdinalIgnoreCase));
                // Single-drive fallback: if no match found but there is exactly one storage device, use it
                sd ??= (report.StorageDetails?.Devices.Count == 1 && hc.Devices.Count == 1)
                    ? report.StorageDetails.Devices[0] : null;
                if (sd != null) 
                { 
                    sd.HealthPercent = dev.HealthScore; 
                    sd.Temperature = dev.Temperature; 
                    sd.PowerOnHours = dev.PowerOnHours; 
                    
                    // Overwrite placeholder name (e.g. "NVME0N1") with the real vendor model
                    if (!string.IsNullOrWhiteSpace(dev.Model) && !dev.Model.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
                    {
                        sd.Model = dev.Model;
                    }
                    
                    // Populate serial if lsblk didn't provide one
                    if (string.IsNullOrWhiteSpace(sd.SerialNumber) && !string.IsNullOrWhiteSpace(dev.SerialNumber))
                    {
                        sd.SerialNumber = dev.SerialNumber;
                    }
                }

                if (dev.HealthPassed)
                {
                    _state.StatusMessage = $"SMART self-test: {dev.Model}...";
                    Refresh();
                    var prog = new System.Progress<LaptopQC.Core.Diagnostics.SmartTestProgress>(p =>
                    {
                        _state.ProgressStorage = 40 + (int)(p.PercentComplete * 0.2);
                        _state.StatusMessage = $"SMART {dev.Model}: {p.Status}";
                        Refresh();
                    });
                    var tr = await smartSvc.RunShortTestAsync(dev.DevicePath, prog, dev.DeviceType);
                    var msgSuffix = !string.IsNullOrWhiteSpace(tr.Message) && !tr.Passed ? $" ({tr.Message})" : "";
                    report.StorageTest.Details.Add($"Self-Test {dev.Model}: {(tr.Passed ? "Passed" : "Failed")}{msgSuffix}");
                }
            }

            // Authoritative pass/fail comes directly from SMART — bypasses any prior
            // 'Inconclusive' result that fired before SMART data was available.
            // Also clear IsInconclusive so the GradingService doesn't hard-cap to 35.
            if (report.StorageDetails != null)
            {
                report.StorageDetails.IsInconclusive = false;
                report.StorageDetails.InconclusiveReason = null;
            }
            report.StorageTest.Passed = hc.OverallHealthy;
            report.StorageTest.Message = hc.OverallHealthy
                ? $"{hc.Devices.Count} drive(s) healthy (SMART)"
                : $"SMART health warning on {hc.Devices.Count(d => !d.HealthPassed)} drive(s)";
        }
        else
        {
            report.StorageTest.Details.Add("SMART tools not available");
        }
        _state.ProgressStorage = 100;
        Refresh();

        // CPU Stress
        _state.StatusMessage = "CPU Stress Test (15s)...";
        Refresh();
        var cpuStress = new LinuxCpuStressTest(durationSeconds: 15);
        cpuStress.OnProgress += p =>
        {
            _state.ProgressCpu = p.PercentComplete;
            _state.StatusMessage = $"CPU Stress: {p.Status}";
            Refresh();
        };
        var cpuResult = await cpuStress.RunAsync();
        report.CpuTest.Passed &= cpuResult.Passed;
        report.CpuTest.Details.Add(cpuResult.Message);
        _state.ProgressCpu = 100;
        Refresh();

        // RAM Stress
        _state.StatusMessage = "RAM Stress Test...";
        Refresh();
        var ramStress = new LinuxRamStressTest(testSizeMB: 512, iterations: 2);
        ramStress.OnProgress += p =>
        {
            _state.ProgressRam = p.PercentComplete;
            _state.StatusMessage = $"RAM Stress: Iteration {p.CurrentIteration}/{p.TotalIterations}";
            Refresh();
        };
        var ramResult = await ramStress.RunAsync();
        report.RamTest.Passed &= ramResult.Passed;
        report.RamTest.Details.Add(ramResult.Message);
        _state.ProgressRam = 100;
        Refresh();

        // GPU Stress
        _state.StatusMessage = "GPU Stress Test (15s)...";
        Refresh();
        var gpuStress = new LinuxGpuStressTest(durationSeconds: 15);
        gpuStress.OnProgress += p =>
        {
            _state.ProgressGpu = p.PercentComplete;
            _state.StatusMessage = $"GPU Stress: {p.Status}";
            Refresh();
        };
        var gpuResult = await gpuStress.RunAsync();
        report.GpuTest = new TestResult
        {
            Tested = true, Passed = gpuResult.Passed, Message = gpuResult.Message
        };
        report.GpuTest.Details.Add($"GPU: {gpuResult.GpuName}");
        if (gpuResult.MaxTemp > 0) report.GpuTest.Details.Add($"Max Temp: {gpuResult.MaxTemp:F1}°C");
        _state.ProgressGpu = 100;

        _state.UpdateFromReport(report);
        Refresh();
    }

    static TestResult ManualTest(string component, string instructions)
    {
        bool passed = AnsiConsole.Confirm($"Did [bold white]{component}[/] work correctly? [grey]({instructions})[/]");
        return new TestResult { Tested = true, Passed = passed, Message = "Manual verification" };
    }

    static int RunMenuPrompt(string title, string[] options)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{title}[/]")
                .PageSize(options.Length + 1)
                .AddChoices(options));
        return Array.IndexOf(options, choice);
    }

    void Refresh()
    {
        if (_headless)
        {
            if (!string.IsNullOrEmpty(_state.StatusMessage) && _state.StatusMessage != _lastMsg)
            {
                AnsiConsole.MarkupLine($"[grey]{_state.StatusMessage.EscapeMarkup()}[/]");
                _lastMsg = _state.StatusMessage;
            }
            return;
        }

        // The live display owns rendering; callbacks only update state.
    }
}
