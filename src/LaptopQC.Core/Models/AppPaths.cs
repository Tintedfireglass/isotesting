using System;
using System.IO;

namespace LaptopQC.Core.Models;

public static class AppPaths
{
    public static string AppDataFolderName { get; set; } = "Pramaan";

    public static string AppDataDir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                // Fallback for Linux when HOME is missing (e.g. running via sudo)
                appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(appData))
                {
                    appData = "/tmp";
                }
                else
                {
                    appData = Path.Combine(appData, ".config");
                }
            }
            return Path.Combine(appData, AppDataFolderName);
        }
    }
}
