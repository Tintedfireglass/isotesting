using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;
using LaptopQC.Core.Diagnostics;
using LaptopQC.Hardware.Models;
using Pramaan.CLI.UI;
using Spectre.Console;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var handler = new FakeHandler();
var service = new QCSubmissionService(new ApiConfiguration { ApiUrl = "https://test.invalid/api", ApiKey = "test-key" }, new HttpClient(handler));
var report = new QCReport
{
    PhysicalCondition = "B", ScratchesAndDents = "Minor",
    SystemInfo = new SystemInfo { SerialNumber = "TEST-1234", Model = new string('X', 220) + " [brackets]", ComputerName = "test" },
    CpuDetails = new CpuInfo { Name = "Processor [literal] " + new string('C', 200) },
    CpuTest = new TestResult { Tested = true, Passed = false, Message = "Long [literal] details " + new string('Z', 240) },
    OverallScore = 75, OverallGrade = "B"
};
report.CpuTest.Details.Add("Final detail marker: END-OF-DETAILS");
string payload = service.PrepareReport(report);
using (var doc = JsonDocument.Parse(payload))
{
    Check(doc.RootElement.GetProperty("physicalCondition").GetString() == "B", "Physical condition missing");
    Check(!doc.RootElement.TryGetProperty("technicianId", out _), "Optional null technicianId sent");
}
foreach (string condition in new[] { "A+", "A", "B", "C", "D", "E" })
foreach (string scratches in new[] { "None", "Minor", "Major" })
{
    report.PhysicalCondition = condition; report.ScratchesAndDents = scratches;
    using var parsed = JsonDocument.Parse(service.PrepareReport(report));
    Check(parsed.RootElement.GetProperty("physicalCondition").GetString() == condition, "Condition mapping changed");
}
report.PhysicalCondition = ""; report.ScratchesAndDents = " ";
using (var parsed = JsonDocument.Parse(service.PrepareReport(report)))
{
    Check(!parsed.RootElement.TryGetProperty("physicalCondition", out _), "Empty condition was sent");
    Check(!parsed.RootElement.TryGetProperty("scratchesAndDents", out _), "Empty scratches was sent");
}
report.PhysicalCondition = "bad";
try { service.PrepareReport(report); throw new Exception("Invalid condition accepted"); }
catch (ArgumentException) { }
report.PhysicalCondition = "B"; report.ScratchesAndDents = "None";

