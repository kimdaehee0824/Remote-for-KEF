# KEF W2 local HTTP API

This is the protocol used by KEF's W2 platform speakers (LSX II, LSX II LT, LS50 Wireless II, LS60 Wireless, XIO, LS LUXE) on the local network. KEF does not publish it as a supported API. Everything below was either observed on a real **LSX II LT (firmware 2.1.86, `LSXIILT_V2186`)** or taken from public reference code, and each item says which.

Both apps in this repository implement this document. The shared test vectors are in [`spec/test-vectors/kef-w2.json`](../../spec/test-vectors/kef-w2.json); the Windows and macOS test suites both read that file.

## Transport

- Plain HTTP on TCP 80. Endpoints: `GET /api/getData`, `POST /api/setData`.
- Only private IPv4 addresses are accepted by the apps (10/8, 172.16/12, 192.168/16, 169.254/16, 127/8).
- No proxy, no redirects, a short timeout (about 1.5 s), bounded response size.

## Reading

```http
GET http://192.168.0.14/api/getData?path=player%3Avolume&roles=value
```

```json
[{"type":"i32_","i32_":32}]
```

`path` is percent-encoded (`Uri.EscapeDataString` / RFC 3986 unreserved set) and `roles=value` is always sent.

A response is a one-element array holding a typed object `{"type": T, T: value}`. Parsers must:

- reject arrays whose length is not 1,
- reject objects carrying a non-null, non-false `error`,
- check the JSON kind (a volume of `"100"` as a string is an error, not 100),
- treat an HTML body (`<...`) as a login page and stop.

| Purpose | path | type / values | Source |
|---|---|---|---|
| Device name | `settings:/deviceName` | `string_` | observed |
| Model code | `settings:/kef/host/modelName` | `string_`, LSX II LT returns `SP4077` | observed |
| Release text | `settings:/releasetext` | `string_`, e.g. `LSXIILT_V2186` | observed |
| Auth policy | `settings:/webserver/authMode` | `webserverAuthMode`: `none`, `setData`, `all` | observed `setData` |
| Input | `settings:/kef/play/physicalSource` | `kefPhysicalSource`: `usb`, `wifi`, `bluetooth`, `tv`, `optical`, `coaxial`, `analog`, `standby`, `powerOn` | observed `usb`, `wifi`, `standby`, `powerOn` |
| Volume | `player:volume` | `i32_`, 0-100 | observed |
| Mute | `settings:/mediaPlayer/mute` | `bool_` | observed |
| Power | `settings:/kef/host/speakerStatus` | `kefSpeakerStatus`: `powerOn`, `standby` | observed `powerOn`, `standby` |
| Device limit enabled | `settings:/kef/host/volumeLimit` | `bool_` | observed `true` |
| Device max volume | `settings:/kef/host/maximumVolume` | `i32_` | observed `70` |

### Model detection

`modelName` is not a product name but the first six characters of the KEF part number (LSX II LT reports `SP4077`; KEF sells it as `SP4077J2CA` in Sage Green). The finish is in the rest of the part number, so the API does not report it. Seen on real speakers: `SP4077` (LSX II LT), `SP4041` (LSX II), `SP4083` (XIO). From KEF's part numbers, not yet seen: `SP4025` (LS50 Wireless II), `SP4017` (LS60 Wireless), `SP4042` (LS LUXE). Use the prefix of `releasetext` before `_V` as well. Known identifiers and aliases: `LSXIILT` (`LSX2LT`), `LSXII` (`LSX2`), `LS50W2` (`LS50WII`), `LS60` (`LS60W`), `XIO`, `LSLUXE`. Per-model input lists follow EvotecIT/homeassistant-kef `const.py`, and KEF's specifications for LS LUXE.

### Consistent reads

Read input, volume, mute and power, then read input a second time. If the two input values differ the device is mid-switch; discard the snapshot.

## Writing

Logical payload, before protection:

```json
{"path":"player:volume","role":"value","value":{"type":"i32_","i32_":29}}
```

Other values: `{"type":"bool_","bool_":true}` for mute, `{"type":"kefPhysicalSource","kefPhysicalSource":"usb"}` for input.

With `authMode` `none` this body is posted as is. With `setData` or `all` the value is encrypted and the request is signed.

## HMAC_SHA256_AES256 (verified on LSX II LT, empty web page password)

1. `salt` = 6 random bytes, `saltB64 = Base64(salt)`.
2. `key = SHA256(salt || UTF8(password))`. The password is the speaker's web page password, not the KEF account password. Empty is a valid value but must not be assumed.
3. Serialize the typed value object as compact JSON (no whitespace). The body that carries the envelope keeps `+` and `/` as they are, not escaped as `\u002B`.
4. `iv` = 16 random bytes. AES-256-CBC with PKCS#7 padding.
5. `envelope = Base64(iv || ciphertext)`.
6. Body actually sent: `{"path":"...","role":"value","value":"<envelope>"}` (compact JSON, keys in that order).
7. `timestamp` = Unix time in milliseconds, UTC, as a decimal string.
8. Signed message:
   - POST: `user.<saltB64>.<timestamp>.<fullUrl>.<actualBody>`
   - GET: `user.<saltB64>.<timestamp>.<fullUrl>` (full URL including the query)
9. `signature = Base64(HMAC-SHA256(key, UTF8(message)))`.
10. Header: `Authorization: HMAC_SHA256_AES256 dXNlcg==.<saltB64>.<timestamp>.<signature>` (`dXNlcg==` is Base64 of `user`).

