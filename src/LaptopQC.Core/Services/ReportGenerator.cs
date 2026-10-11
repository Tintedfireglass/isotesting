using LaptopQC.Core.Models;
using System.Text;
using QRCoder;

namespace LaptopQC.Core.Services;

public class ReportGenerator
{
    private readonly string _verifyBaseUrl;
    private readonly string _brandDisplayName;

    public ReportGenerator(string verifyBaseUrl = "https://pramaan-dashboard.gadgetguruz.com", string brandDisplayName = "PRAMAAN")
    {
        _verifyBaseUrl = verifyBaseUrl.TrimEnd('/');
        _brandDisplayName = brandDisplayName;
    }

    public string GenerateHtmlReport(QCReport report)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset='utf-8'>");
        sb.AppendLine("<meta http-equiv='Content-Type' content='text/html; charset=utf-8'>");
        sb.AppendLine($"<title>QC Certificate - {report.RefurbishId}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            @page {
                size: A4;
                margin: 0;
            }
            * {
                box-sizing: border-box;
            }
            body { 
                font-family: 'Segoe UI', -apple-system, sans-serif; 
                margin: 0 auto;
                padding: 32px;
                color: #1f2937; 
                background: white;
                max-width: 210mm;
                min-height: 100vh;
                box-shadow: 0 0 20px rgba(0,0,0,0.1);
            }
            .header { 
                border-bottom: 2px solid #1f2937; 
                padding-bottom: 16px; 
                margin-bottom: 24px; 
                display: flex;
                justify-content: space-between;
                align-items: flex-end;
            }
            .header h1 {
                font-size: 28px;
                font-weight: bold;
                text-transform: uppercase;
                letter-spacing: 2px;
                margin: 0;
            }
            .header .subtitle {
                font-size: 12px;
                color: #6b7280;
                margin-top: 4px;
            }
            .header-right {
                text-align: right;
            }
            .header-right .test-id {
                font-size: 16px;
                font-weight: bold;
            }
            .header-right .date {
                font-size: 12px;
                color: #6b7280;
            }
            .overall-status {
                background: #f9fafb;
                border: 1px solid #e5e7eb;
                border-radius: 4px;
                padding: 24px;
                margin-bottom: 32px;
                display: flex;
                justify-content: space-between;
                align-items: center;
            }
            .overall-label {
                font-size: 11px;
                text-transform: uppercase;
                letter-spacing: 1px;
                color: #6b7280;
                font-weight: 600;
            }
            .overall-grade {
                font-weight: bold;
                font-size: 48px;
                margin-top: 4px;
                line-height: 1;
            }
            .overall-sublabel {
                font-size: 14px;
                color: #374151;
                margin-top: 4px;
            }
            .grade-A { color: #15803d; }
            .grade-B { color: #0d9488; }
            .grade-C { color: #d97706; }
            .grade-D { color: #ea580c; }
            .grade-E { color: #dc2626; }
            .grade-F { color: #991b1b; }
            .grade-badge {
                padding: 3px 10px;
                border-radius: 4px;
                font-size: 12px;
                font-weight: bold;
                display: inline-block;
                min-width: 28px;
                text-align: center;
            }
            .badge-A { background: #dcfce7; color: #15803d; }
            .badge-B { background: #ccfbf1; color: #0d9488; }
            .badge-C { background: #fef3c7; color: #d97706; }
            .badge-D { background: #ffedd5; color: #ea580c; }
            .badge-E { background: #fee2e2; color: #dc2626; }
            .badge-F { background: #fecaca; color: #991b1b; }
            .badge-none { background: #f3f4f6; color: #6b7280; }
            .machine-id {
                text-align: right;
            }
            .machine-id .label {
                font-size: 11px;
                text-transform: uppercase;
                letter-spacing: 1px;
                color: #6b7280;
                font-weight: 600;
                margin-bottom: 4px;
            }
            .machine-id .value {
                font-family: monospace;
                font-size: 18px;
            }
            .info-grid { 
                display: grid; 
                grid-template-columns: 1fr 1fr; 
                gap: 48px; 
                margin-bottom: 32px; 
            }
            .section-title {
                font-size: 10px;
                text-transform: uppercase;
                letter-spacing: 2px;
                font-weight: bold;
                border-bottom: 1px solid #d1d5db;
                padding-bottom: 8px;
                margin-bottom: 12px;
            }
            .info-table {
                width: 100%;
                font-size: 13px;
            }
            .info-table tr {
                border-bottom: 1px dotted #d1d5db;
            }
            .info-table td {
                padding: 8px 0;
            }
            .info-table .label {
                color: #6b7280;
                width: 35%;
            }
            .info-table .value {
                font-weight: 500;
            }
            .info-table .mono {
                font-family: monospace;
            }
            .test-results-title {
                font-size: 10px;
                text-transform: uppercase;
                letter-spacing: 2px;
                font-weight: bold;
                border-bottom: 2px solid #1f2937;
                padding-bottom: 8px;
                margin-bottom: 16px;
            }
            .test-table {
                width: 100%;
                font-size: 13px;
                text-align: left;
                border-collapse: collapse;
            }
            .test-table thead tr {
                background: #f3f4f6;
                border-bottom: 1px solid #d1d5db;
            }
            .test-table th {
                padding: 8px 12px;
                font-weight: 600;
            }
            .test-table td {
                padding: 12px;
                border-bottom: 1px solid #e5e7eb;
            }
            .test-table .test-name {
                font-weight: 500;
            }
            .test-table .details {
                color: #6b7280;
            }
            .test-table .details-extra {
                font-size: 11px;
                color: #9ca3af;
            }
            .notes-section {
                background: #f9fafb;
                border: 1px solid #d1d5db;
                border-radius: 4px;
                padding: 16px;
                margin-top: 24px;
                page-break-inside: avoid;
            }
            .notes-section h3 {
                font-size: 11px;
                text-transform: uppercase;
                color: #6b7280;
                font-weight: bold;
                margin: 0 0 8px 0;
            }
            .notes-section p {
                font-size: 13px;
                font-style: italic;
                margin: 0;
            }
            .footer {
                margin-top: 48px;
                padding-top: 24px;
                border-top: 1px solid #d1d5db;
                font-size: 11px;
                color: #6b7280;
                display: flex;
                justify-content: space-between;
            }
            @media print {
                body {
                    background: white;
                    -webkit-print-color-adjust: exact;
                    print-color-adjust: exact;
                }
            }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        
        // Header
        sb.AppendLine("<div class='header'>");
        sb.AppendLine("<div>");
        sb.AppendLine("<h1>QC Certificate</h1>");
        sb.AppendLine("<div class='subtitle'>Quality Control Report</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='header-right'>");
        sb.AppendLine($"<div class='test-id'>ID: {report.RefurbishId}</div>");
        sb.AppendLine($"<div class='date'>{report.Timestamp:g}</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Overall Status - Grade Display
        var pramaanGrade = report.PramaanResult?.GradeBand ?? "N/A";
        var pramaanScore = report.PramaanResult?.OverallHealthScore ?? 0;
        var gradeLabel = report.PramaanResult?.GradeBand != null ? GradingService.GradeLabel(report.PramaanResult.GradeBand) : "Unknown";

        // QR Code Generation
        string qrImgTag = "";
        if (!string.IsNullOrEmpty(report.HealthId))
        {
            try
            {
                var verificationUrl = $"{_verifyBaseUrl}/verify/{report.HealthId}";
                using var qrGenerator = new QRCodeGenerator();
                using var qrCodeData = qrGenerator.CreateQrCode(verificationUrl, QRCodeGenerator.ECCLevel.M);
                using var qrCode = new PngByteQRCode(qrCodeData);
                byte[] qrBytes = qrCode.GetGraphic(5);
                string base64 = Convert.ToBase64String(qrBytes);
                qrImgTag = $"<div class='qr-code' style='background:white; padding:8px; border:1px solid #e5e7eb; border-radius:4px;'><img src='data:image/png;base64,{base64}' alt='Verification QR Code' width='80' height='80' /></div>";
            }
            catch { /* Ignore */ }
        }

        sb.AppendLine("<div class='overall-status'>");
        sb.AppendLine("<div>");
        sb.AppendLine($"<div class='overall-label'>{_brandDisplayName} Health Score</div>");
        sb.AppendLine($"<div class='overall-grade grade-{pramaanGrade}'>{pramaanGrade}</div>");
        sb.AppendLine($"<div class='overall-sublabel'>{gradeLabel} &mdash; {pramaanScore}/100</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div style='display:flex; align-items:center; gap:24px;'>");
        sb.AppendLine("<div class='machine-id'>");
        sb.AppendLine("<div class='label'>Machine ID</div>");
        sb.AppendLine($"<div class='value'>{(report.DeviceId > 0 ? report.DeviceId.ToString() : "N/A")}</div>");
        sb.AppendLine("</div>");
        if (!string.IsNullOrEmpty(qrImgTag))
        {
            sb.AppendLine(qrImgTag);
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // System Info Grid
        sb.AppendLine("<div class='info-grid'>");
        
        // Left Column - System Specification
        sb.AppendLine("<div>");
        sb.AppendLine("<div class='section-title'>System Specification</div>");
        sb.AppendLine("<table class='info-table'>");
        sb.AppendLine("<tbody>");
        if (report.SystemInfo != null)
        {
            sb.AppendLine($"<tr><td class='label'>Manufacturer</td><td class='value'>{report.SystemInfo.Manufacturer}</td></tr>");
            sb.AppendLine($"<tr><td class='label'>Model</td><td class='value'>{report.SystemInfo.Model}</td></tr>");
            sb.AppendLine($"<tr><td class='label'>Device ID</td><td class='value mono'>{(report.DeviceId > 0 ? report.DeviceId.ToString() : "N/A")}</td></tr>");
            sb.AppendLine($"<tr><td class='label'>MAC Address</td><td class='value mono'>{MaskString(report.MacAddress)}</td></tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        
        // Right Column - Hardware Details
        sb.AppendLine("<div>");
        sb.AppendLine("<div class='section-title'>Hardware Details</div>");
        sb.AppendLine("<table class='info-table'>");
        sb.AppendLine("<tbody>");
        if (report.CpuDetails != null)
            sb.AppendLine($"<tr><td class='label'>Processor</td><td class='value'>{report.CpuDetails.Name}</td></tr>");
        if (report.RamDetails != null)
            sb.AppendLine($"<tr><td class='label'>RAM</td><td class='value'>{report.RamDetails.TotalCapacityGB} GB</td></tr>");
        if (report.StorageDetails != null)
        {
            if (report.StorageDetails.IsTampered)
            {
                sb.AppendLine("<tr><td class='label'>Storage</td><td class='value'>Storage Tampered - Unable to read data</td></tr>");
            }
            else if (report.StorageDetails.RaidArrays.Count > 0)
            {
                // RAID machine: show array info instead of raw capacity or inconclusive
                var arr = report.StorageDetails.RaidArrays[0];
                string raidStatus = arr.IsHealthy ? "Healthy" : "Degraded";
                string sizeStr = arr.TotalSizeGB > 0
                    ? $" — {arr.TotalSizeGB:F0} GB"
                    : (report.StorageDetails.TotalCapacityGB > 0 ? $" — {report.StorageDetails.TotalCapacityGB:F0} GB" : "");
                sb.AppendLine($"<tr><td class='label'>Storage</td><td class='value'>RAID Array ({arr.Level}){sizeStr} [{raidStatus}]</td></tr>");
            }
            else if (report.StorageDetails.IsInconclusive)
            {
                sb.AppendLine("<tr><td class='label'>Storage</td><td class='value'>Storage Inconclusive - Unable to verify health data</td></tr>");
            }
            else if (report.StorageDetails.IsSuspicious)
            {
                sb.AppendLine("<tr><td class='label'>Storage</td><td class='value'>Storage data suspicious - Review recommended</td></tr>");
            }
            else
            {
                sb.AppendLine($"<tr><td class='label'>Storage</td><td class='value'>{report.StorageDetails.TotalCapacityGB:F0} GB</td></tr>");
            }
        }
        if (report.BatteryDetails != null && report.BatteryDetails.IsPresent)
        {
            if (report.BatteryDetails.IsTampered)
            {
                sb.AppendLine("<tr><td class='label'>Battery</td><td class='value'>Battery Tampered - Unable to read data</td></tr>");
            }
            else
            {
                var batteryBrand = FirstNonEmpty(
                    report.BatteryDetails.ManufactureName,
                    report.BatteryDetails.Name,
                    report.BatteryDetails.PartNumber);
                if (!string.IsNullOrWhiteSpace(batteryBrand))
                {
                    sb.AppendLine($"<tr><td class='label'>Battery Brand</td><td class='value'>{SafeHtmlEncode(batteryBrand)}</td></tr>");
                }
                sb.AppendLine($"<tr><td class='label'>Battery Health</td><td class='value'>Wear: {report.BatteryDetails.WearLevelPercent}%</td></tr>");
                var hasCycleCount = report.BatteryDetails.CycleCount.HasValue && report.BatteryDetails.CycleCount.Value > 0;
                if (!hasCycleCount)
                {
                    sb.AppendLine("<tr><td class='label'>Cycle Count</td><td class='value'>Not reported by firmware</td></tr>");
                }
                if (report.ChargerTest.Tested)
                {
                    sb.AppendLine($"<tr><td class='label'>Charger / AC</td><td class='value'>{(report.ChargerTest.Passed ? "Working / Connected" : "Failed")}</td></tr>");
                }
            }
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        
        sb.AppendLine("</div>");

        // Test Results Table
        sb.AppendLine("<div class='test-results-title'>Diagnostic Results</div>");
        sb.AppendLine("<table class='test-table'>");
        sb.AppendLine("<thead>");
        sb.AppendLine("<tr>");
        sb.AppendLine("<th style='width:22%'>Test Component</th>");
        sb.AppendLine("<th style='width:10%; text-align:center'>Grade</th>");
        sb.AppendLine("<th style='width:10%; text-align:center'>Score</th>");
        sb.AppendLine("<th>Notes / Details</th>");
        sb.AppendLine("</tr>");
        sb.AppendLine("</thead>");
        sb.AppendLine("<tbody>");
        
        AppendTestRow(sb, "CPU", report.CpuTest);
        AppendTestRow(sb, "RAM", report.RamTest);
        // Storage: expand into per-drive rows when multiple physical drives are present
        if (report.StorageDetails != null && report.StorageDetails.Devices.Count > 1)
        {
            for (int driveIdx = 0; driveIdx < report.StorageDetails.Devices.Count; driveIdx++)
            {
                var device = report.StorageDetails.Devices[driveIdx];
                var driveName = !string.IsNullOrWhiteSpace(device.Model) ? device.Model : $"Drive {driveIdx + 1}";
                var rowLabel = $"Storage {driveIdx + 1} — {driveName}";

                // Build a per-drive message
                var parts = new List<string>();
                var driveType = device.IsSsd ? "SSD" : "HDD";
                if (device.SizeGB > 0)
                    parts.Add($"{device.SizeGB:F0} GB {driveType}");
                if (device.HealthPercent.HasValue)
                    parts.Add($"{device.HealthPercent}% health");
                if (device.IsTampered)
                    parts.Add("Tampered");
                else if (device.IsInconclusive)
                    parts.Add("Inconclusive");
                else if (device.IsSuspicious)
                    parts.Add("Suspicious — review recommended");

                var driveResult = new TestResult
                {
                    Tested   = report.StorageTest.Tested,
                    Passed   = report.StorageTest.Passed && !device.IsTampered,
                    Grade    = report.StorageTest.Grade,
                    Score    = report.StorageTest.Score,
                    Message  = parts.Count > 0 ? string.Join(" — ", parts) : report.StorageTest.Message,
                };
                AppendTestRow(sb, rowLabel, driveResult);
            }
        }
        else
        {
            AppendTestRow(sb, "Storage", report.StorageTest);
        }

        // Battery: ensure Charger / Power test result is reflected in Details if tested
        if (report.ChargerTest.Tested)
        {
            string chgDetail = $"Charger / AC Power: {(report.ChargerTest.Passed ? "Passed" : "Failed")}{(string.IsNullOrWhiteSpace(report.ChargerTest.Message) ? "" : $" ({report.ChargerTest.Message})")}";
            if (!report.BatteryTest.Details.Any(d => d.StartsWith("Charger", StringComparison.OrdinalIgnoreCase)))
            {
                report.BatteryTest.Details.Add(chgDetail);
            }
        }
        AppendTestRow(sb, "Battery", report.BatteryTest);
        AppendTestRow(sb, "Keyboard", report.KeyboardTest);
        AppendTestRow(sb, "Trackpad", report.TrackpadTest);
        AppendTestRow(sb, "Display", report.DisplayTest);
        AppendTestRow(sb, "USB Ports", report.UsbTest);
        AppendTestRow(sb, "Audio / Video", report.AudioVideoTest);
        AppendTestRow(sb, "3.5mm Audio Jack", report.AudioJackTest);
        AppendTestRow(sb, "GPU Stress Test", report.GpuTest);

        // Network / WiFi: ensure Bluetooth test result is reflected in Details if tested
        if (report.BluetoothTest.Tested)
        {
            string btDetail = $"Bluetooth: {(report.BluetoothTest.Passed ? "Passed" : "Failed")}{(string.IsNullOrWhiteSpace(report.BluetoothTest.Message) ? "" : $" ({report.BluetoothTest.Message})")}";
            if (!report.NetworkTest.Details.Any(d => d.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase)))
            {
                report.NetworkTest.Details.Add(btDetail);
            }
        }
        AppendTestRow(sb, "Network / WiFi", report.NetworkTest);

        AppendDisplaySection(sb, report.DisplayTest);
        
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");

        // Technician Notes
        if (!string.IsNullOrWhiteSpace(report.TechnicianNotes))
        {
            sb.AppendLine("<div class='notes-section'>");
            sb.AppendLine("<h3>Technician Notes</h3>");
            sb.AppendLine($"<p>{SafeHtmlEncode(report.TechnicianNotes)}</p>");
            sb.AppendLine("</div>");
        }

        // Footer
        var appVersion = string.IsNullOrWhiteSpace(report.AppVersion)
            ? AppVersionProvider.GetVersion()
            : report.AppVersion;
        sb.AppendLine("<div class='footer'>");
        sb.AppendLine($"<div>Generated by {_brandDisplayName}</div>");
        sb.AppendLine($"<div>App Version: {SafeHtmlEncode(appVersion)}</div>");
        sb.AppendLine($"<div>ID: {report.RefurbishId}</div>");
        sb.AppendLine($"<div>Date Printed: {DateTime.Now:d}</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }

    private string SafeHtmlEncode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        
        return System.Security.SecurityElement.Escape(text) ?? string.Empty;
    }

    private string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return string.Empty;
    }

    private string MaskString(string? input)
    {
        if (string.IsNullOrEmpty(input) || input == "N/A") return "N/A";
        if (input.Length <= 4) return input;
        return "****" + input.Substring(input.Length - 4);
    }

    private void AppendTestRow(StringBuilder sb, string name, TestResult result)
    {
        string badgeText, scoreText;
        if (!result.Tested)
        {
            badgeText = "<span style='color:#6b7280; font-size:11px;'>—</span>";
            scoreText = "<span style='color:#6b7280; font-size:11px;'>—</span>";
        }
        else
        {
            var grade = string.IsNullOrEmpty(result.Grade) || result.Grade == "–" ? "none" : result.Grade;
            var badgeClass = $"grade-badge badge-{grade}";
            badgeText = $"<span class='{badgeClass}'>{grade}</span>";
            scoreText = $"<span style='font-weight:500;'>{result.Score}</span>";
        }

        sb.AppendLine("<tr>");
        sb.AppendLine($"<td class='test-name'>{name}</td>");
        sb.AppendLine($"<td style='text-align:center'>{badgeText}</td>");
        sb.AppendLine($"<td style='text-align:center'>{scoreText}</td>");
        sb.AppendLine("<td class='details'>");
        sb.AppendLine($"<div>{SafeHtmlEncode(result.Message)}</div>");
        if (result.Details.Any())
        {
            sb.AppendLine($"<div class='details-extra'>{string.Join(", ", result.Details.Select(d => SafeHtmlEncode(d)))}</div>");
        }
        sb.AppendLine("</td>");
        sb.AppendLine("</tr>");
    }

    private void AppendDisplaySection(StringBuilder sb, TestResult result)
    {
        if (!result.Tested)
            return;

        sb.AppendLine("<section class='details-section' style='margin-top:24px;'>");
        sb.AppendLine("<h3>Display Inspection Details</h3>");
        sb.AppendLine($"<p><strong>Status:</strong> {(result.Passed ? "Passed" : "Failed")} — {SafeHtmlEncode(result.Message)}</p>");
        if (result.Details.Any())
        {
            sb.AppendLine("<ul>");
            foreach (var detail in result.Details)
                sb.AppendLine($"<li>{SafeHtmlEncode(detail)}</li>");
            sb.AppendLine("</ul>");
        }
        sb.AppendLine("</section>");
    }

    public string SaveReport(QCReport report)
    {
        var html = GenerateHtmlReport(report);
        
        var folder = Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "Reports");
        Directory.CreateDirectory(folder);
        
        var filename = $"QC_Report_{report.RefurbishId}_{report.Timestamp:yyyyMMdd_HHmmss}.html";
        // Sanitize filename
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }
        
        var path = Path.Combine(folder, filename);
        File.WriteAllText(path, html, System.Text.Encoding.UTF8);
        
        return path;
    }
}

