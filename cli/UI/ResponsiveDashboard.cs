using Spectre.Console;
using Spectre.Console.Rendering;
using LaptopQC.Core.Models;

namespace Pramaan.CLI.UI;

/// <summary>Reflows using character-cell constraints rather than screen presets.</summary>
public static class DashboardRenderer
{
    public static readonly string[] Views = { "Summary", "Details", "System", "Reports", "Messages" };

    public static Layout Build(DashboardState s, int? columns = null, int? rows = null)
    {
        int width = Math.Max(1, columns ?? AnsiConsole.Profile.Width);
        int height = Math.Max(1, rows ?? AnsiConsole.Profile.Height);
        if (width < 60 || height < 20)
            return new Layout("Resize").Update(new Text("Pramaan: resize to at least 60 columns x 20 rows. Q/Esc exits when idle."));
        bool sideBySide = width >= 34 + 30 + 1;
        int menuRows = Math.Min(DashboardState.MenuItems.Length, Math.Max(3, height - 26));
        int topHeight = sideBySide ? menuRows + 2 : 10;
        int contentHeight = height - topHeight - 6;
        var root = new Layout("Root").SplitRows(new Layout("Header").Size(3),
            new Layout("Top").Size(topHeight), new Layout("Content"), new Layout("Footer").Size(3));
        var tabs = string.Join("  ", Views.Select((name, i) => i == s.SelectedView
            ? $"[bold black on fuchsia] {name} [/]" : $"[grey]{name}[/]"));
        root["Header"].Update(new Rows(
            new Markup("[bold white]PRAMAAN[/]  [bold fuchsia]Trust. Verify. Certify.[/]  [grey]|  LIVE QC[/]"),
            new Text(Fit($"{s.SystemInfo?.Model ?? "Device overview"}  |  {s.StatusMessage}", width), new Style(Color.White)),
            new Markup(tabs)));
        if (sideBySide)
        {
            int remaining = width - 65;
            bool system = remaining >= 31;
            if (system) remaining -= 31;
            bool logo = remaining >= 39;
            if (logo) remaining -= 39;
            var panels = new List<Layout>();
            if (logo) { panels.Add(new Layout("Brand").Size(38)); panels.Add(new Layout("BrandGap").Size(1).Update(new Text(""))); }
            if (system) { panels.Add(new Layout("System").Size(30)); panels.Add(new Layout("SystemGap").Size(1).Update(new Text(""))); }
            panels.Add(new Layout("Actions").Size(34));
            panels.Add(new Layout("ActionGap").Size(1).Update(new Text("")));
            panels.Add(new Layout("Progress"));
            root["Top"].SplitColumns(panels.ToArray());
            root["Actions"].Update(Menu(s, menuRows));
            root["Progress"].Update(Progress(s, 30 + remaining, topHeight - 2));
            if (system) root["System"].Update(SystemPanel(s, topHeight - 2));
            if (logo) root["Brand"].Update(Brand());
        }
        else
        {
            root["Top"].SplitRows(new Layout("Actions").Size(5), new Layout("Progress").Size(5));
            root["Actions"].Update(Menu(s, 3));
            root["Progress"].Update(Progress(s, width, 3));
        }
        var view = Views[s.SelectedView];
        root["Content"].Update(view == "Summary" ? Summary(s, width, contentHeight)
            : Paged(s, view, ContentLines(s, view), width, contentHeight));
        string status = string.IsNullOrEmpty(s.FooterMessage) ? "Ready. Full QC creates a saved report." : s.FooterMessage;
        var statusColor = status.Contains("failed", StringComparison.OrdinalIgnoreCase) ? Color.Red
            : status.Contains("Submitted", StringComparison.OrdinalIgnoreCase) ? Color.Green : Color.White;
        root["Footer"].Update(new Rows(new Text(Fit(status, width), new Style(statusColor)),
            new Text(Fit(s.StorageMessage, width), new Style(s.Store.IsPersistent ? Color.Green : Color.Yellow)),
            new Markup("[bold white]Q/Esc[/][grey] exit | [/][bold white]Tab[/][grey] views | [/][bold white]PgUp/Dn[/][grey] pages | [/][bold white]Arrows/Enter[/][grey] actions[/]")));
        return root;
    }

