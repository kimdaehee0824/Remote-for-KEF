# Remote for KEF (macOS)

A menu bar app for KEF W2 speakers. It changes the speaker's own volume (not the Mac's), mutes, and switches inputs, from a panel that works like the Sound module in Control Center.

- Menu bar symbol fills with the speaker volume; optional number next to it.
- Volume slider: shows the value while dragging and sends once on release. Arrow keys are merged for 200 ms.
- Input list with the same mute → switch → verify sequence as Windows.
- Shows standby. Power control is left out until it is verified on real hardware.
- Settings window: IP address, speaker webpage password (Keychain), app volume limit, launch at login, volume number in the menu bar.

Safety rules are shared with the Windows app and described in [docs/drivers.md](../docs/drivers.md). Connecting, reconnecting and polling only read; nothing is written without a user action.

## Requirements

- macOS 14 Sonoma or later. The app uses `@Observable`, `openSettings` and `NSApp.activate()`, which need macOS 14. macOS 13 would need `ObservableObject` and workarounds for opening Settings from a menu bar app; it is no longer receiving security updates, so it is not supported.
- Xcode 16 or later (Swift 6).
- The speaker and the Mac on the same local network. macOS asks for Local Network access on first use; allow it.

No third-party dependencies. CryptoKit and CommonCrypto provide the `HMAC_SHA256_AES256` scheme.

## Build and run

```bash
cd macos
swift test                    # core, KEF driver, shared vectors
scripts/build-app.sh          # build/Remote for KEF.app, signed ad hoc
open "build/Remote for KEF.app"
```

`scripts/build-app.sh --universal` builds for Apple silicon and Intel. You can also open `Package.swift` in Xcode and run the `KEFRemote` scheme. Run as a bare executable, launch at login is unavailable because it needs an app bundle.

## Layout

| Target | Platform | Contents |
|---|---|---|
| `KefRemoteCore` | macOS, Linux | `SpeakerDriver` contract, `SpeakerController` (safety policy), `VolumePolicy` |
| `KefW2Driver` | macOS, Linux | KEF W2 HTTP API, auth, strict parsing, model table |
| `KEFRemote` | macOS | SwiftUI `MenuBarExtra` app |

The core and driver also build on Linux (with OpenSSL) so their tests can run in CI without a Mac. `SharedVectorTests` read `spec/test-vectors/kef-w2.json`, the same file the Windows tests use.

## Status

| Item | Status |
|---|---|
| Core and driver tests (47) | Pass on Linux, Swift 6.1 |
| Crypto against shared vectors | Pass on Linux (OpenSSL path). The CommonCrypto path runs the same tests on a Mac. |
| macOS app | Not yet built or run on a Mac |
| LSX II LT on real hardware | Not yet tested with this app. The protocol was verified with the Windows prototype. |
| Other KEF models | Unverified, shown as such in the app |
| Power (standby/wake) | Supported by the controller, not offered by the KEF driver until verified |
