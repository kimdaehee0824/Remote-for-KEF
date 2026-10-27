using KefRemote.Core;
using KefRemote.Drivers.Kef;

namespace KefRemote.Drivers;

public static class BuiltInDrivers
{
    /// <summary>All bundled drivers. Add a new driver here (see docs/drivers.md).</summary>
    public static readonly IReadOnlyList<DriverDescriptor> All =
    [
        KefW2Driver.Descriptor,
    ];

    public static DriverDescriptor Find(string? id) =>
        All.FirstOrDefault(d => d.Id == id) ?? KefW2Driver.Descriptor;
}
