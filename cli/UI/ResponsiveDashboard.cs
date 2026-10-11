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
        bool sideBySide = width >= 36 + 32;
        int menuRows = Math.Min(DashboardState.MenuItems.Length, Math.Max(3, height - 25));
        int topHeight = sideBySide ? menuRows + 2 : 10;
        int contentHeight = height - topHeight - 5;
        var root = new Layout("Root").SplitRows(new Layout("Header").Size(2),
            new Layout("Top").Size(topHeight), new Layout("Content"), new Layout("Footer").Size(3));
        root["Header"].Update(new Text(Fit($"PRAMAAN | {s.OverallScore}/100 {s.GradeLabel} | Fails {s.FailCount} | {s.Elapsed}", width)
            + "\n" + Fit(s.StatusMessage, width), new Style(Color.Purple)));
        if (sideBySide)
        {
            int menuWidth = Math.Max(36, width / 3);
            root["Top"].SplitColumns(new Layout("Actions").Size(menuWidth), new Layout("Progress"));
            root["Actions"].Update(Menu(s, menuRows));
            root["Progress"].Update(Progress(s, width - menuWidth, topHeight - 2));
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
        root["Footer"].Update(new Text(Fit(string.IsNullOrEmpty(s.FooterMessage) ? "Ready. Full QC creates a saved report." : s.FooterMessage, width)
            + "\n" + Fit(s.StorageMessage, width) + "\n"
            + Fit($"Q/Esc: exit idle | Tab: {view} | PgUp/PgDn | Up/Down Enter", width), new Style(Color.Yellow)));
        return root;
    }

    static Panel Box(IRenderable content, string title) => new Panel(content).Header(title).Border(BoxBorder.Ascii).BorderColor(Color.Purple).Expand();
    static IRenderable Menu(DashboardState s, int count)
    {
        int start = Math.Clamp(s.SelectedMenuIndex - count / 2, 0, Math.Max(0, DashboardState.MenuItems.Length - count));
        return Box(new Rows(Enumerable.Range(start, count).Select(i => new Text(
            (i == s.SelectedMenuIndex ? "> " : "  ") + DashboardState.MenuItems[i],
            i == s.SelectedMenuIndex ? new Style(Color.Black, Color.Purple) : new Style(Color.White)))), "ACTIONS");
    }
    static IRenderable Progress(DashboardState s, int width, int availableRows)
    {
        var values = new[] { ("CPU", s.ProgressCpu), ("RAM", s.ProgressRam), ("Storage", s.ProgressStorage), ("GPU", s.ProgressGpu), ("Manual", s.ProgressComp) };
        if (availableRows < 5)
            return Box(new Text($"CPU {s.ProgressCpu}%  RAM {s.ProgressRam}%\nStorage {s.ProgressStorage}%  GPU {s.ProgressGpu}%\nManual {s.ProgressComp}% | {s.Elapsed}"), "PROGRESS");
        int length = Math.Max(1, width - 21);
        return Box(new Text(string.Join("\n", values.Select(v =>
        {
            int pct = Math.Clamp(v.Item2, 0, 100), fill = length * pct / 100;
            return $"{v.Item1,-7} {new string('#', fill)}{new string('-', length - fill)} {pct,3}%";
        }))), "PROGRESS");
    }
    static IRenderable Summary(DashboardState s, int width, int height)
    {
        var tests = Tests(s.Report);
        var cards = new List<(string, string)> { ("OVERALL", $"{s.OverallScore}/100 {s.GradeLabel}\n{s.FailCount} failures") };
        foreach (var (name, result) in tests.Take(5)) cards.Add((name, $"{Status(result)} | {result.Score}/100\n{result.Message}"));
        cards.Add(("HARDWARE", $"{tests.Skip(5).Count(t => t.Result.Tested && t.Result.Passed)} passed\nSee Details for all tests"));
        if (height < 7)
            return Paged(s, "Summary", cards.Select(c => $"{c.Item1}: {c.Item2.Replace('\n', ' ')}"), width, height);
        int count = Math.Max(1, (width - 4) / 24), cardWidth = Math.Max(1, (width - 4) / count - 4);
        int rowCount = Math.Max(1, (height - 3) / 5), perPage = count * rowCount;
        int pages = (cards.Count + perPage - 1) / perPage;
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        var grid = new Grid().Expand();
        for (int i = 0; i < count; i++) grid.AddColumn(new GridColumn().PadRight(1));
        var visible = cards.Skip(s.Page * perPage).Take(perPage).ToArray();
        for (int offset = 0; offset < visible.Length; offset += count)
        {
            var cells = new List<IRenderable>();
            for (int i = 0; i < count; i++)
            {
                if (offset + i >= visible.Length) { cells.Add(new Text("")); continue; }
                var card = visible[offset + i];
                var panel = Box(new Text(string.Join("\n", Wrap(card.Item2, cardWidth).Take(3))), card.Item1);
                panel.Height = 5;
                cells.Add(panel);
            }
            grid.AddRow(cells.ToArray());
        }
        int spare = height - 3 - Math.Min(rowCount, (visible.Length + count - 1) / count) * 5;
        IRenderable body = spare >= 4 ? new Rows(grid, new Text(string.Join("\n", Wrap(
            $"Model: {s.SystemInfo?.Model}\nSerial: {s.SystemInfo?.SerialNumber}\nHealth ID: {s.HealthId}", width - 4).Take(spare)))) : grid;
        return Box(body, $"SUMMARY {s.Page + 1}/{pages}");
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
