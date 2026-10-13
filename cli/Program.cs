using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using Spectre.Console;
using LaptopQC.Core.Diagnostics;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;
using LaptopQC.Hardware.Models;
using Pramaan.CLI.Diagnostics;
using Pramaan.CLI.UI;

namespace Pramaan.CLI;

class Program
{
    static DashboardState state = new();
    static bool isRunning = true;

    static Program()
    {
        state.LoadRecentReports();
        state.LoadSavedReports();
    }

    static async Task Main(string[] args)
    {
        // A real TTY remains interactive even when launched by a CI smoke test.
        AnsiConsole.Profile.Capabilities.Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (Environment.GetEnvironmentVariable("TERM") == "linux")
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.Standard;
        // ── CLI argument handling (non-interactive modes) ────────────────────
        if (args.Length > 0)
        {
            if (args[0].ToLowerInvariant() is "--diagnose" or "-d" or "--stress" or "-s" or "--test-usb" or "--test-ethernet")
            {
                if (await OnlineActivation.RequireAsync(state, interactive: false) == null)
                { Environment.ExitCode = 3; return; }
            }
            if (args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
            {
                var exitCode = await Pramaan.CLI.Agent.AgentCli.RunAsync(args);
                Environment.ExitCode = exitCode;
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    return;

                case "--version":
                case "-v":
                    AnsiConsole.MarkupLine("[purple]Pramaan CLI[/] v1.0.0 (linux-x64)");
                    return;

                case "--diagnose":
                case "-d":
                    Console.CursorVisible = true;
                    try { state.SystemInfo = new LinuxSystemDiagnostic().GetInfo(); } catch { }
                    await RunDiagnosticsOnly(null!);
                    PrintReport();
                    return;

                case "--stress":
                case "-s":
                    Console.CursorVisible = true;
                    await RunStressTestsOnly(null!);
                    PrintReport();
                    return;

                case "--full-qc":
                case "-f":
                    Console.CursorVisible = true;
                    try { state.SystemInfo = new LinuxSystemDiagnostic().GetInfo(); } catch { }
                    state.StatusMessage = "Starting Full QC Wizard (Sequential Mode)...";
                    await new QCWizard(state, headless: true).RunAsync();
                    PrintReport();
                    return;

                case "--heartbeat":
                    await RunHeartbeatAsync();
                    return;

                case "--auto-basic-qc":
                    await RunAutoBasicQcAsync();
                    return;

                case "--install-background":
                    InstallBackgroundServices();
                    return;

                case "--test-usb":
                    Console.CursorVisible = true;
                    await RunUsbTestAsync();
                    return;

                case "--test-ethernet":
                    Console.CursorVisible = true;
                    await RunEthernetTestAsync();
                    return;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown argument:[/] {args[0]}");
                    PrintHelp();
                    return;
            }
        }

        Console.CursorVisible = false;

        // Gather basic system info upfront for header display
        try
        {
            var sysDiag = new LinuxSystemDiagnostic();
            state.SystemInfo = sysDiag.GetInfo();
            
            // Device ID is only allocated after activation/login, not on startup
            // It will be populated when user activates or logs in
        }
        catch { /* non-fatal */ }

        while (isRunning)
        {
            int? actionToRun = null;
            Console.CursorVisible = false;

            while (Console.KeyAvailable) Console.ReadKey(true);

            var layout = DashboardRenderer.Build(state);

            await AnsiConsole.Live(layout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    ctx.Refresh();

                    while (isRunning)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(true);
                            DashboardDisplay.Navigate(key.Key, state);
                            switch (key.Key)
                            {
                                case ConsoleKey.UpArrow:
                                    state.SelectedMenuIndex =
                                        state.SelectedMenuIndex > 0
                                        ? state.SelectedMenuIndex - 1
                                        : DashboardState.MenuItems.Length - 1;
                                    break;

                                case ConsoleKey.DownArrow:
                                    state.SelectedMenuIndex =
                                        state.SelectedMenuIndex < DashboardState.MenuItems.Length - 1
                                        ? state.SelectedMenuIndex + 1
                                        : 0;
                                    break;

                                case ConsoleKey.Enter:
                                    actionToRun = state.SelectedMenuIndex;
                                    return; // Break out of Live context

                                case ConsoleKey.Q:
                                case ConsoleKey.Escape:
                                    isRunning = false;
                                    return; // Break out of Live context
                            }

                        }
                        DashboardDisplay.Refresh(ctx, state);
                        await Task.Delay(100);
                    }
                });

