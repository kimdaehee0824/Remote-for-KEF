# Remote for KEF

**A Very Revolutionary Way to Control Your KEF Speakers.**

Unofficial desktop remote for KEF wireless speakers on Windows and macOS, built so other network players can be added.

[한국어](README.ko.md)

> Not affiliated with or endorsed by KEF. "KEF" and product names are trademarks of their owners and are used only to describe compatibility.

## Why

Switching a KEF LSX II LT between the PC (USB-C) and Spotify, turning it on, or nudging its volume should not require picking up a phone. This app sits in the tray (Windows) or menu bar (macOS) and controls the speaker itself over the local network: one click to see its state, one more to change it.

It deliberately does **not** sync with the Windows or Mac system volume. The speaker's own volume is controlled directly.

## Features

- **Power**: a switch next to the speaker name puts it in standby or wakes it on the last input. In standby, picking an input wakes the speaker on that input.
- **State at a glance**: the switch shows power, the selected input shows the input, the number shows the volume. The line under the name only changes when something is unusual (standby, switching, offline).
- **Inputs** (USB-C / PC, Wi-Fi, Bluetooth, TV, Optical on the LSX II LT): switched safely. Mute, switch, verify, then unmute only if the new input is not louder.
- **Exact volume, 0 to 100 in steps of 1**, always the value the speaker reports back.
- **Volume limit**: the app limit (default 30) and the maximum volume set in KEF Connect, whichever is lower. The limit is marked on the slider with where it comes from. Raises stop there; lowering always works.
- **Exact volume matching** (off by default): KEF speakers can sound quieter at the same number when it is reached from below. When on, raises pass briefly through a slightly higher value (never above the limit) so every level sounds as if it was reached from above. Explained in the app's (?) / (i) help.
- **Windows**: WinUI 3 tray flyout, global hotkeys for volume up/down and mute (default Ctrl+Alt+↑/↓/M, changeable) with a volume display in the bottom-right corner, settings window.
- **macOS**: menu bar panel with the system slider and switch, haptic ticks every 5 while dragging on a Force Touch trackpad, Liquid Glass buttons on macOS 26 and later, settings window with tabs.
- **Safe by default**: nothing is written on startup, reconnect or polling, every write is read back, and nothing is replayed after a network drop. See [docs/drivers.md](docs/drivers.md).

## Download and first run

Builds are not signed with a paid certificate, so each system asks once before the first launch.

**Windows 10 (1809) or 11**, `RemoteForKEF-win-x64.zip` (or `-arm64` for ARM PCs):

1. Unzip to a folder you keep, for example `C:\Apps\RemoteForKEF`. Starting with Windows points at this folder, so not Downloads.
2. Run `RemoteForKEF.exe`. If SmartScreen says "Windows protected your PC", choose **More info → Run anyway**.
3. Enter the speaker's IP address in the settings window that opens. It is shown in KEF Connect under the speaker's details.
4. Windows 11 hides new tray icons in the overflow (^) menu. To keep it visible: **Settings → Personalization → Taskbar → Other system tray icons → Remote for KEF**.

No installer and no .NET install needed; the app is self-contained. Settings live in `%LOCALAPPDATA%\KefRemote`; the speaker password, if any, is protected with Windows DPAPI for the current user.

**macOS 14 or later**, `RemoteForKEF-macos.zip` (Apple silicon and Intel):

1. Unzip and move **Remote for KEF.app** to Applications.
2. Open it. macOS says it cannot verify the developer. Open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to Remote for KEF. On macOS 14 you can also Control-click the app and choose **Open**.
3. Allow **Local Network** access when asked; without it the speaker cannot be reached.
4. Click the speaker icon in the menu bar, then **설정** to enter the IP address.

## Windows hotkeys

Registered system-wide while the app runs, so they win over the app in front. The defaults overlap with:

| Keys | Also used by |
|---|---|
| Ctrl+Alt+↑ / ↓ | VS Code (add cursor above/below), JetBrains IDEs (previous/next occurrence), Visual Studio (Ctrl+Alt+↓, open files list), Intel graphics screen rotation when its hotkeys are enabled |
| Ctrl+Alt+M | JetBrains IDEs (extract method), Word (insert comment) |

If you use those often, pick other keys in **Settings → 단축키**. A combination another app registered first is shown there as unavailable.

## Supported players

| Player | Status |
|---|---|
| KEF LSX II LT | Volume, mute and inputs verified on hardware (firmware 2.1.86). Power control follows the same protocol; verification on hardware is pending |
| KEF LSX II, LS50 Wireless II, LS60, XIO | Same W2 protocol, not yet verified |

Other players (BluOS, WiiM/LinkPlay, Yamaha MusicCast, ...) are not included because they could not be tested. The app talks to devices through a small driver contract, so adding one is a single class plus tests. If you own one, see [docs/drivers.md](docs/drivers.md) for the contract and the safe hardware checks, and send a pull request.

Owners of the unverified KEF models: please try it and open an issue with the result.

## Build

**Windows**, .NET 10 SDK (Windows App SDK comes from NuGet):

```powershell
dotnet test windows/tests/KefRemote.Tests/KefRemote.Tests.csproj
dotnet publish windows/src/KefRemote.App/KefRemote.App.csproj -c Release -r win-x64 -p:Platform=x64 -o dist/win-x64
dist/win-x64/RemoteForKEF.exe
```

The UI is built in C# without XAML pages (styles are in `Styles.xaml`), so the code also compiles on Linux and macOS for checking; the app itself runs only on Windows.

`RemoteForKEF.exe --background` starts in the tray without opening anything (used by "start with Windows"). `RemoteForKEF.exe --self-test <folder>` runs every screen against a simulated speaker, saves screenshots and `report.txt`, and exits with 0 only if every step passed. CI runs it on each push.

**macOS**: see [macos/README.md](macos/README.md).

## Repository layout

```text
windows/   C# .NET 10: Core (controller, driver contract), Drivers, Desktop (shared UI logic), App (WinUI 3), tests
macos/     Swift package: core, KEF driver, SwiftUI menu bar app
docs/      protocol notes and the driver contract
spec/      language-neutral test vectors shared by both apps
```

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