    static Panel Box(IRenderable content, string title) => new Panel(content)
        .Header($"[bold white]{title}[/]")
        .Border(AnsiConsole.Profile.Capabilities.Unicode ? BoxBorder.Rounded : BoxBorder.Ascii)
        .BorderColor(Color.Grey).Expand();

    static IRenderable Brand() => new Rows(new Text(@" ___ ___  _   __  __  _   _   _  _
| _ \ _ \/_\ |  \/  |/_\ /_\ | \| |
|  _/   / _ \| |\/| / _ \ _ \| .` |
|_| |_|_\/ \_\_|  |/_/ \_/ \_\_|\_|", new Style(Color.Fuchsia, decoration: Decoration.Bold)),
        new Markup("[white] Trust. Verify.[/] [bold fuchsia]Certify.[/]"),
        new Text(" Hardware quality certification", new Style(Color.Grey)));

    static IRenderable SystemPanel(DashboardState s, int availableRows) => Box(new Text(string.Join("\n", new[]
    {
        $"Model  {s.SystemInfo?.Model ?? "-"}", $"Serial {s.SystemInfo?.SerialNumber ?? "-"}",
        $"Device {s.DeviceId}", $"OS     {s.SystemInfo?.OsVersion ?? "-"}",
        $"Score  {(s.Report == null ? "Not scanned" : s.OverallScore + "/100")}", $"Grade  {s.GradeLabel}", $"Time   {s.Elapsed}"
    }.Take(availableRows).Select(line => Fit(line, 26)))), "SYSTEM INFORMATION");
    static IRenderable Menu(DashboardState s, int count)
    {
        int start = Math.Clamp(s.SelectedMenuIndex - count / 2, 0, Math.Max(0, DashboardState.MenuItems.Length - count));
        return Box(new Rows(Enumerable.Range(start, count).Select(i => new Text(
            (i == s.SelectedMenuIndex ? "> " : "  ") + DashboardState.MenuItems[i],
            i == s.SelectedMenuIndex ? new Style(Color.Black, Color.Fuchsia, Decoration.Bold) : new Style(Color.White)))), "QUICK ACTIONS");
    }
    static IRenderable Progress(DashboardState s, int width, int availableRows)
    {
        var values = new[] { ("CPU", s.ProgressCpu), ("RAM", s.ProgressRam), ("Storage", s.ProgressStorage), ("GPU", s.ProgressGpu), ("Manual", s.ProgressComp) };
        if (availableRows < 5)
            return Box(new Markup($"[white]CPU {s.ProgressCpu}%   RAM {s.ProgressRam}%[/]\n[white]Storage {s.ProgressStorage}%   GPU {s.ProgressGpu}%[/]\n[grey]Manual {s.ProgressComp}% | {s.Elapsed}[/]"), "TEST PROGRESS");
        int length = Math.Max(1, width - 21);
        return Box(new Markup(string.Join("\n", values.Select(v =>
        {
            int pct = Math.Clamp(v.Item2, 0, 100), fill = length * pct / 100;
            return $"[white]{v.Item1,-7}[/] [fuchsia]{new string('=', fill)}[/][grey]{new string('-', length - fill)}[/] [white]{pct,3}%[/]";
        }))), "TEST PROGRESS");
    }
    static IRenderable Summary(DashboardState s, int width, int height)
    {
        var report = s.Report ?? new QCReport();
        var tests = Tests(report);
        bool graded = s.GradeLabel != "N/A";
        bool scanned = tests.Any(t => t.Result.Tested);
        var cards = new List<(string Title, string Status, string Value, string Detail, Color Color)>
        {
            ("OVERALL", graded ? s.FailCount > 0 ? "NEEDS ATTENTION" : s.GradeLabel : s.IsTesting ? "TESTING" : scanned ? "DIAGNOSTICS" : "READY TO SCAN", graded ? $"{s.OverallScore} / 100" : s.IsTesting ? "Scanning..." : scanned ? "Not graded yet" : "No scan yet", graded ? $"{s.GradeLabel} | {s.FailCount} fail" : $"{s.FailCount} fail | {s.WarningCount} warn", !scanned ? Color.Grey : s.FailCount > 0 ? Color.Red : Color.Green)
        };
        var primary = report.StorageDetails?.Devices.Where(d => d.SizeGB > 1).OrderByDescending(d => d.SizeGB).FirstOrDefault();
        var module = report.RamDetails?.Modules.FirstOrDefault();
        void Card(string title, TestResult result, string? value, string? detail) => cards.Add((title, Status(result), value ?? "Not scanned", detail ?? "Run Full QC", StatusColor(result)));
        Card("CPU", report.CpuTest, report.CpuDetails?.Name, report.CpuDetails == null ? null : $"{report.CpuDetails.Cores} cores / {report.CpuDetails.Threads} threads");
        Card("MEMORY", report.RamTest, report.RamDetails == null ? null : $"{report.RamDetails.TotalCapacityGB} GB {module?.MemoryType}", module == null ? null : $"{module.SpeedMHz} MHz | {report.RamDetails!.Modules.Count} slots");
        Card("STORAGE", report.StorageTest, primary == null ? null : $"{primary.SizeGB:F0} GB {(primary.IsSsd ? "SSD" : "HDD")}", primary == null ? null : $"Health {primary.HealthPercent?.ToString() ?? "N/A"}%");
        Card("BATTERY", report.BatteryTest, report.BatteryDetails == null ? null : report.BatteryDetails.IsPresent ? $"Health {report.BatteryDetails.HealthPercent?.ToString() ?? "N/A"}%" : "No battery", report.BatteryDetails == null ? null : $"Charge {report.BatteryDetails.EstimatedChargeRemaining}%");
        Card("GPU", report.GpuTest, report.GpuTest.Tested ? report.GpuTest.Message : null, "See component details");
        cards.Add(("HARDWARE", tests.Skip(5).Any(t => t.Result.Tested) ? "MANUAL TESTS" : "NOT TESTED", $"{tests.Skip(5).Count(t => t.Result.Tested && t.Result.Passed)} passed", "Keyboard / USB / AV", Color.Grey));
        cards.Add(("ALERTS", !scanned ? "NOT SCANNED" : s.FailCount > 0 ? "ATTENTION" : "CLEAR", $"{s.FailCount} failures", $"{s.WarningCount} warnings", !scanned ? Color.Grey : s.FailCount > 0 ? Color.Red : Color.Green));
        if (height < 7)
            return Paged(s, "Health summary", cards.Select(c => $"{c.Title}: {c.Status} | {c.Value} | {c.Detail}"), width, height);
        int count = Math.Max(1, (width - 4) / 22), cardWidth = Math.Max(1, (width - 4) / count - 5);
        int rowCount = Math.Max(1, (height - 2) / 5), perPage = count * rowCount;
        int pages = (cards.Count + perPage - 1) / perPage;
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        var grid = new Grid().Expand();
        for (int i = 0; i < count; i++) grid.AddColumn(new GridColumn().Width((width - 4) / count - 1).PadLeft(0).PadRight(1));
        var visible = cards.Skip(s.Page * perPage).Take(perPage).ToArray();
        for (int offset = 0; offset < visible.Length; offset += count)
        {
            var cells = new List<IRenderable>();
            for (int i = 0; i < count; i++)
            {
                if (offset + i >= visible.Length) { cells.Add(new Text("")); continue; }
                var card = visible[offset + i];
                var panel = Box(new Rows(new Text(Fit(card.Status, cardWidth), new Style(card.Color, decoration: Decoration.Bold)),
                    new Text(Fit(card.Value, cardWidth), new Style(Color.White, decoration: Decoration.Bold)),
                    new Text(Fit(card.Detail, cardWidth), new Style(Color.Grey))), card.Title);
                panel.Height = 5;
                cells.Add(panel);
            }
            grid.AddRow(cells.ToArray());
        }
        int cardHeight = (visible.Length + count - 1) / count * 5 + 2;
        int spare = height - cardHeight;
        var health = Box(grid, $"HEALTH SUMMARY {s.Page + 1}/{pages}");
        health.Height = cardHeight;
        var sections = new List<IRenderable> { health };
        if (spare >= 12)
        {
            var components = (Panel)ComponentTable(s, width); components.Height = 12;
            sections.Add(components); spare -= 12;
        }
        if (spare >= 5)
        {
            var recent = (Panel)RecentReports(s); recent.Height = Math.Min(spare, 7);
            sections.Add(recent);
        }
        return new Rows(sections);
    }

    static Color StatusColor(TestResult result) => !result.Tested ? Color.Grey : result.Passed ? Color.Green : Color.Red;
    static IRenderable ComponentTable(DashboardState s, int width)
    {
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey).Expand();
        table.AddColumn(new TableColumn("[bold fuchsia]Component[/]").Width(11));
        table.AddColumn(new TableColumn("[bold fuchsia]Status[/]").Width(10));
        table.AddColumn("[bold fuchsia]Details[/]");
        table.AddColumn(new TableColumn("[bold fuchsia]Score[/]").Width(7).RightAligned());
        var report = s.Report ?? new QCReport();
        foreach (var (name, result) in Tests(report).Take(7))
            table.AddRow(new Text(name, new Style(Color.White)), new Text(Status(result), new Style(StatusColor(result))),
                new Text(Fit(ComponentDetail(report, name, result), Math.Max(1, width - 48)), new Style(Color.White)),
                new Text(result.Tested && s.GradeLabel != "N/A" ? $"{result.Score}/100" : "-", new Style(Color.White)));
        return Box(table, "COMPONENT HEALTH");
    }
    static string ComponentDetail(QCReport report, string name, TestResult result) => name switch
    {
        "CPU" when report.CpuDetails != null => $"{report.CpuDetails.Name} | {report.CpuDetails.Cores}C / {report.CpuDetails.Threads}T",
        "RAM" when report.RamDetails != null => $"{report.RamDetails.TotalCapacityGB} GB | {report.RamDetails.Modules.FirstOrDefault()?.MemoryType} | {report.RamDetails.Modules.Count} modules",
        "Storage" when report.StorageDetails != null => string.Join(" | ", report.StorageDetails.Devices.Take(2).Select(d => $"{d.Model} {d.SizeGB:F0} GB {(d.IsSsd ? "SSD" : "HDD")}")),
        "Battery" when report.BatteryDetails != null => $"Health {report.BatteryDetails.HealthPercent}% | Charge {report.BatteryDetails.EstimatedChargeRemaining}%",
        _ => result.Message
    };
    static IRenderable RecentReports(DashboardState s)
    {
        var table = new Table().Border(TableBorder.None).Expand();
        table.AddColumn("[grey]Report ID[/]"); table.AddColumn("[grey]Date & time[/]"); table.AddColumn("[grey]Status[/]");
        foreach (var report in s.SavedReports.Take(3))
            table.AddRow(new Text(report.HealthId.Truncate(12)), new Text(report.SavedAtUtc.ToLocalTime().ToString("MMM d HH:mm")),
                new Text(report.Status, new Style(report.Status == "Submitted" ? Color.Green : Color.Yellow)));
        if (s.SavedReports.Count == 0) table.AddRow("No reports yet", "Run Full QC", "Ready");
        return Box(table, "RECENT REPORTS");
    }
    static IRenderable Paged(DashboardState s, string title, IEnumerable<string> source, int width, int height)
    {
        var lines = source.SelectMany(line => Wrap(line, width - 4)).ToArray();
        int size = Math.Max(1, height - 2), pages = Math.Max(1, (lines.Length + size - 1) / size);
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        return Box(new Text(string.Join("\n", lines.Skip(s.Page * size).Take(size))), $"{title.ToUpperInvariant()} {s.Page + 1}/{pages}");
    }
    static IEnumerable<string> ContentLines(DashboardState s, string view)
    {
        if (view == "Details")
        {
            if (s.Report == null) { yield return "No scan data. Run diagnostics or Full QC."; yield break; }
            yield return $"CPU: {s.Report.CpuDetails?.Name}";
            yield return $"Memory: {s.Report.RamDetails?.TotalCapacityGB} GB";
            if (s.Report.StorageDetails != null)
                foreach (var device in s.Report.StorageDetails.Devices.ToArray())
                    yield return $"Drive: {device.Model} | {device.SizeGB} GB | Health {device.HealthPercent}%";
            if (s.Report.BatteryDetails != null)
                yield return $"Battery: charge {s.Report.BatteryDetails.EstimatedChargeRemaining}% | Health {s.Report.BatteryDetails.HealthPercent}%";
            foreach (var (name, result) in Tests(s.Report))
            {
                yield return $"{name}: {Status(result)} | Score {result.Score}/100";
                yield return result.Message;
                // Diagnostics append details on a worker. Capture the existing prefix;
                // don't enumerate a List while it is being extended.
                var details = result.Details;
                int count = details.Count;
                for (int i = 0; i < count; i++) yield return details[i];
                yield return "";
            }
        }
        else if (view == "System")
        {
            yield return $"Computer: {s.SystemInfo?.ComputerName}";
            yield return $"Model: {s.SystemInfo?.Manufacturer} {s.SystemInfo?.Model}";
            yield return $"Serial: {s.SystemInfo?.SerialNumber}";
            yield return $"OS: {s.SystemInfo?.OsVersion}";
            yield return $"Device ID: {s.DeviceId}";
            yield return $"Health ID: {s.HealthId}";
            yield return $"CPU: {s.Report?.CpuDetails?.Name}";
            yield return $"Memory: {s.Report?.RamDetails?.TotalCapacityGB} GB";
        }
        else if (view == "Reports")
        {
            yield return "Choose Saved reports / Retry from Actions to upload a pending report.";
            foreach (var report in s.SavedReports.ToArray())
            {
                yield return $"{report.SavedAtUtc:u} | {report.Status}";
                yield return $"Report: {report.ReportId} | Machine: {report.MachineId}";
                if (report.LastError != null) yield return report.LastError;
                yield return "";
            }
            if (s.SavedReports.Count == 0) yield return "No saved reports yet.";
        }
        else
        {
            yield return s.FooterMessage;
            yield return s.StorageMessage;
            foreach (var error in s.Store.LoadErrors) yield return error;
        }
    }
    public static (string Name, TestResult Result)[] Tests(QCReport? r) => r == null ? Array.Empty<(string, TestResult)>() : new[]
    {
        ("CPU", r.CpuTest), ("RAM", r.RamTest), ("Storage", r.StorageTest), ("Battery", r.BatteryTest),
        ("GPU", r.GpuTest), ("Network", r.NetworkTest), ("Keyboard", r.KeyboardTest), ("Trackpad", r.TrackpadTest),
        ("Display", r.DisplayTest), ("USB", r.UsbTest), ("Audio/Video", r.AudioVideoTest), ("Audio jack", r.AudioJackTest)
    };
    static string Status(TestResult r) => !r.Tested ? "Not tested" : r.Passed ? "PASS" : "FAIL";
    static string Clean(string? value) => string.Concat((value ?? "").Select(c => c == '\n' ? '\n' : c >= ' ' && c <= '~' ? c : ' '));
    static string Fit(string? value, int width)
    {
        var text = Clean(value).Replace('\n', ' ');
        return text.Length <= width ? text : text[..Math.Max(0, width - 3)] + "...";
    }
    static IEnumerable<string> Wrap(string? value, int width)
    {
        width = Math.Max(1, width);
        foreach (var original in Clean(value).Split('\n'))
        {
            var line = original;
            while (line.Length > width)
            {
                int split = line.LastIndexOf(' ', width - 1, width);
                if (split <= 0) split = width;
                yield return line[..split];
                line = line[split..].TrimStart();
            }
            yield return line;
        }
    }
}

public static class StringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "...";
}
