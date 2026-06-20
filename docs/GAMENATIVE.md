# Running with Fallout 4 inside GameNative

[GameNative](https://github.com/AshParse/GameNative) is a Box64+Wine wrapper
for Android, the spiritual sibling of Winlator and Mobox. Fallout 4 has
been observed running on Snapdragon 8 Gen 3 / 8 Elite class chips at
720p/30 with DXVK + a sensible texture mod load order.

This doc covers the parts that matter for the companion protocol.

## 1. Enable the companion server in-game

Inside the Wine prefix, edit `Documents/My Games/Fallout4/Fallout4Custom.ini`:

```ini
[Companion]
bCompanionEnabled=1
bCompanionAutoStart=1
iCompanionPort=27000
iCompanionDiscoveryPort=28000
```

You can find this file from Android via GameNative's "Documents" view or
by browsing the Wine prefix at:

```
/sdcard/Android/data/app.gamenative/files/imagefs/home/<user>/.wine/drive_c/users/<user>/My Documents/My Games/Fallout4/
```

(Exact path varies with GameNative version; the imagefs root is the
authoritative anchor.)

## 2. Networking — why loopback "just works"

Wine on Linux/Android doesn't sandbox sockets. A Windows `WSASocket` call
becomes a `socket(AF_INET, ...)` syscall in the host kernel, with the
same process credentials. From the kernel's POV, Fallout4.exe is just
another Linux process inside the GameNative container, and its sockets
go on the **shared Android network stack**.

So:

- The game binding `0.0.0.0:27000` is visible at `127.0.0.1:27000` from
  the host Android side.
- An Android app calling `connect("127.0.0.1", 27000)` will hit the game.
- The reverse direction (game initiating to Android app) is not used by
  this protocol — the client always connects.

## 3. The UDP discovery gotcha (and why we skip it)

UDP broadcast (`255.255.255.255` or subnet broadcast) requires a non-loopback
interface that supports `IFF_BROADCAST`. GameNative's networking modes:

| Mode          | Broadcast OK? | Loopback OK? |
|---------------|---------------|--------------|
| Native Linux  | yes (lo not eligible) | yes  |
| TUN bridge    | yes (tun0)    | yes          |
| WireGuard mode| varies        | yes          |

In all modes loopback is reliable. So Strip-Boy adds a loopback UDP send
alongside the original broadcast — the game's listener picks both up,
and the device list contains `127.0.0.1` regardless of WiFi state.

If the user *does* want to connect from a different device on the LAN —
e.g. a phone next to the AYN Thor — they can flip to LAN mode in app
settings. That uses GameNative's network mode as-is.

## 4. Performance notes

- The companion server is the cheapest possible bit of game work — it
  sends a full dump on connect (~50–200 KB) and then ~250 ms delta
  updates of small JSON-ish trees. Negligible impact on game frame time.
- Don't run Strip-Boy in a foreground window over Fallout 4. Use
  Android's split-screen if you must see both, or pair the AYN Thor's
  HDMI-out for game and the OLED for companion.
- AYN Thor's gamepad input goes through GameNative's `xinput` shim for
  the game. The companion app uses Android's `KeyEvent` / `MotionEvent`
  gamepad path, which is independent — they don't fight for input.

## 5. Building + installing the patched APK

From this repo, with your AYN Thor connected over USB:

```bash
# Build the patched APK (one-time: ~5s for patcher build, then ~2s)
./scripts/build.sh

# Install
adb devices
adb install -r apk/out/pipboy-loopback.apk
```

If `com.bethsoft.falloutcompanionapp` is already installed (Bethesda's original), `adb`
will refuse to overlay a debug-signed APK over a production signature.
Uninstall the original first:

```bash
adb uninstall com.bethsoft.falloutcompanionapp
adb install apk/out/pipboy-loopback.apk
```

Or sideload via the Android Files app from `apk/out/pipboy-loopback.apk`.

## 5b. Verifying airplane-mode safety

After the SwrveDisabled + HockeyAppDisabled patches the app should have
no outbound traffic except `127.0.0.1` and the LAN autodiscover
broadcast. To confirm on the Thor:

1. Disconnect from WiFi / enable airplane mode.
2. Launch Fallout 4 in GameNative, load a save.
3. Launch the patched companion app.
4. The `127.0.0.1` device should still appear in the device list within
   3 s; selecting it connects normally.

For a stricter check (counts every packet leaving the app's UID), enable
USB debugging again on WiFi, then:

```bash
adb shell -t 'su -c "iptables -I OUTPUT -m owner --uid-owner $(dumpsys package com.bethsoft.falloutcompanionapp | grep userId | head -1 | grep -oE '[0-9]+') ! -d 127.0.0.1 -j LOG --log-prefix PIPBOY: --log-level 4"'
adb logcat | grep PIPBOY
```

(Requires root, which the Thor has by default for the developer SKU.
On a stock device, use a packet capture VPN like PCAPdroid instead.)

## 6. First-run flow

1. Start Fallout 4 inside GameNative. Load a save (the companion server
   only starts once a save is loaded).
2. Launch the Pip-Boy companion app.
3. Tap "Connect to Pip-Boy" — after up to 3 s of discovery the device
   list should include `127.0.0.1` as **PC**.
4. Select it. First connect can take 2–4 s while the game serialises the
   full data dump.
5. Status / Inventory / Data / Map / Radio populate normally.

## 7. Troubleshooting

| Symptom                  | Likely cause                                                    | Fix                                                              |
|--------------------------|-----------------------------------------------------------------|------------------------------------------------------------------|
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | Original Bethesda APK still installed     | `adb uninstall com.bethsoft.falloutcompanionapp` then re-install              |
| Device list empty        | Companion not enabled in-game                                   | Set `bCompanionEnabled=1` in `Fallout4Custom.ini`, reload save   |
| Device list empty        | Save not loaded                                                 | Load any save first; the server starts with save load            |
| Loopback shows but Connect refused | GameNative's process is paused                        | Bring GameNative to foreground or run it in split-screen          |
| Connects but no data     | Wrong protocol version / mod conflict                           | Disable `Fallout4Companion` replacement mods                      |
| Map shows empty corners  | Player is in an interior cell with no worldspace                | Exit to outdoor, MapUpdate fires automatically                    |
| Drops every ~10s         | GameNative paused the Wine prefix while in background           | Disable battery optimisation for GameNative in Android settings  |
| App crashes on launch    | 32-bit native libs only (armeabi-v7a), 64-bit-only device       | The Pip-Boy app shipped armeabi-v7a only; needs 32-bit runtime    |
