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
- it **drives the AYN Thor's analog-stick RGB LEDs** so they change
  exactly once per screen-colour change, mirroring the bottom-screen
  brightness slider with a 70 % ceiling

The app's UI, audio, save data, gameplay protocol, and asset loading
are untouched. Eleven methods in `Assembly-CSharp.dll` change, plus
two helper smali classes (the launcher activity and the LED bridge)
and a `<intent-filter>` move in the manifest. No special permissions.

This repository contains **the patcher and build scripts**. You supply
your own personal copy of the original v1.2 APK; nothing in this repo
redistributes Bethesda's code or assets.

It also carries a small Android helper app under
[`thor-launch-wrapper/`](thor-launch-wrapper/) for the AYN Thor flow: one tap
starts the patched companion on the bottom display, starts GameNative on the
top display with the verified GOG Fallout 4 target, then removes the launcher
from the task stack.

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
| 11 | `PipboyPostEffect.SetColor` (tail)                            | After the existing `_materialToModify.SetColor("_Color", color)` (the literal moment the screen shader's tint changes), inject `new AndroidJavaClass("io.pipboy.thor.LEDBridge").CallStatic("apply", (int)(r*255), (int)(g*255), (int)(b*255), 1.0f)`. The AndroidJavaClass JNI ref is cached on a new private static field (`_stripboyLedBridgeCls`) so we don't pay JNI lookup cost per call. The smali helper drives the AYN Thor's SN3112L/R analog-stick LED controllers **directly** via `/sys/class/sn3112{l,r}/led/brightness` (world-writable on stock firmware — same path Moonbench's Bifrost LED utility uses) in the format `"1-R:G:B:A\n"`. Both sticks get the same `"1-"` prefix (verified empirically that `2-`/`3-`/`4-` are silent no-ops; only `1-` drives the LEDs — path selects side). The helper saturates the colour (subtracts min channel, rescales to `target_max`) to strip the white wash, and scales the result's max channel to `bottom × 0.1275` (≈ 5 % LED-PWM ceiling × screen-brightness factor) — at bottom=44 that's max-channel = 5, ≈ 2 % PWM. The kernel driver ignores the 4th alpha field — brightness lives entirely in the RGB magnitudes after this scaling. Async architecture: `apply()` just stores pending values + posts to a dedicated `HandlerThread`; the actual `FileOutputStream` sysfs writes happen on that background thread so they never starve Unity's main-thread shader uniform writes. Dedupe on `(r, g, b)` skips redundant calls. No permissions required. |

Total IL delta: ~85 instructions across eleven methods. The decompiled patched DLL
is byte-identical to the original except for those eleven methods (plus a private
static `bool` field `_stripboyHudColorLogged` on `PipboyStatusManager` and a
private static `AndroidJavaClass` field `_stripboyLedBridgeCls` on
`PipboyPostEffect`). See [`patcher/Program.cs`](patcher/Program.cs) for the
exact edits.

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

## Thor launch wrapper

The optional wrapper APK is source-only in this repo at
[`thor-launch-wrapper/`](thor-launch-wrapper/). It is intentionally separate
from the patched Bethesda companion APK: the wrapper is a small native Android
app with a green-on-black Pip-Boy-style interface, a custom launcher icon,
and a strict patched-companion check.

Launch contract:

- Companion package: `com.bethsoft.falloutcompanionapp`
- Patched companion marker: `io.pipboy.thor.LauncherActivity`
- GameNative package: `app.gamenative`
- GameNative action: `app.gamenative.LAUNCH_GAME`
- Fallout 4 GOG app id: `1998527297`
- GameNative source extra: `game_source=GOG`
- Bifrost package: `com.moonbench.bifrost`
- Required Bifrost plugin id: `fallout4-pipboy`

Build and install:

```bash
cd thor-launch-wrapper
JAVA_HOME="$(/usr/libexec/java_home -v 17)" ./gradlew assembleDebug --no-daemon
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

ADB-only smoke launch:

```bash
adb shell am start -n com.moonbench.thorlaunch/.MainActivity \
  --ez com.moonbench.thorlaunch.AUTO_LAUNCH true
```

Expected final state on AYN Thor:

- Display #0: `app.gamenative/.MainActivity`
- Display #4: `com.bethsoft.falloutcompanionapp/com.unity3d.player.UnityPlayerNativeActivity`
- No live `com.moonbench.thorlaunch` process after handoff

When `launch automatically next time onwards` is checked, later wrapper starts
are headless: the app verifies all checks and launches the two targets without
drawing the wizard, showing itself again only if a prerequisite fails. Manual
launches with that box unchecked prime both Thor stick LEDs to green at roughly
30% brightness before handoff.

The wrapper distinguishes three companion states — not installed, installed but
**unpatched**, and patched — and on detecting an unpatched companion it begins
the patch flow (locate the installed `base.apk`, verify it against the known
v1.2 baseline, then patch + install). The on-device patch *engine* is still
open: see [`thor-launch-wrapper/docs/THOR_AUTO_PATCH.md`](thor-launch-wrapper/docs/THOR_AUTO_PATCH.md)
for what is implemented and the one engine decision that remains. Until an
on-device engine lands, an unpatched companion degrades to a clear
"build it on PC with `scripts/build.sh`" message rather than a silent failure.

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
├── thor-launch-wrapper/      optional AYN Thor launcher APK source
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
- For `thor-launch-wrapper/`: JDK 17 and an Android SDK usable by Gradle

## Attributions

- Strip-Boy companion patch and Thor Launch Wrapper: Kuri
  ([github/KuriGohan-Kamehameha](https://github.com/KuriGohan-Kamehameha)).
- Bifrost: invented and maintained by Pollux, with plugin and project
  contributions from Kuri.
- Fallout, Pip-Boy, and related marks are owned by Bethesda / ZeniMax and are
  referenced descriptively. This project is not affiliated with or endorsed by
  Bethesda / ZeniMax.

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