string root = Path.Combine(AppContext.BaseDirectory, "scratch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var store = new SavedReportStore(Path.Combine(root, "reports"));
var saved = SavedReportStore.Create(payload);
store.Save(saved);
if (!OperatingSystem.IsWindows())
{
    var mode = File.GetUnixFileMode(Path.Combine(store.DirectoryPath, saved.ReportId + ".json"));
    Check(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Report file grants access beyond its owner");
}
Check(new SavedReportStore(store.DirectoryPath).Load().Single().Payload == payload, "Payload changed on restart");
Check(!File.ReadAllText(Path.Combine(store.DirectoryPath, saved.ReportId + ".json")).Contains("test-token"), "Token stored");
foreach (var (code, expected) in new[] { (HttpStatusCode.BadRequest, "Pending"), (HttpStatusCode.Unauthorized, "Pending"),
    (HttpStatusCode.Forbidden, "Pending"), (HttpStatusCode.Conflict, "Verify duplicate"), (HttpStatusCode.Created, "Submitted") })
{
    handler.Code = code;
    var result = await store.SubmitAsync(saved, service, "test-token");
    Check(saved.Status == expected && handler.Payloads.Last() == payload, "Retry changed payload or status");
    Check(result.IsAuthError == (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), "Auth error classification failed");
    Check(handler.LastToken == "test-token", "Token not transmitted");
    Check(new SavedReportStore(store.DirectoryPath).Load().Single().Status == expected, "Outcome not persisted");
}
handler.ThrowTimeout = true;
await store.SubmitAsync(saved, service, "test-token");
Check(saved.Status == "Pending" && !string.IsNullOrEmpty(saved.LastError), "Timeout lost pending report");
handler.ThrowTimeout = false;
// An interrupted write must not hide or overwrite the committed payload.
File.WriteAllText(Path.Combine(store.DirectoryPath, "interrupted.tmp"), "partial");
File.WriteAllText(Path.Combine(store.DirectoryPath, "corrupt.json"), "{");
Check(store.Load().Count == 1 && store.LoadErrors.Count == 1, "Corrupt or partial file handling failed");
string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "blocked");
int sends = handler.Payloads.Count;
try { await new SavedReportStore(blocked).SubmitAsync(saved, service, "test-token"); throw new Exception("Storage failure ignored"); }
catch (IOException) { }
Check(handler.Payloads.Count == sends, "Sent report before successful save");
// Simulate storage becoming full/unavailable after the server accepted a report.
string failingDirectory = Path.Combine(root, "after-send");
var afterSendStore = new SavedReportStore(failingDirectory);
handler.Code = HttpStatusCode.Created;
handler.AfterSend = () =>
{
    foreach (var file in Directory.GetFiles(failingDirectory)) File.Delete(file);
    Directory.Delete(failingDirectory);
    File.WriteAllText(failingDirectory, "unavailable");
};
var accepted = await afterSendStore.SubmitAsync(saved, service, "test-token");
Check(accepted.Success && accepted.StorageError != null && saved.Status == "Submitted", "Lost successful HTTP outcome after disk failure");
handler.AfterSend = null;

var state = new DashboardState { Report = report, SystemInfo = report.SystemInfo, FooterMessage = "Validation failed: " + new string('E', 350) + " ERROR-END" };
state.SavedReports.Add(saved);
string outputRoot = Path.Combine(AppContext.BaseDirectory, "snapshots"); Directory.CreateDirectory(outputRoot);
foreach (bool unicode in new[] { false, true })
foreach (var (width, height) in new[] { (80, 24), (100, 32), (140, 45), (160, 50), (60, 20), (60, 50), (160, 20), (40, 12) })
{
    for (int view = 0; view < DashboardRenderer.Views.Length; view++)
    {
        state.SelectedView = view; state.Page = 0;
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = width; console.Profile.Height = height;
        console.Profile.Capabilities.Unicode = unicode;
        AnsiConsole.Profile.Capabilities.Unicode = unicode;
        try { console.Write(DashboardRenderer.Build(state, width, height)); }
        catch (Exception ex) { throw new Exception($"Render failed: {width}x{height} {DashboardRenderer.Views[view]}", ex); }
        var rawRendered = writer.ToString();
        // Some Linux terminals retain SGR decorations even with colours disabled.
        // Escape sequences consume no character cells and must not count as width.
        var rendered = Regex.Replace(rawRendered, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Replace("\r", "");
        File.WriteAllText(Path.Combine(outputRoot, $"{width}x{height}-{view}-{(unicode ? "unicode" : "ascii")}.ansi"), rawRendered);
        var lines = rendered.TrimEnd('\n').Split('\n');
        File.WriteAllText(Path.Combine(outputRoot, $"{width}x{height}-{view}-{(unicode ? "unicode" : "ascii")}.txt"), rendered);
        Check(lines.Length <= height && lines.All(l => l.Length <= width), $"Overflow at {width}x{height} {DashboardRenderer.Views[view]}: {lines.Length} rows, max width {lines.Max(l => l.Length)}, line {JsonSerializer.Serialize(lines.FirstOrDefault(l => l.Length > width))}");
        if (view == 0 && width >= 140 && height >= 45)
            Check(rendered.Contains("HARDWARE") && rendered.Contains("GPU") && rendered.Contains("Keyboard"), "Overview rows were cropped");
        if (width >= 60 && height >= 20)
        {
            Check(rendered.Contains("ACTIONS") && rendered.Contains("PROGRESS"), "Essential panels missing");
            Check(rendered.Contains("Q/Esc"), "Footer cropped");
        }
        File.WriteAllText(Path.Combine(outputRoot, $"{width}x{height}-{view}.txt"), rendered);
    }
}
// Navigation and shrinking preserve report/selection; all long text is reachable.
state.SelectedMenuIndex = 7; state.SelectedView = 1; state.Page = 0;
var reached = new List<string>();
for (int i = 0; i < 30; i++)
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
    console.Profile.Width = 80; console.Profile.Height = 24;
    console.Write(DashboardRenderer.Build(state, 80, 24));
    reached.Add(writer.ToString()); DashboardDisplay.Navigate(ConsoleKey.PageDown, state);
}
Check(string.Join("", reached).Contains("END-OF-DETAILS"), "Full details unreachable");
Check(state.SelectedMenuIndex == 7 && ReferenceEquals(state.Report, report), "Resize reset state");
DashboardDisplay.Navigate(ConsoleKey.Tab, state);
Check(state.SelectedView == 2 && state.Page == 0, "Tab failed");
File.WriteAllText(Path.Combine(outputRoot, "payload.json"), payload);
// A styled preview from the production renderer, using clearly synthetic hardware.
var previewReport = new QCReport
{
    SystemInfo = new SystemInfo { Manufacturer = "ASUS", Model = "TUF Dash F15", SerialNumber = "PREVIEW-001", OsVersion = "Pramaan Live Linux" },
    CpuDetails = new CpuInfo { Name = "Intel Core i5-11300H", Cores = 4, Threads = 8, MaxClockSpeedMHz = 4400 },
    RamDetails = new RamInfo { TotalCapacityGB = 24, Modules = new() { new RamModule { MemoryType = "DDR4", SpeedMHz = 3200 }, new RamModule { MemoryType = "DDR4", SpeedMHz = 3200 } } },
    StorageDetails = new StorageInfo { Devices = new() { new StorageDevice { Model = "Samsung NVMe", SizeGB = 477, IsSsd = true, HealthPercent = 64 } } },
    BatteryDetails = new BatteryInfo { IsPresent = true, HealthPercent = 70, EstimatedChargeRemaining = 81 },
    OverallScore = 75, OverallGrade = "B"
};
foreach (var (_, result) in DashboardRenderer.Tests(previewReport))
{
    result.Tested = true; result.Passed = true; result.Score = 90; result.Grade = "A"; result.Message = "Verified";
}
previewReport.StorageTest.Passed = false; previewReport.StorageTest.Score = 35; previewReport.StorageTest.Grade = "F"; previewReport.StorageTest.Message = "SMART warning";
var preview = new DashboardState { SystemInfo = previewReport.SystemInfo, DeviceId = 1042, Elapsed = "00:03:24",
    ProgressCpu = 100, ProgressRam = 100, ProgressStorage = 100, ProgressGpu = 100, ProgressComp = 100,
    StatusMessage = "QC complete | 1 component needs attention", StorageMessage = "USB report storage: persistent",
    FooterMessage = "Report saved locally. Upload pending; open Saved reports / Retry." };
preview.UpdateFromReport(previewReport);
preview.SavedReports.Add(SavedReportStore.Create(service.PrepareReport(previewReport)));
preview.SavedReports.Add(saved);
var previousPersistence = Environment.GetEnvironmentVariable("PRAMAAN_REPORTS_PERSISTENT");
Environment.SetEnvironmentVariable("PRAMAAN_REPORTS_PERSISTENT", "1");
foreach (var (width, height) in new[] { (80, 24), (140, 45), (160, 50) })
{
    using var recorder = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }).CreateRecorder();
    recorder.Profile.Width = width; recorder.Profile.Height = height;
    preview.Page = 0;
    recorder.Write(DashboardRenderer.Build(preview, width, height));
    File.WriteAllText(Path.Combine(outputRoot, $"preview-{width}.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>Pramaan terminal preview</title><style>body{background:#000;margin:24px;color:#fff}pre{font:14px/1.3 Consolas,monospace;margin:0}span{font-style:normal!important}</style></head><body>" + recorder.ExportHtml() + "</body></html>");
}
Environment.SetEnvironmentVariable("PRAMAAN_REPORTS_PERSISTENT", previousPersistence);
Console.WriteLine("PASS: responsive render bounds, full details, navigation, condition mapping, frozen retries, HTTP failures, atomic recovery, corrupt files, and storage failures.");
Console.WriteLine($"Snapshots and contract fixture: {outputRoot}");

sealed class FakeHandler : HttpMessageHandler
{
    public HttpStatusCode Code = HttpStatusCode.Created;
    public bool ThrowTimeout;
    public Action? AfterSend;
    public List<string> Payloads = new();
    public string? LastToken;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        LastToken = request.Headers.Authorization?.Parameter;
        if (ThrowTimeout) throw new TaskCanceledException("Simulated timeout");
        AfterSend?.Invoke();
        return new HttpResponseMessage(Code) { Content = new StringContent(Code == HttpStatusCode.Created
            ? "{\"message\":\"ok\",\"id\":1}" : "{\"error\":\"Validation Error\",\"message\":\"test failure\"}") };
    }
}
