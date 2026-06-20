# Strip-Boy

A surgical IL patcher that strips Bethesda's *Fallout 4 Pip-Boy* Android
companion app of its menus, telemetry, and LAN-discovery assumptions so it
becomes a one-tap loopback companion for the same-device case — *e.g.*
**AYN Thor** running Fallout 4 inside **GameNative** (Box64+Wine).

After the patch:

- the app **discovers the game over loopback** (`127.0.0.1:28000`), not
  just LAN broadcast
- it **auto-picks PC**, **auto-picks fullscreen mode**, **auto-selects
  the loopback entry** — zero taps from icon to live Pip-Boy view
- it **makes no outbound network calls** beyond `127.0.0.1` and the LAN
  broadcast the original already does — airplane-mode safe even with
  WiFi off
- it **launches on the secondary display** on dual-screen handhelds
  (the Thor's bottom screen), via a tiny smali launcher activity
- it **mirrors the in-game HUD colour** from the game's protocol every
  tick (the existing chain was gated on a delta-dirty flag that some
  F4 builds don't set when the player changes the colour)

The app's UI, audio, save data, gameplay protocol, and asset loading
are untouched. Only ten methods in `Assembly-CSharp.dll` change, plus
one new launcher class and a `<intent-filter>` move in the manifest.

This repository contains **the patcher and build scripts**. You supply
your own personal copy of the original v1.2 APK; nothing in this repo
redistributes Bethesda's code or assets.

## What's patched

| # | Method                                                       | Change |
|---|---------------------------------------------------------------|--------|
| 1 | `SocketDiscoveryChannel.CoreInitialize`                       | Adds one UDP send of the existing autodiscover payload to `127.0.0.1:28000` right after the broadcast send. The game's listener (bound `0.0.0.0:28000` inside Wine) replies on loopback, the existing receive callback parses it, and a `127.0.0.1` entry appears in the device list alongside any LAN responders. (+11 IL) |
| 2 | `SwrveSettings.IsValid`                                       | Body → `return false`. `SwrveManager.SendEvent` early-exits, so analytics POSTs to `swrve-content.s3.amazonaws.com` never fire. |
| 3 | `HockeyAppSettings.IsValid`                                   | Body → `return false`. `HockeyAppManager.Init` disables the crash-report iOS+Android components, so HockeyApp POSTs to `rink.hockeyapp.net` (Microsoft sunset that service in 2019) never fire. |
| 4 | `PlatformSelectionMenu.OnFlashReady`                          | Appends `OnItemSelected(0)` — the same call the PC button makes when tapped. The platform-select screen auto-advances; you go straight to the device list. (+3 IL) |
| 5 | `FlowState.<CheckForConnectivity>OnEntering` (compiler-named) | Body → `FlowTrigger.WIFIEnabled.Fire();`. The original gated on `Application.internetReachability == 2` (LAN reachable), which fails in airplane mode without WiFi. Loopback doesn't need WiFi, so we drop the check. |
| 6 | `SocketDiscoveryChannel.CoreInitialize` (broadcast Send)      | Wraps the broadcast UDP `Send` in `try { ... } catch (Exception) {}`. In full airplane mode the broadcast throws `SocketException: network unreachable`; without the shield the throw aborts `CoreInitialize` before the loopback send/receive ever run. |
| 7 | `IPListMenu.SetPossibleConnections`                           | Scans the device list right after it's populated; if any entry has `IP == "127.0.0.1"` it calls `OnListItemSelected(idx)` immediately — same path as a finger-tap. (~25 IL) |
| 8 | `FontConfigManager.GetText`                                   | Prepends `if (key == "$Companion_NoConsoleFoundDesc") return <GameNative-aware message>;`. All other keys fall through unchanged. |
| 9 | `DisplayModeSelectionMenu.OnFlashReady`                       | Appends `OnItemSelected(1)` — same shape as patch #4 — which is the "Fullscreen" enum value. Bypasses the one-time "Hardware vs Fullscreen" prompt; Hardware mode is for Bethesda's physical wrist-mount Pip-Boy cradle, not relevant on a handheld's flat bottom screen. |
| 10 | `PipboyStatusManager.UpdatePipboyEffectColor`                 | Body rewritten: switches the `GetMember<PipboyArray>("EffectColor", false)` call to `tolerateAbsentValue: true` (silent skip if the game's protocol doesn't carry the node), drops the `IsDirty` gate so the colour applies every tick, and emits a one-shot `Debug.Log` to `adb logcat` on first delivery so you can verify the bridge is live. The downstream chain — setter → PlayerPrefs → `PipboyEffectColorChanged` event → `PipboyPostEffect.SetColor` shader uniform — was already wired by Bethesda. |

Total IL delta: ~80 instructions across ten methods. The decompiled patched DLL
is byte-identical to the original except for those ten methods (plus a single
new private static `bool` field, `_stripboyHudColorLogged`, on
`PipboyStatusManager` that acts as the once-per-process Debug.Log gate). See
[`patcher/Program.cs`](patcher/Program.cs) for the exact edits.

## Build

```bash
# 1. Drop your personal v1.2 APK at apk/original.apk (or pass a path).
ln -s ~/Downloads/fallout-pip-boy-1-2.apk apk/original.apk

# 2. Run the pipeline.
./scripts/build.sh           # macOS / Linux
.\scripts\build.ps1          # Windows / PowerShell 7+
```

Output: **`apk/out/pipboy-loopback.apk`** — installable, v1/v2/v3 signed.

The pipeline: extract `Assembly-CSharp.dll`, run the patcher
(Mono.Cecil), in-place zip-update the DLL in a copy of the APK,
zipalign, sign with a freshly-generated debug keystore (`apk/debug.keystore`),
verify. ~3 seconds end-to-end after first build.

See [`apk/README.md`](apk/README.md) for how to source `original.apk`,
or run `adb pull` from a device that still has the original installed.
[`docs/CHECKSUMS.md`](docs/CHECKSUMS.md) records the SHA-256 of the
known-good baseline build.

## Install

```bash
# If you have Bethesda's original installed already, uninstall first
# (signatures will mismatch):
adb uninstall com.bethsoft.falloutcompanionapp

adb install -r apk/out/pipboy-loopback.apk
```

Or sideload via the Android Files app from the APK file.

## In-game setup

Inside the Wine prefix that runs Fallout 4, edit
`My Documents\My Games\Fallout4\Fallout4Custom.ini` and add:

```ini
[Companion]
bCompanionEnabled=1
bCompanionAutoStart=1
iCompanionPort=27000
iCompanionDiscoveryPort=28000
```

Load any save (the companion server starts on save load), then launch
the patched companion. Within ~3 s of discovery, a `127.0.0.1` "PC"
entry shows up — connect to it.

Full GameNative path + troubleshooting in
[`docs/GAMENATIVE.md`](docs/GAMENATIVE.md).

> **Silent NPC dialogue?** If Fallout 4 runs but characters' lips move with no
> voice (music and cutscenes are fine), that's Wine's FAudio failing to decode
> the game's xWMA `.fuz` files — separate from the companion. GOG copies don't
> get GameNative's automatic audio fix; the one-time workaround (install the
> game's *own* bundled DirectX redist under a non-arm64ec Proton, no downloads)
> is written up in
> [`docs/GAMENATIVE.md` §8](docs/GAMENATIVE.md#8-no-npc-dialogue-the-xwma-voice-fix).

## Airplane-mode safety

After patches 2, 3, and 5, the only sockets the app opens are:

- `UDP 0.0.0.0:<ephemeral>` — autodiscover broadcast (silently no-ops if no
  LAN interface)
- `UDP unicast → 127.0.0.1:28000` — loopback autodiscover (patch #1)
- `TCP → <selected device>:27000` — gameplay channel (loopback or LAN)

If the selected device is `127.0.0.1`, **the app stays entirely on
loopback** and works in airplane mode even with WiFi off. Both the
`fallout4.com` and `xbox.com` URL strings in the binary are only used by
`Application.OpenURL` from user taps on the in-app Settings menu — never
auto-fired.

## 32-bit only (no arm64 build)

The original APK ships **armeabi-v7a only** native libs (`libunity.so`,
`libmono.so`, `libgfxunity3d.so`, `libSmartGlassCore.so`, etc.). Building
an arm64-v8a variant would need Unity 5's IL2CPP arm64 toolchain (Unity
didn't ship one for the 5.x cycle) **and** the source for Bethesda's
proprietary native plugins (no public source). It's not feasible without
that.

In practice this is fine — AYN Thor (Snapdragon 8 Gen 2, "kalama")
reports `arm64-v8a, armeabi-v7a, armeabi` in its supported ABIs, so the
32-bit runtime is intact and the app runs cleanly. The same will hold
on any Android handheld that hasn't dropped 32-bit support. Devices to
watch out for: post-2024 Pixel-class flagships that ship 64-bit-only.

## Repo layout

```
Strip-Boy/
├── patcher/
│   ├── Patcher.csproj        net10.0 console app
│   └── Program.cs            Mono.Cecil patches (~250 lines)
├── scripts/
│   ├── build.sh              macOS / Linux pipeline
│   └── build.ps1             Windows PowerShell 7+ pipeline
├── docs/
│   ├── PROTOCOL.md           F4 gameplay companion wire format
│   ├── GAMENATIVE.md         install + Wine networking notes + troubleshooting
│   ├── COPYRIGHT.md          interop scope (Sega v. Accolade, DMCA §1201(f))
│   └── CHECKSUMS.md          known-good APK hash
├── apk/                      (gitignored — see apk/README.md)
│   ├── original.apk          ← you supply
│   ├── managed/              ← extracted + patched DLLs
│   ├── work/                 ← scratch
│   ├── debug.keystore        ← generated first run
│   └── out/
│       └── pipboy-loopback.apk   ← the deliverable
├── README.md                 (this file)
└── LICENSE                   MIT (patcher source only — see notes inside)
```

## Why a patch instead of a rewrite?

The original app does the UI, the binary node-tree codec, the audio,
the animations, the asset loading — all of it well. The only thing
broken on a same-device setup is the discovery probe's destination
address. Replacing 30 KB of source to fix 11 instructions of IL would
have been silly.

## Verification

```bash
# Confirm the final APK's DLL has all three patches:
unzip -p apk/out/pipboy-loopback.apk assets/bin/Data/Managed/Assembly-CSharp.dll \
    > /tmp/check.dll
ilspycmd /tmp/check.dll -o /tmp/check -p
grep -A 1 'IPAddress.Broadcast' /tmp/check/SocketDiscoveryChannel.cs
grep 'return false' /tmp/check/SwrveSettings.cs /tmp/check/HockeyAppSettings.cs
```

## Dependencies

- macOS, Linux, or Windows
- `dotnet` SDK 8.0+ (developed against 10.0.203)
- `keytool` (any JDK)
- Android `build-tools` 30.0.0+ for `apksigner` + `zipalign` (Android
  Studio's SDK Manager installs these)
- `unzip` + `zip` (on Windows, the script uses `System.IO.Compression`,
  no external `zip` needed)

## Legal

This project is a clean-room interoperability tool, distributed under
the MIT license. It does **not**:

- redistribute Bethesda's APK or any of its contents
- bypass any technical protection measure (the APK has none)
- modify or distribute any game files

It **does** insert three small modifications into a user's *own* copy of
the companion APK so it can interoperate with Fallout 4 running in a
Wine compatibility layer on the same device. Reverse-engineering for
interoperability is explicitly protected under DMCA §1201(f) and US
case law (*Sega v. Accolade*, *Sony v. Connectix*).

Trademarks of ZeniMax / Bethesda are referenced only descriptively.
This project is not affiliated with or endorsed by ZeniMax or Bethesda.

See [LICENSE](LICENSE) and [docs/COPYRIGHT.md](docs/COPYRIGHT.md).
