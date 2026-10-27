# Drivers

The apps talk to a player through a small driver contract. A driver translates one vendor protocol. It contains no safety policy: limits, read-back verification and the no-replay rule live in the controller and apply to every driver equally.

| Windows (C#) | macOS (Swift) |
|---|---|
| `ISpeakerDriver` in `windows/src/KefRemote.Core` | `SpeakerDriver` in `macos/` |
| `SpeakerController` | `SpeakerController` (`macos/Sources/KefRemoteCore`) |

## Contract

```text
capabilities : set of { volume, mute, source, power }   # what can be changed
sources      : [ { id, label } ]                         # empty unless `source`
volumeTolerance : int (default 0)                        # read-back tolerance when a native scale is mapped to 0-100

connect() -> DeviceInfo { name, model, firmware, deviceVolumeLimit? }   # read only
read()    -> SpeakerState { power: on|standby|unknown, sourceId?, volume 0-100, muted }   # read only
setVolume(int 0-100)
setMute(bool)
setSource(id)
setPower(bool)          # only with `power`
```

Rules for driver authors:

1. `connect` and `read` must never write.
2. A setter sends exactly one request. Never retry a mutation, even after a timeout; the controller reads back and decides.
3. Throw instead of guessing. A malformed response, an HTML login page, a 401/403 or an unknown auth mode is an error.
4. Talk only to private IPv4 hosts (use `HostAddress.ParseLocal`).
5. `read` returns one consistent snapshot. If the device is mid-change (for example two different input readings), throw.

## Controller policy (applies to all drivers)

- Startup, reconnect and polling only read. Nothing is written without a user action.
- One request at a time. Mute, input and power commands arriving while busy are dropped. Volume input arriving while busy is merged into one slot holding the latest target (absolute targets replace it, steps are summed and applied to the value read right before writing). The slot is discarded on any failure or disconnect.
- Each command re-reads first and is cancelled if the input changed since the user saw it.
- Each write is confirmed by reading back (up to 4 reads, 180 ms apart).
- Any failure invalidates the state. Commands are blocked until the next successful poll.
- Disconnecting or editing the connection bumps a generation counter; in-flight work is discarded and never replayed.
- Raises are capped at min(app limit, device limit). Lowering is always allowed. A volume above the cap is shown as is and never forced down.
- Unmute is refused while the volume is above the cap.
- Input switching requires mute support: mute, verify, switch, verify. Unmute again only if the new input's volume is not higher than before and not above the cap; otherwise stay muted and say so.

## Bundled drivers

| Id | Devices | Status | Notes |
|---|---|---|---|
| `kef-w2` | KEF LSX II LT, LSX II, LS50 Wireless II, LS60, XIO | **Verified** on LSX II LT only | volume, mute, input. [Protocol](protocol/kef-w2.md) |

Other players are not bundled because nobody on the project could test them. Candidates with public local HTTP APIs include BluOS (port 11000, `/Status`, `/Volume`), LinkPlay/WiiM (`httpapi.asp`, often HTTPS with a self-signed certificate) and Yamaha MusicCast (Yamaha Extended Control). A driver for any of them is welcome from someone who owns the device.

## Adding a driver

1. Create `windows/src/KefRemote.Drivers/<Vendor>/<Vendor>Driver.cs` implementing `ISpeakerDriver`. Derive from `HttpDriver` for HTTP devices.
2. Expose a `DriverDescriptor` (id, display name, supported models, status, whether a password is used, factory) and add it to `BuiltInDrivers.All`.
3. Add stub tests in `windows/tests/KefRemote.Tests` (see `KefW2DriverTests.cs` and `FakeKefDevice.cs` for an in-memory device). Mark the driver `Experimental` until the safe hardware checks below pass on a real device, and include the results in the pull request.
4. If the protocol has non-obvious parts (crypto, signing), document it in `docs/protocol/` and add language-neutral vectors to `spec/test-vectors/` so the macOS implementation can share them.

### Safe hardware checks before marking a driver verified

Run with the volume at a quiet level (30 or below):

1. Connect and poll for a minute. Confirm zero write requests.
2. Lower the volume by one step and confirm the read-back.
3. Mute and unmute.
4. If inputs are supported: switch away and back. Confirm the speaker is muted during the switch and the volume did not rise.
5. Unplug the network during a command. Confirm nothing is replayed after reconnecting.
