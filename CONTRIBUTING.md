# Contributing

## Build

**Windows**, .NET 10 SDK (Windows App SDK comes from NuGet):

```powershell
dotnet test windows/tests/KefRemote.Tests/KefRemote.Tests.csproj
dotnet publish windows/src/KefRemote.App/KefRemote.App.csproj -c Release -r win-x64 -p:Platform=x64 -o dist/win-x64
dist/win-x64/RemoteForKEF.exe
```

The UI is built in C# without XAML pages (styles are in `Styles.xaml`), so the code also compiles on Linux and macOS for checking; the app itself runs only on Windows.

**macOS**, Xcode 16 or later (Xcode 26 adds the Liquid Glass buttons):

```bash
cd macos
swift test
scripts/build-app.sh            # build/Remote for KEF.app, signed ad hoc
open "build/Remote for KEF.app"
```

More in [macos/README.md](macos/README.md).

## Launch options

| Option | Windows | macOS |
| --- | --- | --- |
| `--background` | Starts in the tray without opening anything (used by "start with Windows") | |
| `--self-test <folder>` | Walks every screen against a simulated speaker, saves PNGs and `report.txt`, exits with 0 only if every step passed | Same |
| `--demo` | Runs against a simulated speaker with the flyout open | Runs against a simulated speaker |
| `--lang ko\|en\|ja` | Picks the interface language instead of the system's | Same |
| `--appearance light\|dark` | | With `--demo`, picks light or dark for the app only. Liquid Glass still follows the system appearance |

CI runs the self-test on every push. `--demo` with `--lang` is handy for screenshots in each language.

## Releases

Every change on main goes out as a release.

1. Raise the version: `Version` in `windows/Directory.Build.props`, `CFBundleShortVersionString` in `macos/Resources/Info.plist`, and `CFBundleVersion` next to it by one (Sparkle compares that number).
2. Commit as `version :: x.y.z`, push, then push the tag: `git tag vx.y.z && git push origin vx.y.z`.
3. The Release workflow builds both apps and attaches them to a draft release for the tag. Write the notes and publish it. Installed copies only see published releases.

| Asset | |
| --- | --- |
| `RemoteForKEF-win-x64-Setup.exe`, `RemoteForKEF-win-arm64-Setup.exe` | Installs for the current user in `%LocalAppData%\RemoteForKEF`, no admin rights, with a Start menu shortcut |
| `RemoteForKEF-win-x64.zip`, `RemoteForKEF-win-arm64.zip` | Portable: unzip anywhere and run `Remote for KEF.exe`. Updates itself in that folder |
| `releases.win-x64.json`, `releases.win-arm64.json`, `RemoteForKEF-x.y.z-win-*-full.nupkg` | Windows update feed (Velopack), one channel per architecture |
| `RemoteForKEF-macos.zip` | The Mac app, universal |
| `appcast.xml` | Mac update feed (Sparkle), signed with the EdDSA key |
| `SHA256SUMS.txt` | Checksums of the downloads |

Both apps look for a new release once a day and on request in Settings. The check is a plain request to GitHub; Sparkle's system profile stays off.

### Repository secrets

Set them with `gh secret set NAME < file` (or Settings > Secrets and variables > Actions).

| Secret | |
| --- | --- |
| `SPARKLE_ED_PRIVATE_KEY` | The Sparkle key as `generate_keys -x` exports it; its public half is `SUPublicEDKey` in Info.plist. Installed Mac copies only accept updates signed with it, so keep a backup. Without it a release has no `appcast.xml` and Mac copies don't see it; once it is added, run the Release workflow for that tag with "appcast only" |
| `DEVELOPER_ID_IDENTITY` | Optional. `Developer ID Application: Name (TEAMID)`. When set, the Mac app is signed with it and notarized; when not, it is signed ad hoc |
| `DEVELOPER_ID_CERT_P12`, `DEVELOPER_ID_CERT_PASSWORD` | With the identity: the certificate and its private key as base64 of a .p12, and its password |
| `NOTARY_KEY_P8`, `NOTARY_KEY_ID`, `NOTARY_ISSUER_ID` | With the identity: an App Store Connect API key for notarization |

The Windows downloads are not code-signed yet.

## Adding a speaker

The apps talk to a player through a small driver contract, so adding one is a single class plus tests. [docs/drivers.md](docs/drivers.md) describes the contract and the safe checks to run on real hardware. Players that could not be tested on hardware are not included, so please send a driver only for a device you own.

Owners of the KEF models marked as not yet tested: please try the app and open an issue with the result.

## Repository layout

```text
windows/   C# .NET 10: Core (controller, driver contract), Drivers, Desktop (shared UI logic), App (WinUI 3), tests
macos/     Swift package: core, KEF driver, SwiftUI menu bar app
docs/      protocol notes and the driver contract
spec/      language-neutral test vectors shared by both apps
```
