<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/banner-en-dark.png">
    <img src="docs/images/banner-en-light.png" alt="Remote for KEF" width="100%">
  </picture>
</p>

<p align="center">English · <a href="README_ko.md">한국어</a> · <a href="README_jp.md">日本語</a></p>

<p align="center">
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-win-x64.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-windows-dark.svg"><img src="docs/images/download-windows-light.svg" height="44" alt="Download for Windows"></picture></a>
  &nbsp;
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-macos.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-macos-dark.svg"><img src="docs/images/download-macos-light.svg" height="44" alt="Download for macOS"></picture></a>
</p>

Switching inputs, waking the speaker or nudging the volume should not mean reaching for your phone. **Remote for KEF** sits in the Windows tray and the Mac menu bar and controls your KEF wireless speaker directly over the local network.

- **Power and inputs**: Wake the speaker or put it in standby, and switch between USB-C, Wi-Fi, Bluetooth, TV and Optical
- **Exact volume**: 0 to 100 in steps of 1, always the value the speaker reports back
- **Volume limit**: Stops at your own limit or the maximum set in KEF Connect, whichever is lower
- **Accurate volume**: Optionally reaches every level from above, so the same number always sounds equally loud
- **Shortcuts on Windows**: Volume up, down and mute from anywhere, with an on-screen volume display
- **Speaker finish**: Draws the speaker icon in your speaker's color

How each of these works is in [Features](docs/features.md).

## Requirements

| Item     | Windows                 | macOS                             |
| -------- | ----------------------- | --------------------------------- |
| OS       | Windows 10 (1809) or 11 | macOS 14 or later                 |
| Hardware | x64, ARM64              | Apple silicon and Intel (Universal) |

| Speaker                                  | Status                        |
| ---------------------------------------- | ----------------------------- |
| KEF LSX II LT                            | Tested                        |
| KEF LSX II, LS50 Wireless II, LS60, XIO  | Same protocol, not yet tested |

The interface is in English, Korean and Japanese and follows your system language.

## Install

Builds are not code-signed or notarized, so each system asks once before the first launch.

**Windows**

1. Download `RemoteForKEF-win-x64.zip` (`RemoteForKEF-win-arm64.zip` for ARM PCs) from [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases) and unzip it to a folder you keep, such as `C:\Apps\RemoteForKEF`.
2. Run `RemoteForKEF.exe`. If SmartScreen says "Windows protected your PC", choose **More info → Run anyway**.
3. Enter the speaker's IP address. KEF Connect shows it under the speaker's details.
4. Windows 11 hides new tray icons under **^**. To keep it visible, turn it on in **Settings → Personalization → Taskbar → Other system tray icons**.

**macOS**

1. Download `RemoteForKEF-macos.zip` from [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases), unzip it and move **Remote for KEF.app** to Applications.
2. Open it. When macOS says it cannot verify the developer, go to **System Settings → Privacy & Security** and click **Open Anyway**.
3. Allow **Local Network** access when asked. Without it the speaker cannot be reached.
4. Click the speaker in the menu bar, then **Settings** to enter the IP address.

## Usage

Click the tray or menu bar icon to see power, input and volume, and change them right there. On Windows, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd> changes the volume and <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>M</kbd> mutes; these overlap with some IDE shortcuts and can be changed in **Settings → Shortcuts**.

## Acknowledgements

- [kef-lsx-volume](https://github.com/roberdorfer/kef-lsx-volume) by Robert Oberdorfer. Accurate volume and the volume steps on Windows are built on the volume curve measurements published there. Thank you for sharing them.
- [homeassistant-kef](https://github.com/EvotecIT/homeassistant-kef), the reference for the KEF network protocol and its authentication.

## Contributing

Building from source, the driver contract and how to add another speaker are in [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Remote for KEF is available under the [MIT License](LICENSE). See also [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Not affiliated with or endorsed by KEF. KEF and product names are trademarks of their owners.
