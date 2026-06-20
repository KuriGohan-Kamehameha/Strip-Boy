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
| NPC lips move, **no voice** (music/cutscenes fine) | Wine's FAudio doesn't decode the game's xWMA `.fuz` dialogue | Install native XAudio2 — see [§8](#8-no-npc-dialogue-the-xwma-voice-fix) |

## 8. No NPC dialogue: the xWMA voice fix

If Fallout 4 runs but **NPCs move their lips with no sound** — while music,
ambient, and the Bink intro cutscenes all play fine — that's the classic
Bethesda-on-Wine voice bug, nothing to do with Strip-Boy. Character dialogue
lives as **xWMA** inside `.fuz` files and is decoded through **XAudio2**. Wine's
XAudio2 reimplementation (FAudio) in the GameNative Proton builds doesn't decode
xWMA, so the voice track is silent while everything on another codec is fine.

GameNative ships an automatic fix — it extracts the native XAudio DLLs into the
prefix — but **only for Steam titles, on first launch**. A **GOG** copy never
gets it, and the UI exposes no way to add native XAudio2 by hand (it's absent
from Win Components, and the Contents Manager only accepts graphics packages —
DXVK / VKD3D / Box64 / etc., no audio type).

### The fix (verified on AYN Thor, GOG Fallout 4)

Install Microsoft's native `xaudio2_7.dll` into the Wine prefix using the game's
**own bundled DirectX redistributable**. Nothing is downloaded and nothing is
redistributed — Fallout 4 already ships `DXSETUP.exe` under `_CommonRedist\DirectX\`.

1. **Long-press the game → gear (*Options*) → *Edit container*.**
2. **General → Wine Version → `proton-9.0-x86_64`.** This is the load-bearing
   step. Under the default **arm64ec** Proton the x64 game prefers the ARM64EC
   builtin FAudio over any native x64 DLL, so an installed native `xaudio2_7` is
   simply ignored. A plain `x86_64` Proton has no such preference and loads the
   native DLL. (Tradeoff in §8.1.)
3. **General → Executable Path → `_CommonRedist/DirectX/DXSETUP.exe`**, set
   **Exec Arguments → `/silent`**, then **Save**.
4. **Launch it.** The Proton switch rebuilds the prefix first ("Installing
   Mono…", then the VC++ redists, then DXSETUP runs silently). When GameNative's
   "How did the game run?" rating dialog pops up, the install has finished —
   dismiss it.
5. **General → Executable Path → back to `Fallout4.exe`**, clear Exec Arguments,
   **Save**.
6. **Environment tab → add** `WINEDLLOVERRIDES = xaudio2_7=n,b` (native first,
   builtin fallback) if it isn't already there. This points Wine at the
   `xaudio2_7.dll` DXSETUP just dropped in `system32`.
7. **Relaunch Fallout 4.** Dialogue plays.

Confirm it from the host while the game boots — it should resolve `xaudio2_7`
out of the prefix's `system32`, not Proton's builtin:

```bash
adb logcat | grep -i xaudio2_7
# want:  …/drive_c/windows/system32/xaudio2_7.dll     ← native, fixed
# wrong: …/proton-*/lib/wine/…/xaudio2_7.dll          ← builtin FAudio
```

### 8.1 Performance tradeoff

`proton-9.0-x86_64` emulates the entire x64 binary (Box64/FEX) instead of
arm64ec's near-native ARM path, so framerate drops versus the arm64ec build —
on an 8 Gen 2, roughly the difference between "smooth" and "playable." If you'd
rather keep arm64ec speed, the clean alternative is owning Fallout 4 **on
Steam**: the Steam copy gets the automatic XAudio extraction on first launch and
stays on arm64ec.

### 8.2 Dead ends (so you don't repeat them)

- **The override alone**, on arm64ec — there's no native DLL for it to bind to,
  so it falls straight back to the same builtin FAudio.
- **Windows Media Decoder → Builtin** (winegstreamer) — does not reroute the
  xWMA decode.
- **A community `xaudio2_9` package** — wrong version. Fallout 4 wants XAudio
  **2.7**; 2.9 is a different COM interface and isn't a drop-in.
- **Running DXSETUP under arm64ec** — installs the DLL, still ignored. arm64ec
  is the blocker; that's the entire reason for step 2.
