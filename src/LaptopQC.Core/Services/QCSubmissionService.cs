using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using LaptopQC.Core.Models;

namespace LaptopQC.Core.Services;

public class QCSubmissionService
{
    private readonly ApiConfiguration _config;
    private readonly HttpClient _httpClient;

    public QCSubmissionService(ApiConfiguration? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new ApiConfiguration();
        _httpClient = httpClient ?? new HttpClient();
        var url = _config.ApiUrl;
        if (!url.EndsWith("/")) url += "/";
        _httpClient.BaseAddress = new Uri(url);
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _config.ApiKey);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Submit a QC report to the server.
    /// </summary>
    /// <param name="report">The QC report to submit</param>
    /// <param name="technicianId">Optional technician ID if user is logged in</param>
    /// <returns>SubmitResult with status and message</returns>
    public async Task<SubmitResult> SubmitReportAsync(QCReport report, int? technicianId = null, string? authToken = null)
    {
        try { return await SubmitPreparedAsync(PrepareReport(report, technicianId), authToken); }
        catch (Exception ex) { return new SubmitResult { ErrorMessage = ex.Message }; }
    }

    // Freeze the wire payload once. Retrying must not regrade or rehash a report.
    public string PrepareReport(QCReport report, int? technicianId = null)
    {
        ValidateCondition(report.PhysicalCondition, new[] { "A+", "A", "B", "C", "D", "E" }, "physicalCondition");
        ValidateCondition(report.ScratchesAndDents, new[] { "None", "Minor", "Major" }, "scratchesAndDents");
        var request = MapToRequest(report, technicianId);
        request.PhysicalCondition = string.IsNullOrWhiteSpace(report.PhysicalCondition) ? null : report.PhysicalCondition;
        request.ScratchesAndDents = string.IsNullOrWhiteSpace(report.ScratchesAndDents) ? null : report.ScratchesAndDents;
        if (string.IsNullOrWhiteSpace(request.ReportId) || string.IsNullOrWhiteSpace(request.MachineId))
            throw new ArgumentException("Report ID and machine identity are required.");
        return JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    static void ValidateCondition(string? value, string[] allowed, string field)
    {
        if (!string.IsNullOrWhiteSpace(value) && !allowed.Contains(value))
            throw new ArgumentException($"{field}: invalid value '{value}'. Choose {string.Join(", ", allowed)}.");
    }

    public async Task<SubmitResult> SubmitPreparedAsync(string payload, string? authToken = null)
    {
        try
        {
            
            // Adjust the URL if base address ends with /api or not
            var endpoint = _config.ApiUrl.EndsWith("/api") ? "/api/qc-results" : "qc-results";
            // If base address is full path to API root
            if (_config.ApiUrl.EndsWith("/api"))
            {
                endpoint = "qc-results";
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(authToken))
            {
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
            }
            
            using var response = await _httpClient.SendAsync(message);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SubmitResponse>();
                return new SubmitResult
                {
                    Success = true,
                    DemoExhausted = result?.DemoExhausted ?? false
                };
            }

            var error = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"API Submission Failed: {response.StatusCode} - {error}");
            var isAuthError = response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                              response.StatusCode == System.Net.HttpStatusCode.Forbidden;
            return new SubmitResult
            {
                Success = false,
                IsAuthError = isAuthError,
                IsDuplicate = response.StatusCode == System.Net.HttpStatusCode.Conflict,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? response.ReasonPhrase : error
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"API Submission Error: {ex.Message}");
            return new SubmitResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private SubmitQCResultRequest MapToRequest(QCReport report, int? technicianId = null)
    {
        // Calculate total RAM safely
        long ramTotal = 0;
        if (report.RamDetails != null)
        {
            ramTotal = (long)(report.RamDetails.TotalCapacityGB * 1024 * 1024 * 1024); // Estimate bytes
        }

        var identityKey = MachineIdentityService.IsUsableHardwareSerial(report.SystemInfo?.SerialNumber)
            ? report.SystemInfo?.SerialNumber?.Trim()
            : MachineIdentityService.BuildFallbackSerial(report.SystemInfo?.MacAddress ?? report.MacAddress, report.SystemInfo?.ComputerName);
        if (string.IsNullOrWhiteSpace(identityKey))
            identityKey = _config.StationId;

        var request = new SubmitQCResultRequest
        {
            ReportId = report.ReportId,
            HealthId = report.HealthId,
            MachineId = identityKey ?? "",
            Timestamp = report.Timestamp,
            RefurbishId = report.RefurbishId,
            TechnicianNotes = report.TechnicianNotes,
            AppVersion = string.IsNullOrWhiteSpace(report.AppVersion) ? AppVersionProvider.GetVersion() : report.AppVersion,
            PhysicalCondition = report.PhysicalCondition,
            ScratchesAndDents = report.ScratchesAndDents,
            OverallPass = report.OverallPass,
            OverallScore = report.OverallScore,
            OverallGrade = report.OverallGrade,
            TechnicianId = technicianId, // Include logged-in technician ID if available
            TechnicianLabel = report.TechnicianId,
            
            SystemInfo = new SystemInfoSnapshot
            {
                ComputerName = report.SystemInfo?.ComputerName,
                Manufacturer = report.SystemInfo?.Manufacturer,
                Model = report.SystemInfo?.Model,
                ProductVersion = report.SystemInfo?.ProductVersion,
                SystemFamily = report.SystemInfo?.SystemFamily,
                SerialNumber = identityKey,
                MacAddress = report.MacAddress,
                OsVersion = report.SystemInfo?.OsVersion,
                WindowsProductName = report.SystemInfo?.WindowsProductName,
                WindowsActivationStatus = report.SystemInfo?.WindowsActivationStatus,
                IsWindowsActivated = report.SystemInfo?.IsWindowsActivated,
                WindowsLastUpdatedAt = report.SystemInfo?.WindowsLastUpdatedAt,
                AntivirusStatus = report.SystemInfo?.AntivirusStatus,
                IsAntivirusHealthy = report.SystemInfo?.IsAntivirusHealthy,
                DeviceId = report.DeviceId,
                CpuModel = report.CpuDetails?.Name,
                RamTotal = ramTotal > 0 ? ramTotal : 0,
                // BIOS fields — collected from Win32_BIOS via WMI
                BiosVersion = report.SystemInfo?.BiosVersion,
                BiosManufacturer = report.SystemInfo?.BiosManufacturer,
                BiosReleaseDate = report.SystemInfo?.BiosReleaseDate,
            },
            
            CpuDetails = report.CpuDetails,
            RamDetails = report.RamDetails,
            StorageDetails = report.StorageDetails,
            BatteryDetails = report.BatteryDetails,
            DeviceDetails = report.DeviceDetails,
            
            // PRAMAAN scoring data
            PramaanScore = report.PramaanResult?.OverallHealthScore,
            PramaanGrade = report.PramaanResult?.GradeBand,
            PramaanCategoryScores = report.PramaanResult?.CategoryScores,
            PramaanRiskFlags = report.PramaanResult?.RiskFlags,
            PramaanAlgorithmVersion = report.PramaanResult?.AlgorithmVersion,
            
            TestResults = new List<ApiTestResult>()
        };

        // Generate tamper-proof hash using the complete report JSON
        try 
        {
            string jsonReport = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = false });
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(jsonReport));
                request.PramaanHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                report.DiagnosticHash = request.PramaanHash; // Store it back on the report for reference
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to generate diagnostic hash: {ex.Message}");
            request.PramaanHash = "";
        }

        // Helper to add test result
        void AddResult(string type, TestResult result)
        {
            request.TestResults.Add(new ApiTestResult
            {
                TestType = type,
                Tested = result.Tested,
                Passed = result.Passed,
                Score = result.Score,
                Grade = result.Grade,
                Message = result.Message,
                Details = type == "Keyboard" && result.KeyboardMissingKeys != null
                    ? new { missingKeys = result.KeyboardMissingKeys }
                    : (object)result.Details,
                Timestamp = result.Timestamp == default ? report.Timestamp : result.Timestamp
            });
        }

        AddResult("CPU", report.CpuTest);
        AddResult("RAM", report.RamTest);
        AddResult("Storage", report.StorageTest);
        AddResult("Battery", report.BatteryTest);
        AddResult("Keyboard", report.KeyboardTest);
        AddResult("Trackpad", report.TrackpadTest);
        AddResult("Display", report.DisplayTest);
        AddResult("USB", report.UsbTest);
        AddResult("AudioVideo", report.AudioVideoTest);
        AddResult("AudioJack", report.AudioJackTest);
        AddResult("GPU", report.GpuTest);
        AddResult("Network", report.NetworkTest);

        return request;
    }
}

public class SubmitResult
{
    public bool Success { get; set; }
    public bool IsAuthError { get; set; }
    public string? ErrorMessage { get; set; }
    public bool DemoExhausted { get; set; }
    public bool IsDuplicate { get; set; }
    public int? StatusCode { get; set; }
    public string? StorageError { get; set; }
}
