# Flicker-sync experiments

How to mirror the screen's per-frame `fBrightness` flicker onto the
analog-stick LEDs without breaking the Pip-Boy shader rendering.

## Problem recap

The shipped LED bridge writes once per `PipboyPostEffect.SetColor` call
— event-driven, fires only when the screen tint changes. The screen's
visible flicker / pulse / scanline-jitter is driven by per-frame
mutation of `PipboyPostEffect.fBrightness` (range ~0.3..1.5), which
the shader reads via the `_Brightness` material uniform. The LEDs are
blind to that.

Two prior attempts at hooking `PipboyPostEffect.Update` synchronously
or asynchronously both produced visible shader artifacts (gradient
banding mid-screen, missing scanlines). Either the per-frame IL
injection itself disturbs Mono's JIT/renderer scheduling, or our
specific operations (`Material.GetColor`, `object[]` allocation, JNI
boxing) have observable side effects.

## Three experiments, in escalating cost

The shape: each experiment isolates ONE suspect. Run A1 first to
identify the actual culprit before committing to B or C.

---

### Experiment A1 — Minimal IL injection, NO JNI

The cheapest possible probe. Insert TWO instructions at the
`_Brightness` SetFloat call site: a `dup` (copy the float that's
about to be passed to the shader) and a `stsfld` to stash it in a new
private static field on `PipboyPostEffect`. No JNI, no allocations,
no Java side at all.

**Hypothesis**: if this still breaks the shader → IL injection in
Update is itself poison (some Mono JIT or method-size-threshold
interaction). All Update-based approaches are dead.

If the shader is fine → the issue was JNI/allocations, and we can
build the Java side carefully (see A2).

**Cecil sketch** (drop into `patcher/Program.cs` as a new patch
class, registered after `LEDStickBridge`):

```csharp
static class FlickerProbeA1
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect not found");
        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("Update() not found");

        // Idempotence
        if (type.Fields.Any(f => f.Name == "_stripboyLastFB"))
            return new(false, "A1 probe already installed");

        // Find the `_Brightness` SetFloat sequence:
        //   ldstr "_Brightness"
        //   ldarg.0
        //   call PipboyPostEffect::QBrightness()
        //   callvirt Material::SetFloat(string, float)
        // We insert dup + stsfld AFTER the QBrightness call so the
        // float we tap is exactly the value the shader receives.
        Instruction? brightCall = null;
        foreach (var i in update.Body.Instructions)
        {
            if (i.OpCode != OpCodes.Ldstr) continue;
            if ((i.Operand as string) != "_Brightness") continue;
            var c = i.Next;
            while (c != null)
            {
                if ((c.OpCode == OpCodes.Call || c.OpCode == OpCodes.Callvirt)
                    && c.Operand is MethodReference mr
                    && mr.Name == "QBrightness")
                { brightCall = c; break; }
                if (c.OpCode == OpCodes.Callvirt) break;  // hit SetFloat without finding QBrightness
                c = c.Next;
            }
            break;
        }
        if (brightCall is null)
            throw new Exception("Could not locate _Brightness SetFloat sequence");

        var fbField = new FieldDefinition("_stripboyLastFB",
            FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Single);
        type.Fields.Add(fbField);

        var il = update.Body.GetILProcessor();
        var dup = il.Create(OpCodes.Dup);
        var st = il.Create(OpCodes.Stsfld, fbField);
        il.InsertAfter(brightCall, dup);
        il.InsertAfter(dup, st);

        // Stack delta: 0 (dup adds 1, stsfld pops 1). Peak +1.
        if (update.Body.MaxStackSize < 8) update.Body.MaxStackSize = 8;

        return new(true, "PipboyPostEffect::Update _Brightness call tee'd → _stripboyLastFB");
    }
}
```

