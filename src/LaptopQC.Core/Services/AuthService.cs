using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaptopQC.Core.Services;

/// <summary>
/// Handles optional user authentication for the desktop app.
/// The app works locally without login, but login allows associating results with a technician.
/// </summary>
public class AuthService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _sessionFilePath;
    
    public bool IsLoggedIn => CurrentUser != null;
    public UserInfo? CurrentUser { get; private set; }
    public string? Token { get; private set; }
    public string? LicenseKey { get; private set; }
    public int? MachineId { get; private set; }
    public bool IsTrialSession { get; private set; }
    public DateTime? LastOnlineCheckUtc { get; private set; }
    public event Action? LoggedOut;

    public AuthService(string apiUrl = "https://pramaan-dashboard.gadgetguruz.com/api")
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _sessionFilePath = Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "auth_session.json");

        LoadSession();
    }

    /// <summary>
    /// Attempt to login with username and password.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiUrl}/auth/login", new
            {
                username,
                password
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (result != null)
                {
                    CurrentUser = result.User;
                    Token = result.Token;
                    LicenseKey = null;
                    IsTrialSession = false;
                    LastOnlineCheckUtc = DateTime.UtcNow;
                    SaveSession();
                    return new LoginResult { Success = true, Message = "Login successful" };
                }
            }
            
            var error = await response.Content.ReadAsStringAsync();
            return new LoginResult { Success = false, Message = error ?? "Login failed" };
        }
        catch (HttpRequestException ex)
        {
            return new LoginResult 
            { 
                Success = false, 
                Message = $"Cannot connect to server. Working offline.\nError: {ex.Message}" 
            };
        }
        catch (TaskCanceledException)
        {
            return new LoginResult 
            { 
                Success = false, 
                Message = "Connection timed out. Check your internet connection." 
            };
        }
        catch (Exception ex)
        {
            return new LoginResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Attempt to login with a 16-digit license key and lock it to this machine's hardware fingerprint.
    /// The server allocates a unique Machine ID and returns it.
    /// </summary>
    public async Task<LoginResult> LoginWithLicenseAsync(string licenseKey, string machineSerial, string? macAddress = null, string? computerName = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiUrl}/auth/license", new
            {
                licenseKey = licenseKey,
                machineSerial = machineSerial,
                macAddress = macAddress,
                computerName = computerName
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (result != null)
                {
                    CurrentUser = result.User;
                    Token = result.Token;
                    LicenseKey = licenseKey;
                    MachineId = result.MachineId;
                    IsTrialSession = false;
                    LastOnlineCheckUtc = DateTime.UtcNow;
                    SaveSession();
                    return new LoginResult { Success = true, Message = "License Login successful" };
                }
            }
            
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            return new LoginResult { Success = false, IsAuthError = true, Message = error?.Message ?? "License Login failed" };
        }
        catch (HttpRequestException ex)
        {
            return new LoginResult 
            { 
                Success = false, 
                Message = $"Cannot connect to server to validate license.\nError: {ex.Message}" 
            };
        }
        catch (TaskCanceledException)
        {
            return new LoginResult 
            { 
                Success = false, 
                Message = "Connection timed out verifying license. Check your internet connection." 
            };
        }
        catch (Exception ex)
        {
            return new LoginResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Logout and clear session.
    /// </summary>
    public void Logout()
    {
        CurrentUser = null;
        Token = null;
        LicenseKey = null;
        MachineId = null;
        LastOnlineCheckUtc = null;
        IsTrialSession = false;
        ClearSession();
        LoggedOut?.Invoke();
    }

    /// <summary>
    /// Activates a trial session using credentials returned from the server's /api/auth/trial endpoint.
    /// Sets the app into a logged-in state identical to license login, but with no LicenseKey stored.
    /// The trial session is NOT persisted to auth_session.json — TrialService manages its own file.
    /// </summary>
    public void StartTrialSession(string email, string token, int machineId, DateTime trialEndsAtUtc)
    {
        CurrentUser = new UserInfo
        {
            Id       = 0,
            Username = email,
            Role     = "TrialUser",
            Email    = email
        };
        Token              = token;
        LicenseKey         = null;   // Trials have no license key
        MachineId          = machineId;
        LastOnlineCheckUtc = DateTime.UtcNow;
        IsTrialSession     = true;
        // Do NOT call SaveSession() — trial state is managed by TrialService
    }

    /// <summary>
    /// Get the current technician ID for result attribution.
    /// Returns null if not logged in.
    /// </summary>
    public int? GetTechnicianId() => CurrentUser?.Id;

    private void SaveSession()
    {
        try
        {
            if (CurrentUser == null || string.IsNullOrWhiteSpace(Token))
                return;

            var directory = Path.GetDirectoryName(_sessionFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new AuthSessionRecord
            {
                Token = Token!,
                User = CurrentUser,
                LicenseKey = LicenseKey,
                MachineId = MachineId,
                LastOnlineCheckUtc = LastOnlineCheckUtc
            };

            var json = JsonSerializer.Serialize(payload);
            File.WriteAllText(_sessionFilePath, json);
        }
        catch
        {
            // Best-effort persistence; don't block login flow.
        }
    }

    private void LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionFilePath))
                return;

            var json = File.ReadAllText(_sessionFilePath);
            var payload = JsonSerializer.Deserialize<AuthSessionRecord>(json);
            if (payload?.User == null || string.IsNullOrWhiteSpace(payload.Token))
                return;

            CurrentUser = payload.User;
            Token = payload.Token;
            LicenseKey = payload.LicenseKey;
            MachineId = payload.MachineId;
            LastOnlineCheckUtc = payload.LastOnlineCheckUtc;
        }
        catch
        {
            CurrentUser = null;
            Token = null;
            LicenseKey = null;
            MachineId = null;
            LastOnlineCheckUtc = null;
        }
    }

    private void ClearSession()
    {
        try
        {
            if (File.Exists(_sessionFilePath))
            {
                File.Delete(_sessionFilePath);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    public bool IsOnlineCheckRequired(int maxDays = 7)
    {
        if (!LastOnlineCheckUtc.HasValue)
            return true;

        return DateTime.UtcNow - LastOnlineCheckUtc.Value > TimeSpan.FromDays(maxDays);
    }

    public void MarkOnlineCheckNow()
    {
        LastOnlineCheckUtc = DateTime.UtcNow;
        SaveSession();
    }

    /// <summary>
    /// Refreshes the trial session by re-calling /api/auth/trial with the stored email + machine info.
    /// This updates last_seen on the machine row — no separate heartbeat endpoint needed.
    /// </summary>
    public async Task SendTrialHeartbeatAsync(string machineSerial, string? macAddress = null, string? computerName = null)
    {
        var email = CurrentUser?.Username;
        if (string.IsNullOrWhiteSpace(email))
            return;

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
                LastOnlineCheckUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Trial heartbeat failed: {ex.Message}");
        }
    }
}

internal sealed class AuthSessionRecord
{
    public string Token { get; set; } = "";
    public UserInfo User { get; set; } = new();
    public string? LicenseKey { get; set; }
    public int? MachineId { get; set; }
    public DateTime? LastOnlineCheckUtc { get; set; }
}

public class LoginResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    /// <summary>
    /// True only when the server explicitly rejects the credentials (HTTP 4xx).
    /// False for network errors, timeouts, or other transient failures.
    /// Background tasks should only logout when this is true.
    /// </summary>
    public bool IsAuthError { get; set; }
}

public class LoginResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";
    
    [JsonPropertyName("user")]
    public UserInfo User { get; set; } = new();

    [JsonPropertyName("machineId")]
    public int? MachineId { get; set; }
}

public class UserInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
    
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    public string DisplayText => DisplayName ?? Username;
    
    public string RoleDisplay => Role switch
    {
        "SuperAdmin" => "Gadget Guruz",
        "Refurbisher" => "Refurbisher",
        "Technician" => "Technician",
        "Enterprise" => "Enterprise",
        "B2CDevice" => "B2C Device",
        _ => Role
    };
}

public class ApiErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
