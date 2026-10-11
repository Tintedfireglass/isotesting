using System.Net.Http.Json;
using LaptopQC.Core.Models;

namespace LaptopQC.Core.Services;

public class MachineHistorySubmissionService
{
    private readonly ApiConfiguration _config;
    private readonly HttpClient _httpClient;

    public MachineHistorySubmissionService(ApiConfiguration? config = null)
    {
        _config = config ?? new ApiConfiguration();
        _httpClient = new HttpClient();
        var url = _config.ApiUrl;
        if (!url.EndsWith("/")) url += "/";
        _httpClient.BaseAddress = new Uri(url);
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _config.ApiKey);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<SubmitResult> SubmitComponentGradesAsync(
        QCReport report,
        Dictionary<string, ComponentGrade> componentGrades,
        string source,
        string? authToken = null)
    {
        try
        {
            var request = MapToRequest(report, componentGrades, source);

            var endpoint = _config.ApiUrl.EndsWith("/api") ? "/api/machine-history" : "machine-history";
            if (_config.ApiUrl.EndsWith("/api"))
            {
                endpoint = "machine-history";
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request)
            };

            if (!string.IsNullOrWhiteSpace(authToken))
            {
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
            }

            var response = await _httpClient.SendAsync(message);

            if (response.IsSuccessStatusCode)
            {
                return new SubmitResult { Success = true };
            }

            var error = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"Machine History Submission Failed: {response.StatusCode} - {error}");
            var isAuthError = response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                              response.StatusCode == System.Net.HttpStatusCode.Forbidden;
            return new SubmitResult
            {
                Success = false,
                IsAuthError = isAuthError,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? response.ReasonPhrase : error
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Machine History Submission Error: {ex.Message}");
            return new SubmitResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private SubmitMachineHistoryRequest MapToRequest(
        QCReport report,
        Dictionary<string, ComponentGrade> componentGrades,
        string source)
    {
        var identityKey = MachineIdentityService.IsUsableHardwareSerial(report.SystemInfo?.SerialNumber)
            ? report.SystemInfo?.SerialNumber
            : MachineIdentityService.BuildFallbackSerial(report.SystemInfo?.MacAddress ?? report.MacAddress, report.SystemInfo?.ComputerName);

        if (string.IsNullOrWhiteSpace(identityKey))
            identityKey = _config.StationId;

        return new SubmitMachineHistoryRequest
        {
            MachineId = identityKey ?? "",
            Timestamp = report.Timestamp,
            Source = source,
            ComponentGrades = componentGrades,
            AppVersion = string.IsNullOrWhiteSpace(report.AppVersion) ? AppVersionProvider.GetVersion() : report.AppVersion
        };
    }
}
