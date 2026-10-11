namespace LaptopQC.Core.Models;

public class ApiConfiguration
{
    // Default to localhost for now, user can change via config file later
    // Updated to point to your Vercel deployment
    public string ApiUrl { get; set; } = "https://pramaan-dashboard.gadgetguruz.com/api";
    
    // Default API key matching the one in web/lib/auth.ts
    public string ApiKey { get; set; } = "default-api-key-change-in-production";
    
    public bool AutoSubmitEnabled { get; set; } = true;
    public string StationId { get; set; } = Environment.MachineName;
}
