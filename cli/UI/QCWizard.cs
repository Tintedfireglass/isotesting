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
        var activation = await OnlineActivation.RequireAsync(_state);
        if (activation == null) return null;
        var report = new QCReport { Timestamp = DateTime.Now, AppVersion = "1.0.0L", DeviceId = activation.MachineId };
        _state.UpdateFromReport(report);
        _state.ProgressCpu = _state.ProgressRam = _state.ProgressStorage = _state.ProgressGpu = _state.ProgressComp = 0;
        _state.Elapsed = "00:00:00";
        string activeToken = activation.Token;
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
            var saved = SavedReportStore.Create(submitService.PrepareReport(report, activation.TechnicianId));
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
        var smartSvc = new LinuxSmartTestService(targets: report.StorageDetails!.Devices.Select(d => d.DeviceId));
        var hc = smartSvc.QuickHealthCheck();
        report.StorageTest = LinuxStorageChecks.ApplyHealth(report.StorageDetails, hc, smartSvc.Errors);
        foreach (var dev in hc.Devices.Where(d => d.HealthPassed && !d.DeviceType.Contains("megaraid") && !d.DeviceType.Contains("cciss")))
        {
            _state.StatusMessage = $"SMART self-test: {dev.Model}...";
            var progress = new Progress<SmartTestProgress>(p =>
            {
                _state.ProgressStorage = 40 + p.PercentComplete / 2;
                _state.StatusMessage = $"SMART {dev.Model}: {p.Status}";
            });
            var test = await smartSvc.RunShortTestAsync(dev.DevicePath, progress, dev.DeviceType);
            report.StorageTest.Details.Add($"Self-test {dev.Model}: {test.Message}");
            if (test.Success && !test.Passed)
            {
                report.StorageTest.Passed = false;
                report.StorageTest.Message = $"Storage self-test failed: {dev.Model}. See Details.";
            }
        }        _state.ProgressStorage = 100;
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
