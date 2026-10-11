namespace LaptopQC.Core.Services;

public static class MachineIdentityService
{
    private static readonly string[] InvalidSerialMarkers =
    {
        "DEFAULT STRING",
        "TO BE FILLED BY O.E.M.",
        "TO BE FILLED BY OEM",
        "SYSTEM SERIAL NUMBER",
        "UNKNOWN",
        "NONE",
        "NOT SPECIFIED",
        "NOT AVAILABLE",
        "N/A",
        "NA",
        "SERIAL",
        "0",
        "123456789"
    };

    public static bool IsUsableHardwareSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return false;

        var trimmed = serial.Trim();
        var upper = trimmed.ToUpperInvariant();

        if (trimmed.Length < 4)
            return false;

        if (InvalidSerialMarkers.Contains(upper))
            return false;

        return true;
    }

    public static string NormalizeMac(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
            return string.Empty;

        var cleaned = new string(macAddress.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return cleaned;
    }

    public static string BuildFallbackSerial(string? macAddress, string? computerName)
    {
        var normalizedMac = NormalizeMac(macAddress);
        if (!string.IsNullOrWhiteSpace(normalizedMac))
            return $"MAC-{normalizedMac}";

        if (!string.IsNullOrWhiteSpace(computerName))
            return $"HOST-{computerName.Trim().ToUpperInvariant()}";

        return "UNKNOWN-MACHINE";
    }

    public static string GetBestIdentityKey(string? serialNumber, string? macAddress, string? computerName)
    {
        if (IsUsableHardwareSerial(serialNumber))
            return serialNumber!.Trim().ToUpperInvariant();

        var normalizedMac = NormalizeMac(macAddress);
        if (!string.IsNullOrWhiteSpace(normalizedMac))
            return $"MAC-{normalizedMac}";

        if (!string.IsNullOrWhiteSpace(computerName))
            return $"HOST-{computerName.Trim().ToUpperInvariant()}";

        return "UNKNOWN-MACHINE";
    }
}
