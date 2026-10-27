# KEF Remote

Unofficial desktop remote for KEF wireless speakers, built so other network players can be added. Windows first, macOS next.

[한국어](README.ko.md)

> Not affiliated with or endorsed by KEF. "KEF" and product names are trademarks of their owners and are used only to describe compatibility.

## Why

Switching a KEF LSX II LT between the PC (USB-C) and Spotify, or nudging its volume, should not require picking up a phone. This app sits in the tray (Windows) or menu bar (macOS) and controls the speaker's own volume and input over the local network.

It deliberately does **not** sync with the Windows master volume. The speaker's volume is controlled directly.

## Features

- Speaker name, address, connection state, current input
- Input switching (e.g. USB-C / PC and Wi-Fi), done safely: mute, switch, verify, unmute only if the new input is not louder
- Volume slider and mute, always showing the speaker's real value
- App volume limit (default 30). Raises are capped; lowering always works. The speaker's own limit is respected too
- Tray app: closing the window keeps it running; optional quiet start after Windows sign-in
- Never writes anything on startup or reconnect. Nothing is replayed after a network drop

Planned (in progress): global hotkeys with an on-screen volume indicator (Windows), menu bar volume slider (macOS).

## Supported players

| Player | Status |
|---|---|
| KEF LSX II LT | Verified on hardware (firmware 2.1.86) |
| KEF LSX II, LS50 Wireless II, LS60, XIO | Same W2 protocol, not yet verified |

Other players (BluOS, WiiM/LinkPlay, Yamaha MusicCast, ...) are not included because they could not be tested. The app talks to devices through a small driver contract, so adding one is a single class plus tests. If you own one, see [docs/drivers.md](docs/drivers.md) for the contract and the safe hardware checks, and send a pull request.

Owners of the unverified KEF models: please try it and open an issue with the result.

## Build

Windows, .NET 10 SDK:

```powershell
dotnet build windows/KefRemote.slnx -c Release
dotnet test windows/KefRemote.slnx
dotnet run --project windows/src/KefRemote.Windows
```

`KefRemote.exe --background` starts in the tray without a window. Settings live in `%LOCALAPPDATA%\KefRemote\settings.json`; the speaker password, if any, is protected with Windows DPAPI for the current user.

## Repository layout

```text
windows/   C# .NET 10: Core (controller, driver contract), Drivers, WinForms app, tests
macos/     Swift package for the menu bar app
docs/      protocol notes and the driver contract
spec/      language-neutral test vectors shared by both apps
```

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
