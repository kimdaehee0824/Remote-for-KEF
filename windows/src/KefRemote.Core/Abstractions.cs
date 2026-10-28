namespace KefRemote.Core;

/// <summary>What a driver can change on the device. Reading state is always required.</summary>
[Flags]
public enum DriverCapabilities
{
    None = 0,
    Volume = 1,
    Mute = 2,
    Source = 4,
    Power = 8,
}

public enum PowerState
{
    Unknown,
    On,
    Standby,
}

/// <summary>Maturity of a driver. Only <see cref="Verified"/> drivers were tested on real hardware.</summary>
public enum DriverStatus
{
    Verified,
    Experimental,
}

public sealed record SourceOption(string Id, string Label);

public sealed record DeviceInfo(string Name, string Model, string Firmware, int? DeviceVolumeLimit = null);

/// <summary>A single consistent snapshot read from the device.</summary>
public sealed record SpeakerState
{
    public static readonly SpeakerState Unknown = new();

    public PowerState Power { get; init; }
    public string? SourceId { get; init; }
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public bool Valid { get; init; }

    /// <summary>Monotonic timestamp (ms) set by the controller when the snapshot arrived.</summary>
    public long SeenAt { get; init; }

    public bool Active => Valid && Power == PowerState.On;
}

public sealed record DriverOptions(string Host, string? Password = null);

/// <summary>
/// A network player. Implementations translate the vendor protocol only; all safety policy
/// (limits, verification, no replay) lives in <see cref="SpeakerController"/>.
/// Implementations must never retry a mutation on their own.
/// </summary>
public interface ISpeakerDriver : IDisposable
{
    DriverCapabilities Capabilities { get; }

    /// <summary>Sources the user may switch to. Empty when <see cref="DriverCapabilities.Source"/> is absent.</summary>
    IReadOnlyList<SourceOption> Sources { get; }

    /// <summary>
    /// Volume readback tolerance in percent points. Drivers that map a native scale onto 0-100
    /// may not read back exactly the value written.
    /// </summary>
    int VolumeTolerance => 0;

    /// <summary>Identify the device. Read only.</summary>
    Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Read the current state. Read only.</summary>
    Task<SpeakerState> ReadAsync(CancellationToken cancellationToken = default);

    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
    Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Standby (<paramref name="on"/> false) or wake to the input that was active before standby.
    /// Only called when <see cref="DriverCapabilities.Power"/> is present.
    /// </summary>
    Task SetPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>
    /// For devices whose output level depends on the direction a volume is reached from: a slightly higher value
    /// to write for a moment before raising <paramref name="current"/> to <paramref name="target"/>, or null when
    /// the target can be written directly. The controller caps it at the volume limit and only verifies the target.
    /// </summary>
    int? ApproachOvershoot(int current, int target) => null;
}

public sealed record DriverDescriptor(
    string Id,
    string DisplayName,
    string SupportedModels,
    DriverStatus Status,
    bool UsesPassword,
    Func<DriverOptions, ISpeakerDriver> Create);

public sealed class SpeakerException(string message, Exception? inner = null) : Exception(message, inner);
