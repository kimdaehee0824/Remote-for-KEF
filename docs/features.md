# Features

English · [한국어](features_ko.md) · [日本語](features_jp.md)

Remote for KEF talks to the speaker directly over your local network. Connecting and reconnecting only read its status; nothing changes on the speaker until you change it.

## Power and inputs

- Turn the speaker off to standby, or turn it on with the last input or with the input you pick.
- Before turning on, the app checks that the volume is within your limit. If the volume before standby is above it, the speaker stays off and the app says why.
- Each input can have its own volume, so the speaker is muted while switching. When the switch is done, the app unmutes only if the new input's volume is not higher than before and within the limit; otherwise it stays muted and the app says why.
- Some models can't be turned on over the network. The app says so, and you turn them on with the remote.

## Volume

- 0 to 100 in steps of 1. The number shown is always the value the speaker reports back.
- Mute and unmute from the flyout or the panel.

## Volume limit

There are two limits, and the lower one applies:

- **App limit**: the highest volume this app raises to.
- **KEF device maximum**: the maximum set in KEF Connect.

The limit in effect is the tick on the volume slider. Lowering always works, and volume changed with the remote or KEF Connect isn't limited. If the volume is already above the limit, you can only lower it.

## Accurate volume

In some ranges a KEF speaker sounds quieter at the same number when the volume comes up from below. With **Accurate volume** on, raising the volume first sends a slightly higher value and then the target 0.1 seconds later, so it sounds as loud as coming down to the target from above.

- Raising to 20 goes through 22, and raising to 12 goes through 17.
- Values of 5 or less and 41 or more are sent directly.
- The value in between never goes past the limit, and lowering is sent as is.

The table comes from measurements published by [kef-lsx-volume](https://github.com/roberdorfer/kef-lsx-volume), taken on an LSX II (USB input). It hasn't been checked on the LSX II LT yet, so it is off by default.

## Windows

- **Shortcuts**: <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd> for volume and <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>M</kbd> for mute, from any app. VS Code, JetBrains IDEs, Visual Studio and Word use some of these too, so they can be changed in **Settings → Shortcuts**.
- **Volume per press**: 1, 2, 3 or 5, or **Perceived loudness**, which follows measured steps (12, 15, 19, 22, 25, 29 …) so the loudness rises evenly. Above 65 it moves by 4; below 12, raising goes to 12 and lowering goes to 0.
- **Volume indicator**: shows in the bottom-right corner when the volume changes. The mouse wheel over it changes the volume too.
- **Start with Windows**: starts quietly in the notification area when you sign in.
- The device web page password is stored encrypted for your Windows user.

## macOS

- The menu bar icon's shape tells on, muted, standby and offline apart, and can show the volume number next to it.
- **Open at login** from Settings.
- The device web page password is stored in the keychain.
- macOS asks for **Local Network** access on the first launch. Without it the speaker can't be reached.

## Speaker finish

Speakers don't report their color, so you pick the finish in Settings and the app draws the speaker in it. The default is LS60 Royal Blue.
