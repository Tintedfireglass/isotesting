using System.Net.Http.Json;
using LaptopQC.Core.Models;

namespace LaptopQC.Core.Services;

/// <summary>
/// Fetches the live PRAMAAN scoring configuration from the centralized database via the Web API.
/// </summary>
public class PramaanConfigService
{
    private readonly ApiConfiguration _config;
    private readonly HttpClient _httpClient;

    public PramaanConfigService(ApiConfiguration? config = null)
    {
        _config = config ?? new ApiConfiguration();
        _httpClient = new HttpClient();
        
        var url = _config.ApiUrl;
        if (!url.EndsWith("/")) url += "/";
        _httpClient.BaseAddress = new Uri(url);
        
        // Timeout relatively short to fallback quickly if offline
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Fetches the active scoring configuration.
    /// Falls back to the hardcoded default if the API is unreachable.
    /// </summary>
    public async Task<PramaanScoringConfig> GetActiveConfigAsync()
    {
        try
        {
            var endpoint = _config.ApiUrl.EndsWith("/api") ? "/api/pramaan/config" : "pramaan/config";
            if (_config.ApiUrl.EndsWith("/api"))
            {
                endpoint = "pramaan/config";
            }
            
            var response = await _httpClient.GetAsync(endpoint);
            if (response.IsSuccessStatusCode)
            {
                var liveConfig = await response.Content.ReadFromJsonAsync<PramaanScoringConfig>();
                if (liveConfig != null)
                {
                    return liveConfig;
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"Failed to fetch live config: {response.StatusCode}. Falling back to default.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching live Pramaan config: {ex.Message}. Falling back to default.");
        }

        // Return hardcoded safely if network fails
        return new PramaanScoringConfig();
    }
}