The signature covers the exact bytes sent, not a re-serialization. GET requests are signed only in `all` mode, which was not tested on hardware.

If `authMode` cannot be read, or the device answers 401/403, an HTML page, or an unknown mode, stop and ask for the password. Never fall back to `none`.

## Behaviour observed on LSX II LT

- Each input keeps its own volume. Switching from `usb` to `wifi` can make the speaker louder, so the apps switch only while muted.
- Mute survives input switches.
- Volume changed between two reads without action from this app (another controller). Always re-read before acting.
- A `usb` reading confirms the speaker's input only. It says nothing about the PC's audio output device.
- `physicalSource` changes before `speakerStatus` in both directions (firmware 2.1.86, read every 150 ms). Waking, the input comes back about 1.8 s before `speakerStatus` turns `powerOn`. Turning off, `physicalSource` reads `standby` about 0.4 s before `speakerStatus` does. The apps therefore take `physicalSource` `standby` as standby, and an input while `speakerStatus` is still `standby` as waking: the input is shown at once and commands wait for `powerOn`.
- With `settings:/kef/host/wakeUpSource` at `wakeup_default` the speaker woke on the input it had before standby.
- After waking with `powerOn`, `physicalSource` can keep reading `powerOn` while `speakerStatus` reads `powerOn` (firmware 2.1.86: still so several minutes later, until the input changed to `usb` and then `wifi`; what ended it wasn't recorded, and whether the speaker was playing meanwhile wasn't checked). The apps confirm a wake by `speakerStatus` alone and show such an input as the last one they saw while on (kept per speaker address across launches). After a wake of their own they then write that input with the usual muted switch, so the speaker is surely on it and names it; picking the shown input does the same. Polls only read. Before any input was seen, the input stays unnamed and the panel says so until one is picked.
- Waking can take much longer than those 2 s: the speaker was seen coming on only after the apps had stopped waiting (about 10 s on Windows, 6 s on macOS). The apps now read every 0.5 s for the first 5 s, then every second, for about 30 s. A speaker that reads fine but is not on by then is reported as still turning on, not as a failure, and the message goes as soon as a later read finds it on.

## Change reports (event queue)

Observed on LSX II LT 2.1.86, and used the same way by EvotecIT/homeassistant-kef. These endpoints are never signed, in any `authMode`.

1. Subscribe: `POST /api/event/modifyQueue` with
   ```json
   {"subscribe":[{"path":"player:volume","type":"itemWithValue"},{"path":"settings:/mediaPlayer/mute","type":"itemWithValue"}],"unsubscribe":[]}
   ```
   The reply is a JSON string holding the queue id, braces included: `"{2b6f…}"`.
2. Wait: `GET /api/event/pollQueue?queueId=%7B2b6f…%7D&timeout=10`. The speaker holds the request for up to `timeout` seconds and answers as soon as something changes, so the client's own timeout must be longer (the apps allow 3 s more). The reply is an array, empty when nothing changed:
   ```json
   [{"path":"player:volume","itemType":"update","itemValue":{"type":"i32_","i32_":31},"rowsEvents":[]}]
   ```
3. Ask again with the same id. After any error (timeout, HTTP error, a reply that isn't an array) the id is dropped and step 1 starts over.

The apps subscribe to input, power, volume, mute, `player:player/data`, the two device limit settings and the device name. `player:player/data/playTime` is left out: while a track plays it changes about every 650 ms.

A report only tells the apps to read. Volume and mute values are compared with what the app already shows, so the echo of the app's own verified write costs nothing; anything else is read again with the normal consistent read. Reports that arrive while a command runs are kept and looked at after it. While reports arrive, the regular reads slow to a heartbeat (3 s with the flyout or panel open, 10 s otherwise). When the queue fails, the apps read at once, go back to regular reads and try to subscribe again after 2, 5, 10, 30 and then every 60 s.

## Now playing (observed on LSX II LT with Spotify Connect)

| Purpose | path | type / values |
|---|---|---|
| Player state | `player:player/data` | untyped object in a one-element array, see below |
| Position | `player:player/data/playTime` | `i64_`, milliseconds; `-1` when there is none |
| Control | `player:player/control` | write with role `activate` |

Fields of `player:player/data` the apps use: `state` (`playing`, `paused`, `stopped`), `trackRoles.title`, `trackRoles.icon` (cover URL), `trackRoles.mediaData.metaData.artist` / `albumArtist` / `album` / `serviceID` (e.g. `spotify`, `airplay`, `googlecast`), `status.duration` (milliseconds), and `controls.pause`, `controls.next_`, `controls.previous` (whether the service allows them).

Physical inputs report their own name as the title (`"usb"`) and no controls, so the apps show playback only on `wifi` and `bluetooth`.

Control is a normal `setData` with role `activate` instead of `value`, encrypted and signed like any write in `setData` mode:

```json
{"path":"player:player/control","role":"activate","value":{"control":"pause"}}
```

`pause` toggles between play and pause; `next` and `previous` skip. A bare `{"control":"play"}` must not be sent: on Spotify Connect it ends the session instead of resuming.

## Not used

- The separate account channel on port 4430 (see hilli/go-kef-w2 `auth.go`).

## Used, not yet tested on hardware

- Power on/off through `physicalSource` (`powerOn` / `standby`), the same write KEF Connect and the community libraries use. `powerOn` should wake the speaker on the input it had before standby. The apps confirm the result through `speakerStatus` and `physicalSource`, tolerating read errors while the speaker changes state.
