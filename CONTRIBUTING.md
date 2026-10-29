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
