using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaptopQC.Core.Services;

/// <summary>
/// Manages the 7-day free trial lifecycle.
///
/// Responsibilities:
///   - Calling POST /api/auth/trial to start or re-validate a trial
///   - Persisting the trial token + expiry locally in %AppData%\Pramaan\trial_session.json
///   - Exposing IsTrialActive / IsTrialExpired / DaysRemaining helper properties
///
/// Cloud enforcement: the server validates email + machine fingerprint uniqueness.
/// Local session:     used to skip an API call on subsequent launches within the valid period.
/// </summary>
public class TrialService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _trialFilePath;

    public TrialSession? CurrentTrial { get; private set; }

    public bool IsTrialActive =>
        CurrentTrial != null &&
        !string.IsNullOrWhiteSpace(CurrentTrial.Token) &&
        CurrentTrial.TrialEndsAtUtc > DateTime.UtcNow;

    public bool IsTrialExpired =>
        CurrentTrial != null &&
        CurrentTrial.TrialEndsAtUtc <= DateTime.UtcNow;

    public int DaysRemaining =>
        IsTrialActive
            ? Math.Max(0, (int)Math.Ceiling((CurrentTrial!.TrialEndsAtUtc - DateTime.UtcNow).TotalDays))
            : 0;

    public TrialService(string apiUrl = "https://pramaan-dashboard.gadgetguruz.com/api")
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _trialFilePath = Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "trial_session.json");

        LoadLocalSession();
    }

    // ─── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new trial (or re-validates an active one) via the server.
    /// Returns a TrialResult with the token and expiry date on success, or an error message.
    /// </summary>
    public async Task<TrialResult> StartOrRefreshTrialAsync(
        string email,
        string machineSerial,
        string? macAddress = null,
        string? computerName = null)
    {
        // Track whether a local session already exists before we call the server.
        // If it doesn't, this is a genuine first-time activation.
        bool isNew = CurrentTrial == null;

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiUrl}/auth/trial", new
            {
                email,
                machineSerial,
                macAddress,
                computerName
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TrialApiResponse>();
                if (result != null)
                {
                    var session = new TrialSession
                    {
                        Email          = result.User.Username,
                        Token          = result.Token,
                        MachineId      = result.MachineId,
                        TrialEndsAtUtc = result.TrialEndsAt.ToUniversalTime()
                    };
                    CurrentTrial = session;
                    SaveLocalSession(session);

                    return new TrialResult
                    {
                        Success       = true,
                        IsNew         = isNew,
                        Token         = result.Token,
                        MachineId     = result.MachineId,
                        TrialEndsAt   = result.TrialEndsAt,
                        DaysRemaining = Math.Max(0, (int)Math.Ceiling((result.TrialEndsAt.ToUniversalTime() - DateTime.UtcNow).TotalDays))
                    };
                }
            }

            var errorBody = await response.Content.ReadFromJsonAsync<TrialApiError>();
            return new TrialResult
            {
                Success      = false,
                ErrorMessage = errorBody?.Message ?? "Trial activation failed"
            };
        }
        catch (HttpRequestException ex)
        {
            return new TrialResult { Success = false, ErrorMessage = $"Cannot connect to server.\n{ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new TrialResult { Success = false, ErrorMessage = "Connection timed out. Check your internet connection." };
        }
        catch (Exception ex)
        {
            return new TrialResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Clears the local trial session file (called on expiry or full license activation).
    /// </summary>
    public void ClearTrial()
    {
        CurrentTrial = null;
        try
        {
            if (File.Exists(_trialFilePath))
                File.Delete(_trialFilePath);
        }
        catch { /* best-effort */ }
    }

    // ─── Local persistence ───────────────────────────────────────────────────────

    private void SaveLocalSession(TrialSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(_trialFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_trialFilePath, JsonSerializer.Serialize(session));
        }
        catch { /* best-effort */ }
    }

    private void LoadLocalSession()
    {
        try
        {
            if (!File.Exists(_trialFilePath)) return;
            var session = JsonSerializer.Deserialize<TrialSession>(File.ReadAllText(_trialFilePath));
            if (session != null && !string.IsNullOrWhiteSpace(session.Token))
                CurrentTrial = session;
        }
        catch { CurrentTrial = null; }
    }
}

// ─── DTOs ────────────────────────────────────────────────────────────────────

public class TrialSession
{
    public string   Email           { get; set; } = "";
    public string   Token           { get; set; } = "";
    public int      MachineId       { get; set; }
    public DateTime TrialEndsAtUtc  { get; set; }
}

public class TrialResult
{
    public bool     Success       { get; set; }
    /// <summary>True only on genuine first-time activation (no prior local session existed).</summary>
    public bool     IsNew         { get; set; }
    public string?  Token         { get; set; }
    public int      MachineId     { get; set; }
    public DateTime TrialEndsAt   { get; set; }
    public int      DaysRemaining { get; set; }
    public string?  ErrorMessage  { get; set; }
}

internal class TrialApiResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("user")]
    public TrialApiUser User { get; set; } = new();

    [JsonPropertyName("machineId")]
    public int MachineId { get; set; }

    [JsonPropertyName("trialEndsAt")]
    public DateTime TrialEndsAt { get; set; }
}

internal class TrialApiUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
}

internal class TrialApiError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
