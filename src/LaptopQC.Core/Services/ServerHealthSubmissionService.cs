using System.Net.Http.Json;
using LaptopQC.Core.Models;

namespace LaptopQC.Core.Services;

public class ServerHealthSubmissionService
{
    private readonly ApiConfiguration _config;
    private readonly HttpClient _httpClient;

    public ServerHealthSubmissionService(ApiConfiguration? config = null)
    {
        _config = config ?? new ApiConfiguration();
        _httpClient = new HttpClient();
        var url = _config.ApiUrl;
        if (!url.EndsWith("/")) url += "/";
        _httpClient.BaseAddress = new Uri(url);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<SubmitResult> SubmitAsync(SubmitServerHealthRequest request, string? authToken)
    {
        try
        {
            var endpoint = _config.ApiUrl.EndsWith("/api") ? "/api/server-health" : "server-health";
            if (_config.ApiUrl.EndsWith("/api"))
            {
                endpoint = "server-health";
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
            return new SubmitResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}

