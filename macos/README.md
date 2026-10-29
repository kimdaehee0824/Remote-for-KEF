# Remote for KEF (macOS)

A menu bar app for KEF W2 speakers. It turns the speaker on and off, changes the speaker's own volume (not the Mac's), mutes and switches inputs, from a panel built from system components.

- **Menu bar**: a speaker with one Uni-Q driver (not the system Sound symbol, so the two volumes are never confused) and the volume in a slot two digits wide. Filled, muted, outlined (standby) and crossed out (offline) are told apart by shape; the icon is a template image the system tints.
- **Panel**: name and model with the power switch on the right, the volume number and system slider with the limit marked in orange, and the input list with a check. While an input switch runs, a spinner sits on that row and the others dim. In standby the list wakes the speaker on the input you pick, with the last one labelled.
- **Haptics**: a tick every 5 while dragging the slider on a Force Touch trackpad, and a different one at the limit. Nothing on a mouse or with the arrow keys.
- **Glass**: Settings and Quit are `.buttonStyle(.glass)` buttons in a `GlassEffectContainer` on macOS 26 and later, `.bordered` on 14 and 15. Everything else is a system component and picks up Liquid Glass from the system. They carry ⌘, and ⌘Q, since a menu bar app has no app menu.
- **Settings**: 일반 (launch at login, number in the menu bar), 스피커 (IP address, speaker webpage password in the Keychain, connection state, device details), 음량 (app limit as slider and field, KEF maximum volume, the limit in effect, 정확한 음량 맞추기 with an (i) popover).

Safety rules are shared with the Windows app and described in [docs/drivers.md](../docs/drivers.md). Connecting, reconnecting and polling only read; nothing is written without a user action.

## Requirements

- macOS 14 Sonoma or later. The app uses `@Observable`, `openSettings` and `sensoryFeedback`, which need macOS 14.
- Xcode 16 or later, or the matching Command Line Tools (Swift 6). Building with Xcode 26 (Swift 6.2) adds the glass buttons; older toolchains build the bordered ones.
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

**App icon**: `Resources/AppIcon.icon` is the Icon Composer file (the same speaker as the Windows icon, in LS60 Royal Blue). `build-app.sh` compiles it with `actool` (Xcode 26) into the bundle; without Xcode the app builds with the generic icon and says so, and CI fails in that case.

**Self-test**: `"build/Remote for KEF.app/Contents/MacOS/KEFRemote" --self-test <folder>` drives the real panel and settings against a simulated LSX II LT, saves a PNG of each state and `report.txt`, and exits with 0 only if every step passed. CI runs it on each push. The captures are drawn offscreen without screen recording permission, so system materials such as Liquid Glass do not appear in them.

**Language**: the UI is in Korean, English or Japanese, the first of them in the system's preferred languages (English when none is listed). `--lang ko|en|ja` picks one at launch, also together with `--self-test`; CI runs the self-test in all three.

## Layout

| Target | Platform | Contents |
|---|---|---|
| `KefRemoteCore` | macOS, Linux | `SpeakerDriver` contract, `SpeakerController` (safety policy), `VolumePolicy` |
| `KefW2Driver` | macOS, Linux | KEF W2 HTTP API, auth, strict parsing, model table, overshoot table |
| `KEFRemote` | macOS | SwiftUI `MenuBarExtra` app |

The core and driver also build on Linux (with OpenSSL) so their tests can run in CI without a Mac. `SharedVectorTests` read `spec/test-vectors/kef-w2.json`, the same file the Windows tests use.

## Status

| Item | Status |
|---|---|
| Core and driver tests | Pass on macOS (CI, Xcode 26) and Linux (Swift 6.1) |
| App build | Universal app built and self-tested on CI (macOS 26) |
| LSX II LT on real hardware | Volume, mute and inputs verified with the Windows app on the same protocol. This app and power control are not yet tested on the speaker |
| Other KEF models | Unverified, shown as such in the app |
