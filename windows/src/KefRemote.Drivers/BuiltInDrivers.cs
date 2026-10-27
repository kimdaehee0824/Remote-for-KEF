using KefRemote.Core;
using KefRemote.Drivers.BluOS;
using KefRemote.Drivers.Kef;
using KefRemote.Drivers.LinkPlay;
using KefRemote.Drivers.MusicCast;

namespace KefRemote.Drivers;

public static class BuiltInDrivers
{
    /// <summary>All bundled drivers. Add a new driver here (see docs/drivers.md).</summary>
    public static readonly IReadOnlyList<DriverDescriptor> All =
    [
        KefW2Driver.Descriptor,
        BluOsDriver.Descriptor,
        LinkPlayDriver.Descriptor,
        MusicCastDriver.Descriptor,
    ];

    public static DriverDescriptor Find(string? id) =>
        All.FirstOrDefault(d => d.Id == id) ?? KefW2Driver.Descriptor;
}
