# Strip-Boy Flickerd

Tiny standalone APK that owns all writes to `/sys/class/sn3112{l,r}/led/brightness`. Receives colour broadcasts from the patched Pip-Boy companion (Strip-Boy) and applies a Perlin-ish flicker pattern at ~30 Hz on a `HandlerThread`.

This exists because **in-process per-frame hooks on `PipboyPostEffect.Update` break the shader rendering** — gradient banding mid-screen, scanlines disappear. Even with sysfs writes on a background thread inside Strip-Boy, the very presence of our IL injection inside the per-frame `Update` body disturbs Unity/Mono enough to corrupt the shader uniform writes that share that method.

By moving the LED tick loop into a separate APK with its own process, **the LED logic cannot affect the Pip-Boy rendering**. The cost is two install steps instead of one.

## Status

**EXPERIMENTAL / DRAFT.** Sources compile cleanly in theory but haven't been built or run against a device yet. See `docs/FLICKER_EXPERIMENTS.md` for the full investigation context — flickerd is Experiment C, the fallback path if Experiments A1/A2 (zero-allocation in-process JNI) fail.

## What it does

```
                            ┌──────────────────────────┐
   Strip-Boy / Pip-Boy app  │ PipboyPostEffect.SetColor│
                            └─────────────┬────────────┘
                                          │ (Cecil-patched in)
                                          ▼
                            ┌──────────────────────────┐
                            │ sendBroadcast            │
                            │   io.pipboy.thor         │
                            │     .SET_COLOR           │
                            │   extras: r, g, b (ints) │
                            └─────────────┬────────────┘
                                          │ (Android binder)
                                          ▼
   Strip-Boy Flickerd      ┌──────────────────────────┐
   (this APK)              │ ColorReceiver.onReceive  │
                           │   updateColor(r,g,b)     │
                           │   startService(Ticker)   │
                           └─────────────┬────────────┘
                                         │
                                         ▼
                           ┌──────────────────────────┐
                           │ TickerService            │
                           │  ↳ HandlerThread @ 30 Hz │
                           │     pulse(t) * flicker() │
                           │     write sysfs ×2       │
                           │  ↳ auto-stop after 30 s  │
                           │     of broadcast silence │
                           └──────────────────────────┘
```

## Build

The toolchain is the same Android SDK build-tools that Strip-Boy uses (`apksigner`, `zipalign`, `aapt2`) plus `d8` for `.class → .dex`. No Gradle required.

```bash
./scripts/build.sh
# → out/flickerd.apk (debug-signed, installable)
```

Output goes in `out/flickerd.apk`. About ~10 KB.

If Strip-Boy's `apk/debug.keystore` already exists you'll reuse it (same alias/pass). Otherwise the script generates one.

## Install

```bash
adb install -r out/flickerd.apk
```

That's it. No permission grants needed — `/sys/class/sn3112{l,r}/led/brightness` is world-writable on stock AYN Thor firmware.

## Wire Strip-Boy to it

This is **not yet implemented** in the Strip-Boy patcher. The change needed:

In `patcher/Program.cs`'s `LEDStickBridge.Apply`, instead of (or in addition to) calling `LEDBridge.apply(...)` via `AndroidJavaClass.CallStatic`, build and send a broadcast Intent:

```csharp
// Pseudocode for the Cecil-emitted IL:
//   Intent i = new Intent("io.pipboy.thor.SET_COLOR");
//   i.putExtra("r", r);
//   i.putExtra("g", g);
//   i.putExtra("b", b);
//   UnityPlayer.currentActivity.sendBroadcast(i);
```

Then the SetColor hook becomes a fire-and-forget broadcast (~50 μs main-thread cost) and all LED logic lives in flickerd.

Strip-Boy should detect flickerd's presence via
`PackageManager.getPackageInfo("io.pipboy.thor.flickerd", 0)` and use the broadcast path; if flickerd isn't installed, fall back to the direct-sysfs path already in `LEDBridge.smali`.

## Behaviour

- Pulse: ~85-100 % brightness oscillating over a 4-second period (slow CRT-warm-up vibe).
- Flicker: ~4 % chance per tick of a brief dip (1-3 ticks at 30 Hz = 33-100 ms) down to 25-60 % brightness. CRT-glitch vibe.
- Brightness ceiling: 5 % LED-PWM × bottom-screen-brightness slider (same maths as Strip-Boy's in-process LEDBridge).
- Auto-shutdown: service stops itself after 30 s without a colour broadcast, so it goes away when the Pip-Boy companion is backgrounded or closed.

## Uninstall

```bash
adb uninstall io.pipboy.thor.flickerd
```

Strip-Boy then falls back to event-driven LED writes (current behaviour).
