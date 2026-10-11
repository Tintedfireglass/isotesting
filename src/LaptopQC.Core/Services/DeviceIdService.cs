using System.Text.Json;

namespace LaptopQC.Core.Services;

public class DeviceIdService
{
    private static readonly object _lock = new();
    private const int StartId = 3000001;
    private static readonly string[] RegistryPaths =
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Pramaan",
            "device_registry.json"),
        Path.Combine(
            LaptopQC.Core.Models.AppPaths.AppDataDir,
            "device_registry.json")
    };

    private Dictionary<string, int> _registry;

    public DeviceIdService()
    {
        _registry = LoadRegistry();
    }

    public int GetOrGenerateDeviceId(string serialNumber)
    {
        var identityKey = serialNumber.Trim().ToUpperInvariant();

        lock (_lock)
        {
            if (_registry.TryGetValue(identityKey, out int id))
            {
                return id;
            }

            // Generate new ID
            int nextId = StartId;
            if (_registry.Values.Count > 0)
            {
                nextId = _registry.Values.Max() + 1;
                // Ensure we don't go below StartId if registry was manually edited or empty
                if (nextId < StartId) nextId = StartId;
            }

            _registry[identityKey] = nextId;
            SaveRegistry();
            
            return nextId;
        }
    }

    private Dictionary<string, int> LoadRegistry()
    {
        foreach (var path in RegistryPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var registry = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (registry != null && registry.Count > 0)
                    {
                        return registry;
                    }
                }
            }
            catch (Exception)
            {
                // Try next path
            }
        }

        return new Dictionary<string, int>();
    }

    private void SaveRegistry()
    {
        var json = JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true });
        foreach (var path in RegistryPaths)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, json);
                return;
            }
            catch (Exception)
            {
                // Try next path
            }
        }
    }
}
