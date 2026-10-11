using System.Reflection;

namespace LaptopQC.Core.Services;

public static class AppVersionProvider
{
    public static string GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        var version = assembly.GetName().Version;
        return version != null ? version.ToString() : "Unknown";
    }
}