            if (!isRunning) break;

            if (actionToRun.HasValue)
            {
                Console.CursorVisible = true;
                AnsiConsole.Clear();
                await HandleMenuAction(actionToRun.Value);
                // Dashboard views already preserve the result. Only standalone
                // USB/Ethernet screens need a pause before returning to it.
                if (actionToRun.Value is 3 or 4)
                {
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to dashboard...[/]");
                    Console.ReadKey(true);
                }
                AnsiConsole.Clear();
            }
        }

        Console.CursorVisible = true;
        AnsiConsole.MarkupLine("\n[purple]Thank you for using Pramaan CLI.[/]\n");
    }

    static async Task HandleMenuAction(int action)
    {
        if (action is >= 1 and <= 4 && await OnlineActivation.RequireAsync(state) == null) return;
        switch (action)
        {
            case 0: // Run Full QC
                state.StatusMessage = "Starting Full QC Wizard...";
                await new QCWizard(state).RunAsync();
                break;

            case 1: // Run Diagnostics Only
                await RunDiagnosticsOnlyWithLive();
                break;

            case 2: // Run Stress Tests Only
                await RunStressTestsOnlyWithLive();
                break;

            case 3: // Test USB Ports
                await RunUsbTestAsync();
                break;

            case 4: // Test Ethernet
                await RunEthernetTestAsync();
                break;

            case 5: // View Results Table
                ShowResultsTable();
                break;

            case 6: // Settings (placeholder)
                state.StatusMessage = "Settings: Not yet implemented.";
                break;

            case 7: // Saved reports
                await SavedReportsScreen.ShowAsync(state);
                break;

            case 8: // Exit
                isRunning = false;
                break;
        }
    }

    static async Task RunDiagnosticsOnlyWithLive()
    {
        await DashboardDisplay.RunAsync(state, () => RunDiagnosticsOnly(null));
    }

    static async Task RunStressTestsOnlyWithLive()
    {
        await DashboardDisplay.RunAsync(state, () => RunStressTestsOnly(null));
    }

    static async Task RunDiagnosticsOnly(LiveDisplayContext? ctx)
    {
        state.StatusMessage = "Running hardware diagnostics...";
        state.ProgressStorage = 0;
        ctx?.UpdateTarget(DashboardRenderer.Build(state));
        ctx?.Refresh();

        var report = state.Report ?? new QCReport { AppVersion = "1.0.0L" };

        try
        {
            void Refresh(string msg) {
                state.StatusMessage = msg;
                if (ctx != null) { ctx.UpdateTarget(DashboardRenderer.Build(state)); ctx.Refresh(); }
                else if (!state.IsTesting) AnsiConsole.MarkupLine($"[grey]{msg.EscapeMarkup()}[/]");
            }

            Refresh("Gathering CPU info...");
            var cpuDiag = new LinuxCpuDiagnostic();
            report.CpuDetails = cpuDiag.GetInfo();
            var cpuVal = cpuDiag.ValidateCpu(report.CpuDetails);
            report.CpuTest = new TestResult { Tested = true, Passed = cpuVal.IsHealthy, Message = cpuVal.Message };
            report.CpuTest.Details.Add(report.CpuDetails.Name);

            Refresh("Gathering RAM info...");
            var ramDiag = new LinuxRamDiagnostic();
            report.RamDetails = ramDiag.GetInfo();
            var ramVal = ramDiag.ValidateRam(report.RamDetails);
            report.RamTest = new TestResult { Tested = true, Passed = ramVal.IsHealthy, Message = ramVal.Message };

            Refresh("Gathering storage info...");
            var storageDiag = new LinuxStorageDiagnostic();
            report.StorageDetails = storageDiag.GetInfo();
            var storVal = storageDiag.ValidateStorage(report.StorageDetails);
            report.StorageTest = new TestResult { Tested = true, Passed = storVal.IsHealthy, Message = storVal.Message };
            state.ProgressStorage = 50;

            Refresh("Gathering battery info...");
            var battDiag = new LinuxBatteryDiagnostic();
            report.BatteryDetails = battDiag.GetInfo();
            var batVal = battDiag.ValidateBattery(report.BatteryDetails);
            report.BatteryTest = new TestResult { Tested = true, Passed = batVal.IsHealthy, Message = batVal.Message };

            Refresh("Gathering device info...");
            var devDiag = new LinuxDeviceDiagnostic();
            report.DeviceDetails = devDiag.GetInfo();
            var wifiOk = report.DeviceDetails.NetworkDevices.Any(n => n.AdapterType == "WiFi" && n.IsConnected);
            var ethOk = report.DeviceDetails.NetworkDevices.Any(n => n.AdapterType == "Ethernet" && n.IsConnected);
            report.NetworkTest = new TestResult
            {
                Tested = true, Passed = wifiOk || ethOk,
                Message = (wifiOk || ethOk) ? "Network OK" : "No active connection"
            };

            // SMART
            Refresh("Running SMART checks...");
            var smartSvc = new LinuxSmartTestService(targets: report.StorageDetails!.Devices.Select(d => d.DeviceId));
            var health = smartSvc.QuickHealthCheck();
            report.StorageTest = LinuxStorageChecks.ApplyHealth(report.StorageDetails, health, smartSvc.Errors);
            state.ProgressStorage = 100;
            state.UpdateFromReport(report);
            state.StatusMessage = "Diagnostics complete!";
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Error: {ex.Message}";
        }

        ctx?.UpdateTarget(DashboardRenderer.Build(state));
        ctx?.Refresh();
    }

    static async Task RunStressTestsOnly(LiveDisplayContext? ctx)
    {
        state.StatusMessage = "Running stress tests...";
        state.ProgressCpu = 0; state.ProgressRam = 0; state.ProgressGpu = 0;
        ctx?.UpdateTarget(DashboardRenderer.Build(state)); ctx?.Refresh();

        var report = state.Report ?? new QCReport { AppVersion = "1.0.0L" };

        void Refresh(string msg) {
            state.StatusMessage = msg;
            if (ctx != null) { ctx.UpdateTarget(DashboardRenderer.Build(state)); ctx.Refresh(); }
            else if (!state.IsTesting) AnsiConsole.MarkupLine($"[grey]{msg.EscapeMarkup()}[/]");
        }

        try
        {
            var cpuStress = new LinuxCpuStressTest(durationSeconds: 15);
            cpuStress.OnProgress += p => { state.ProgressCpu = p.PercentComplete; Refresh($"CPU Stress: {p.Status}"); };
            var cpuResult = await cpuStress.RunAsync();
            if (!report.CpuTest.Tested)
                report.CpuTest = new TestResult { Tested = true, Passed = cpuResult.Passed, Message = cpuResult.Message };
            else
                report.CpuTest.Passed &= cpuResult.Passed;
            report.CpuTest.Details.Add(cpuResult.Message);
            state.ProgressCpu = 100; Refresh("CPU stress complete.");

            var ramStress = new LinuxRamStressTest(testSizeMB: 512, iterations: 2);
            ramStress.OnProgress += p => { state.ProgressRam = p.PercentComplete; Refresh($"RAM Stress: {p.CurrentIteration}/{p.TotalIterations}"); };
            var ramResult = await ramStress.RunAsync();
            if (!report.RamTest.Tested)
                report.RamTest = new TestResult { Tested = true, Passed = ramResult.Passed, Message = ramResult.Message };
            else
                report.RamTest.Passed &= ramResult.Passed;
            report.RamTest.Details.Add(ramResult.Message);
            state.ProgressRam = 100; Refresh("RAM stress complete.");

            var gpuStress = new LinuxGpuStressTest(durationSeconds: 15);
            gpuStress.OnProgress += p => { state.ProgressGpu = p.PercentComplete; Refresh($"GPU Stress: {p.Status}"); };
            var gpuResult = await gpuStress.RunAsync();
            report.GpuTest = new TestResult { Tested = true, Passed = gpuResult.Passed, Message = gpuResult.Message };
            report.GpuTest.Details.Add($"GPU: {gpuResult.GpuName}");
            if (gpuResult.MaxTemp > 0) report.GpuTest.Details.Add($"Max Temp: {gpuResult.MaxTemp:F1}°C");
            state.ProgressGpu = 100;

            state.UpdateFromReport(report);
            state.StatusMessage = "Stress tests complete!";
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Stress test error: {ex.Message}";
        }

        ctx?.UpdateTarget(DashboardRenderer.Build(state)); ctx?.Refresh();
    }

    static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold purple]Pramaan CLI[/] v1.0.0 — Hardware Quality Check Tool");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold white]USAGE:[/]");
        AnsiConsole.MarkupLine("  [green]pramaan[/]                          Launch interactive dashboard");
        AnsiConsole.MarkupLine("  [green]pramaan --help[/]                   Show this help message");
        AnsiConsole.MarkupLine("  [green]pramaan --version[/]                Show version info");
        AnsiConsole.MarkupLine("  [green]pramaan --diagnose[/]               Run hardware diagnostics (non-interactive)");
        AnsiConsole.MarkupLine("  [green]pramaan --stress[/]                 Run stress tests (non-interactive)");
        AnsiConsole.MarkupLine("  [green]pramaan --full-qc[/]                Run full QC (diagnostics + stress) non-interactively");
        AnsiConsole.MarkupLine("  [green]pramaan --test-usb[/]               Run USB port testing");
        AnsiConsole.MarkupLine("  [green]pramaan --test-ethernet[/]          Run Ethernet connectivity testing");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold white]BACKGROUND REPORTING (Silent):[/]");
        AnsiConsole.MarkupLine("  [green]pramaan --heartbeat[/]              Send online heartbeat (requires auth)");
        AnsiConsole.MarkupLine("  [green]pramaan --auto-basic-qc[/]          Run silent QC & submit to cloud (requires auth)");
        AnsiConsole.MarkupLine("  [green]sudo pramaan --install-background[/] Install systemd timers for automatic background testing");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold white]ALIASES:[/]  -h  -v  -d  -s  -f");
    }

    static void PrintReport()
    {
        var report = state.Report;
        if (report == null) { AnsiConsole.MarkupLine("[red]No report data generated.[/]"); return; }

        AnsiConsole.MarkupLine("");
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Purple).Expand();
        t.AddColumn(new TableColumn("[purple]Component[/]"));
        t.AddColumn(new TableColumn("[purple]Result[/]"));
        t.AddColumn(new TableColumn("[purple]Score[/]").RightAligned());
        t.AddColumn(new TableColumn("[purple]Details[/]"));

        void Row(string name, TestResult r)
        {
            if (!r.Tested) return;
            t.AddRow(
                $"[white]{name}[/]",
                r.Passed ? "[green]✓ PASS[/]" : "[red]✗ FAIL[/]",
                $"[grey]{r.Score}/100[/]",
                $"[grey]{r.Message.EscapeMarkup()}[/]"
            );
        }
        Row("CPU",     report.CpuTest);
        Row("RAM",     report.RamTest);
        Row("Storage", report.StorageTest);
        Row("Battery", report.BatteryTest);
        Row("GPU",     report.GpuTest);
        Row("Network", report.NetworkTest);
        AnsiConsole.Write(t);

        var score = report.PramaanResult?.OverallHealthScore ?? report.OverallScore;
        var grade = report.PramaanResult?.GradeBand ?? report.OverallGrade;
        AnsiConsole.MarkupLine($"\n[bold white]Overall Score:[/] [bold yellow]{score}/100[/]  Grade: [bold green]{grade}[/]\n");
    }

    static void ShowResultsTable()
    {
        state.SelectedView = 1;
        state.Page = 0;
        state.FooterMessage = "Component details: Page Up / Page Down for full text.";
    }
    // ── Background Auto-QC Methods ────────────────────────────────────────

    static async Task RunHeartbeatAsync()
    {
        try
        {
            var authService = new AuthService();
            
            if (!authService.IsLoggedIn)
            {
                var trialSvc = new TrialService();
                if (trialSvc.IsTrialActive)
                {
                    var t = trialSvc.CurrentTrial!;
                    authService.StartTrialSession(t.Email, t.Token, t.MachineId, t.TrialEndsAtUtc);
                }
            }

            if (!authService.IsLoggedIn && !authService.IsTrialSession)
            {
                Console.WriteLine("No active license or trial session found.");
                return;
            }

            var sysDiag = new LinuxSystemDiagnostic();
            var si = sysDiag.GetInfo();
            var serial = MachineIdentityService.GetBestIdentityKey(si.SerialNumber, si.MacAddress, si.ComputerName);

            if (!string.IsNullOrWhiteSpace(authService.LicenseKey))
            {
                await authService.LoginWithLicenseAsync(authService.LicenseKey, serial, si.MacAddress, si.ComputerName);
            }
            else if (authService.IsTrialSession)
            {
                await authService.SendTrialHeartbeatAsync(serial, si.MacAddress, si.ComputerName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Heartbeat failed: {ex.Message}");
        }
    }

    static async Task RunAutoBasicQcAsync()
    {
        try
        {
            var activation = await OnlineActivation.RequireAsync(state, interactive: false);
            if (activation == null) { Environment.ExitCode = 3; return; }
            var si = state.SystemInfo!;
            // Create a fresh report
            var report = new QCReport 
            { 
                ReportId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                TechnicianNotes = "Automated weekly component check",
                DeviceId = activation.MachineId
            };

            // Gather Data
            report.SystemInfo = si;
            report.MacAddress = si.MacAddress ?? "";

            var cpuDiag = new LinuxCpuDiagnostic();
            report.CpuDetails = cpuDiag.GetInfo();
            var cpuVal = cpuDiag.ValidateCpu(report.CpuDetails);
            report.CpuTest = new TestResult { Tested = true, Passed = cpuVal.IsHealthy, Message = cpuVal.Message };

            var ramDiag = new LinuxRamDiagnostic();
            report.RamDetails = ramDiag.GetInfo();
            var ramVal = ramDiag.ValidateRam(report.RamDetails);
            report.RamTest = new TestResult { Tested = true, Passed = ramVal.IsHealthy, Message = ramVal.Message };

            var storageDiag = new LinuxStorageDiagnostic();
            report.StorageDetails = storageDiag.GetInfo();
            var storVal = storageDiag.ValidateStorage(report.StorageDetails);
            report.StorageTest = new TestResult { Tested = true, Passed = storVal.IsHealthy, Message = storVal.Message };

            var battDiag = new LinuxBatteryDiagnostic();
            report.BatteryDetails = battDiag.GetInfo();
            var batVal = battDiag.ValidateBattery(report.BatteryDetails);
            report.BatteryTest = new TestResult { Tested = true, Passed = batVal.IsHealthy, Message = batVal.Message };

            var smartSvc = new LinuxSmartTestService(targets: report.StorageDetails!.Devices.Select(d => d.DeviceId));
            report.StorageTest = LinuxStorageChecks.ApplyHealth(report.StorageDetails, smartSvc.QuickHealthCheck(), smartSvc.Errors);
            var grading = new GradingService();
            var components = new[] { "CPU", "RAM", "Storage", "Battery", "SMART" };
            var componentGrades = grading.GradeComponentTestsOnly(report, components);

            if (componentGrades.Count == 0)
                return;

            var submission = new MachineHistorySubmissionService();
            var submitResult = await submission.SubmitComponentGradesAsync(
                report,
                componentGrades,
                "auto_basic_qc",
                activation.Token);

            if (!submitResult.Success && submitResult.IsAuthError)
            {
                Environment.ExitCode = 3;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Auto QC failed: {ex.Message}");
        }
    }

    static void InstallBackgroundServices()
    {
        if (Environment.UserName != "root")
        {
            Console.WriteLine("Error: --install-background must be run as root (using sudo).");
            Environment.Exit(1);
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "/usr/bin/pramaan";
        
        // 1. Heartbeat (Every 4 hours)
        string heartbeatService = $@"[Unit]
Description=Pramaan Heartbeat Service
After=network.target

[Service]
Type=oneshot
ExecStart={exePath} --heartbeat
User=root
";
        string heartbeatTimer = $@"[Unit]
Description=Run Pramaan Heartbeat every 4 hours

[Timer]
OnBootSec=15min
OnUnitActiveSec=4h
AccuracySec=5m

[Install]
WantedBy=timers.target
";

        // 2. Auto QC (Weekly)
        string autoQcService = $@"[Unit]
Description=Pramaan Auto Basic QC Service
After=network.target

[Service]
Type=oneshot
ExecStart={exePath} --auto-basic-qc
User=root
";
        string autoQcTimer = $@"[Unit]
Description=Run Pramaan Auto QC weekly

[Timer]
OnCalendar=weekly
Persistent=true
AccuracySec=12h

[Install]
WantedBy=timers.target
";

        try
        {
            File.WriteAllText("/etc/systemd/system/pramaan-heartbeat.service", heartbeatService);
            File.WriteAllText("/etc/systemd/system/pramaan-heartbeat.timer", heartbeatTimer);
            File.WriteAllText("/etc/systemd/system/pramaan-autoqc.service", autoQcService);
            File.WriteAllText("/etc/systemd/system/pramaan-autoqc.timer", autoQcTimer);

            LinuxCommandRunner.TryRun("systemctl", "daemon-reload");
            LinuxCommandRunner.TryRun("systemctl", "enable --now pramaan-heartbeat.timer");
            LinuxCommandRunner.TryRun("systemctl", "enable --now pramaan-autoqc.timer");

            Console.WriteLine("Successfully installed and enabled systemd timers for background Auto QC and Heartbeat.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to install services: {ex.Message}");
        }
    }

    static async Task RunUsbTestAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold cyan]═══ USB Port Testing ═══[/]\n");

        var usbDiag = new LinuxUsbPortDiagnostic();
        
        var (healthy, message) = usbDiag.QuickValidation();
        AnsiConsole.MarkupLine($"Quick Check: {(healthy ? "[green]✓[/]" : "[red]✗[/]")} {message.EscapeMarkup()}");
        AnsiConsole.MarkupLine("");

        if (!AnsiConsole.Confirm("Run interactive USB port test?", true))
        {
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Prepare a USB flash drive or device for testing.[/]\n");

        var result = await usbDiag.RunInteractiveTestAsync(msg =>
        {
            AnsiConsole.MarkupLine($"[grey]{msg.EscapeMarkup()}[/]");
        });

        AnsiConsole.MarkupLine("\n[bold cyan]═══ Test Results ═══[/]\n");
        
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua);
        table.AddColumn("Port");
        table.AddColumn("Type");
        table.AddColumn("Status");
        table.AddColumn("Device Detected");

        foreach (var port in result.TestedPorts)
        {
            table.AddRow(
                port.PortName,
                port.PortType,
                port.Passed ? "[green]✓ PASS[/]" : "[red]✗ FAIL[/]",
                port.DeviceDetected.EscapeMarkup()
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold]Summary:[/] {result.Summary}");
        AnsiConsole.MarkupLine($"Overall: {(result.AllPortsWorking ? "[green]All ports working[/]" : $"[yellow]{result.WorkingPortsCount}/{result.TestedPorts.Count} ports working[/]")}");
    }

    static async Task RunEthernetTestAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold cyan]═══ Ethernet Testing ═══[/]\n");

        var ethDiag = new LinuxEthernetDiagnostic();

        var (healthy, message) = ethDiag.QuickValidation();
        AnsiConsole.MarkupLine($"Quick Check: {(healthy ? "[green]✓[/]" : "[red]✗[/]")} {message.EscapeMarkup()}");
        AnsiConsole.MarkupLine("");

        if (!AnsiConsole.Confirm("Run interactive Ethernet port test?", true))
        {
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Prepare a live Ethernet cable for testing.[/]\n");

        var result = await ethDiag.RunInteractiveTestAsync(msg =>
        {
            AnsiConsole.MarkupLine($"[grey]{msg.EscapeMarkup()}[/]");
        }, timeoutSeconds: 30);

        DisplayEthernetResults(result);
    }

    static void DisplayEthernetResults(LinuxEthernetDiagnostic.EthernetTestResult result)
    {
        AnsiConsole.MarkupLine("\n[bold cyan]═══ Test Results ═══[/]\n");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua);
        table.AddColumn("Port");
        table.AddColumn("Status");
        table.AddColumn("Link Speed");
        table.AddColumn("IP Address");

        foreach (var port in result.TestedPorts)
        {
            table.AddRow(
                port.PortName,
                port.Passed ? "[green]✓ PASS[/]" : "[red]✗ FAIL[/]",
                string.IsNullOrEmpty(port.LinkSpeed) ? "[grey]—[/]" : port.LinkSpeed,
                string.IsNullOrEmpty(port.IpAddress) ? "[grey]No IP[/]" : port.IpAddress
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold]Summary:[/] {result.InteractiveSummary}");
        AnsiConsole.MarkupLine($"Overall: {(result.AllPortsWorking ? "[green]All ports working[/]" : $"[yellow]{result.WorkingPortsCount}/{result.TestedPorts.Count} ports working[/]")}");
    }
}