**Test protocol**:
1. Apply this patch INSTEAD of `LEDStickBridge` (or alongside, doesn't
   matter — they don't conflict). Rebuild + install.
2. Get to a Pip-Boy display screen. Watch for gradient/scanline
   artifacts.
3. If shader looks clean → A1 succeeds, move to A2.
4. If shader broken → IL-in-Update is fundamentally poisoned, skip to
   Experiment C (sidekick).

---

### Experiment A2 — Add JNI write using pre-allocated jvalue[]

Predicated on A1 succeeding. Adds the LED write path with ZERO
per-frame allocations using `UnityEngine.AndroidJNI.CallStaticVoidMethod`
+ a pre-allocated `jvalue[]`.

**Per-frame work added on top of A1**:
- Load `jvalueArr` static field (already alloc'd at first call)
- `ldelema jvalue` (get address of element 0)
- Store `fBrightness` into the `f` member of that jvalue
- `AndroidJNI.CallStaticVoidMethod(classPtr, methodId, jvalueArr)`

`classPtr` and `methodId` are cached `IntPtr` static fields, looked up
once via `AndroidJNI.FindClass` + `AndroidJNI.GetStaticMethodID` at
the first call.

The LEDBridge.smali side gets a new method
`applyBrightness(float fB)` that just stores fB in a static field +
posts to the existing HandlerThread.

**Hypothesis**: if A1 worked but A2 breaks → the JNI call itself is
the culprit (sync barrier, GC interaction). Throttle: only call A2's
JNI once every N frames.

If A2 works → ship it. We've achieved flicker sync with zero
per-frame allocations.

**Cecil sketch deferred** — only worth writing if A1 succeeds.

---

### Experiment C — Sidekick APK (the user's "Bifrost plugin" framing)

Predicated on A1 failing, i.e. IL-in-Update is fundamentally
incompatible with the renderer.

Build a tiny standalone APK, `io.pipboy.thor.flickerd`, that:
- Declares a `BroadcastReceiver` for action
  `io.pipboy.thor.SET_COLOR` with string extra `rgb="R,G,B"`
- Maintains a `HandlerThread` running at 60 Hz
- Per tick: reads its stored color, applies a Perlin-noise-style
  flicker multiplier (range ~0.3..1.0), scales RGB, writes
  `/sys/class/sn3112{l,r}/led/brightness`

Strip-Boy side change: the existing `LEDStickBridge` Cecil patch
swaps from calling `LEDBridge.apply(...)` to sending a broadcast
intent. The broadcast is fire-and-forget, ~50μs main-thread cost,
no per-frame work at all (still only fires on `SetColor`).

The sidekick's flicker is NOT synced to the actual screen flicker —
it's plausibly-shaped fake. The user can't easily tell the difference
without staring at both simultaneously, and even then a good Perlin
generator looks like a CRT flicker.

**Layout**:
```
strip-boy-flickerd/
├── AndroidManifest.xml
├── src/io/pipboy/thor/flickerd/
│   ├── ColorReceiver.java          ← BroadcastReceiver
│   ├── TickerService.java          ← Foreground service hosting HandlerThread
│   └── LedWriter.java              ← Sysfs writes + Perlin
├── build.gradle
└── README.md
```

Tiny — <50 lines per file, total APK ~30KB.

Strip-Boy's Cecil patch becomes:
```csharp
// Replace LEDBridge AJC call with broadcast intent dispatch
var intentType = ...;
var setActionMethod = ...;
var putExtraStringMethod = ...;
var sendBroadcastMethod = ...;
// new Intent("io.pipboy.thor.SET_COLOR")
//   .putExtra("rgb", r + "," + g + "," + b);
// activity.sendBroadcast(intent);
```

Two APKs to install. User installs Strip-Boy + flickerd. Strip-Boy
detects flickerd's presence (PackageManager) and uses broadcast
path; falls back to direct sysfs if flickerd missing.

---

## Pick order when next at the keyboard

1. **A1 first** (5 minutes to implement + test). The result tells us
   whether B (jvalue JNI) or C (sidekick) is the right next move.
2. If A1 passes → **A2** (~30 min). Most likely to ship cleanly.
3. If A1 fails → **C** (~2 hr for sidekick + Strip-Boy wiring). The
   "no matter what, this can't break the shader" path.

Experiment B (sibling MonoBehaviour) from the previous loop iteration
is deprecated — A1 covers the same hypothesis space with less
scaffolding, and Cecil-creating a new MonoBehaviour type that Unity
accepts at runtime is unpleasant.

## Things not worth pursuing

- **ContentObserver from Java on Settings.System changes**: Mono
  doesn't write fBrightness to Settings.System; Java can't observe a
  Mono field via this mechanism.
- **Reading fBrightness from Java via Mono's embedded C API**:
  technically possible (`mono_class_get_field_from_name` +
  `mono_field_get_value` from `libmonosgen-2.0.so` via JNI), but
  requires per-tick cross-runtime calls of dubious thread-safety.
- **`LightsManager.openSession`**: Android's public lights API. Tested
  the service shape earlier (`adb shell service list | grep light`);
  it exposes the notification LED, not the AYN stick chips. Wrong API.
- **ARM64 PerfCounter sampling**: was on the loop's brainstorm list,
  but it's a hardware-counter API for performance profiling, not a
  way to read application memory. Wrong tool.
