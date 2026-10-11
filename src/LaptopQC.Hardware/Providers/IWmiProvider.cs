#if WINDOWS
using System.Management;

namespace LaptopQC.Hardware.Providers;

public interface IWmiProvider
{
    IEnumerable<ManagementObject> Query(string wmiClass, string[]? properties = null);
    IEnumerable<ManagementObject> Query(string wmiClass, string wmiNamespace);
    T? GetValue<T>(ManagementObject obj, string propertyName, T? defaultValue = default);
}
#endif
