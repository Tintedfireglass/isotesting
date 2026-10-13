using System.Net.Http.Json;
using System.Text.Json;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;
using LaptopQC.Hardware.Models;
using Pramaan.CLI.Diagnostics;
using Spectre.Console;

namespace Pramaan.CLI.UI;

public record VerifiedActivation(string Token, int MachineId, int? TechnicianId);

/// <summary>Cached credentials are hints only. Every test requires a fresh server response.</summary>
public sealed class OnlineActivation
{
    readonly HttpClient client;
    readonly string api;
    public OnlineActivation(HttpClient? client = null, string? api = null)
    {
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        this.api = (api ?? new ApiConfiguration().ApiUrl).TrimEnd('/');
    }

    public async Task<VerifiedActivation> VerifyAsync(bool trial, string credential, SystemInfo machine)
    {
        var identity = MachineIdentityService.GetBestIdentityKey(machine.SerialNumber, machine.MacAddress, machine.ComputerName);
        if (identity == "UNKNOWN-MACHINE" || string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("Activation requires credentials and an identifiable machine.");
        object body = trial
            ? new { email = credential.Trim(), machineSerial = identity, macAddress = machine.MacAddress, computerName = machine.ComputerName }
            : new { licenseKey = credential.Trim(), machineSerial = identity, macAddress = machine.MacAddress, computerName = machine.ComputerName, platform = "linux" };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        using var response = await client.PostAsJsonAsync($"{api}/auth/{(trial ? "trial" : "license")}", body, options);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Activation rejected (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var token = root.GetProperty("token").GetString();
        int id = root.GetProperty("machineId").GetInt32();
        if (string.IsNullOrWhiteSpace(token) || id <= 0)
            throw new InvalidOperationException("Activation server returned an incomplete session.");
        if (trial && root.GetProperty("trialEndsAt").GetDateTime().ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Trial has expired. Activate a license to run tests.");
        int? technician = root.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var uid) ? uid.GetInt32() : null;
        return new VerifiedActivation(token, id, technician > 0 ? technician : null);
    }

    public static async Task<VerifiedActivation?> RequireAsync(DashboardState state, bool interactive = true)
    {
        try
        {
            var machine = new LinuxSystemDiagnostic().GetInfo();
            var identity = MachineIdentityService.GetBestIdentityKey(machine.SerialNumber, machine.MacAddress, machine.ComputerName);
            var credentials = new ActivationCredentials();
            var hint = credentials.Read(identity);
            var auth = new AuthService();
            var trials = new TrialService();
            string? credential = ForcePrompt ? null : SessionCredential ?? hint?.Credential ?? auth.LicenseKey;
            bool trial = SessionCredential != null ? SessionIsTrial : hint?.Trial ?? false;
            if (!ForcePrompt && string.IsNullOrWhiteSpace(credential)) { credential = trials.CurrentTrial?.Email; trial = true; }
            if (string.IsNullOrWhiteSpace(credential))
            {
                if (!interactive) throw new InvalidOperationException("Online activation required. Run Full QC to activate first.");
                var method = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[purple]Online activation required[/]")
                    .AddChoices("Activate license", "Start or validate trial", "Cancel"));
                if (method == "Cancel") throw new InvalidOperationException("Activation cancelled. No tests were run.");
                trial = method != "Activate license";
                credential = trial ? AnsiConsole.Ask<string>("Trial email:")
                    : AnsiConsole.Prompt(new TextPrompt<string>("License key:").Secret());
            }
            VerifiedActivation? verified = null;
            await AnsiConsole.Status().StartAsync("Verifying activation online...", async _ =>
                verified = await new OnlineActivation().VerifyAsync(trial, credential!, machine));
            // Persist only a machine-bound hint outside report storage. Always
            // revalidate online; a cached hint never grants permission to test.
            SessionCredential = credential;
            SessionIsTrial = trial;
            ForcePrompt = false;
            state.DeviceId = verified!.MachineId;
            state.SystemInfo = machine;
            state.FooterMessage = "Activation verified online.";
            try { credentials.Save(trial, credential!, identity); }
            catch (Exception ex) { state.FooterMessage += " Credentials could not be saved: " + ex.Message; }
            return verified;
        }
        catch (Exception ex)
        {
            SessionCredential = null;
            ForcePrompt = true;
            state.DeviceId = 0;
            state.StatusMessage = "Tests blocked: online activation required.";
            state.FooterMessage = "Tests blocked. Connect to the internet and activate a license or valid trial. " + ex.Message;
            state.SelectedView = 4;
            state.Page = 0;
            AnsiConsole.WriteLine(state.FooterMessage);
            return null;
        }
    }

    static string? SessionCredential;
    static bool SessionIsTrial;
    static bool ForcePrompt;
}
