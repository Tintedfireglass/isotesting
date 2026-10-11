using System.Text.Json;
using Spectre.Console;
using LaptopQC.Core.Services;
using Pramaan.CLI.Diagnostics;

namespace Pramaan.CLI.UI;

public static class SavedReportsScreen
{
    public static async Task ShowAsync(DashboardState state)
    {
        state.LoadSavedReports();
        if (state.SavedReports.Count == 0)
        {
            state.FooterMessage = "No saved reports. " + string.Join("; ", state.Store.LoadErrors);
            return;
        }
        AnsiConsole.Clear();
        var choices = state.SavedReports.Select((r, i) => $"{i + 1}. {r.SavedAtUtc:u} {r.MachineId} [{r.Status}]").ToList();
        choices.Add("Back");
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[purple]Saved reports[/]")
            .PageSize(5).UseConverter(c => c.EscapeMarkup()).AddChoices(choices));
        int index = choices.IndexOf(choice);
        if (index == state.SavedReports.Count) return;
        var saved = state.SavedReports[index];
        state.SelectedView = 3;
        state.Page = 0;
        if (saved.Status == "Submitted") { state.FooterMessage = $"Submitted report: {saved.HealthId}"; return; }
        if (saved.Status == "Verify duplicate")
        {
            state.FooterMessage = $"Report {saved.ReportId} already exists on server. Verify health ID {saved.HealthId}; local copy retained.";
            state.SelectedView = 4;
            return;
        }
        var info = new LinuxSystemDiagnostic().GetInfo();
        var identity = MachineIdentityService.GetBestIdentityKey(info.SerialNumber, info.MacAddress, info.ComputerName);
        if (identity == "UNKNOWN-MACHINE" || !identity.Equals(saved.MachineId, StringComparison.OrdinalIgnoreCase))
        {
            state.FooterMessage = $"Retry requires original machine {saved.MachineId}. Current machine: {identity}. Saved identity remains unchanged.";
            state.SelectedView = 4;
            return;
        }
        if (!AnsiConsole.Confirm("Retry this saved report?")) return;
        // Reauthenticate against this report's original hardware. No tokens are stored on the report partition.
        var method = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Authenticate for retry")
            .AddChoices("License key", "Trial email", "Cancel"));
        string? token = null;
        if (method == "Cancel") return;
        if (method == "License key")
        {
            var key = AnsiConsole.Prompt(new TextPrompt<string>("License key:").Secret());
            var auth = new AuthService();
            var result = await auth.LoginWithLicenseAsync(key, saved.MachineId, info.MacAddress, info.ComputerName);
            if (result.Success) token = auth.Token;
            else state.FooterMessage = "Authentication failed: " + result.Message;
        }
        else
        {
            var email = AnsiConsole.Prompt(new TextPrompt<string>("Trial email:"));
            var result = await new TrialService().StartOrRefreshTrialAsync(email, saved.MachineId, info.MacAddress, info.ComputerName);
            if (result.Success) token = result.Token;
            else state.FooterMessage = "Authentication failed: " + result.ErrorMessage;
        }
        state.SelectedView = 4;
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var result = await state.Store.SubmitAsync(saved, new QCSubmissionService(), token);
            state.FooterMessage = result.Success ? $"Submitted saved report {saved.HealthId}" :
                result.IsDuplicate ? $"Report already exists; verify {saved.HealthId}. Local copy retained." : $"Retry failed: {result.ErrorMessage}";
            if (result.StorageError != null) state.FooterMessage += " | " + result.StorageError;
        }
        catch (Exception ex) { state.FooterMessage = $"Storage failed: {ex.Message}. Report retained in memory."; }
    }
}
