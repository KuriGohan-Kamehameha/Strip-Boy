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

**Architecture split** — separate Java methods for color vs brightness
so each Cecil hook injects minimal IL:

| Smali method | Called from | Stores into |
|---|---|---|
| `applyColor(I,I,I)V` | SetColor hook (already shipped, rename) | pendingR/G/B |
| `applyBrightness(F)V` | Update _Brightness tee (A2 hook) | pendingFB |

Both methods post the singleton writer Runnable to the existing
HandlerThread. The writer reads all four pending fields and writes
sysfs. Coalescing via `handler.removeCallbacks` keeps the queue at
≤ 1 entry.

**Per-frame IL on top of A1's dup+stsfld**:

```
; at the same site, immediately after stsfld _stripboyLastFB
; (which left no value on the stack):

; Ensure JNI handles cached (do this once)
ldsfld _stripboyApplyBrightnessMethodId
brtrue methodCached
ldstr "io/pipboy/thor/LEDBridge"
call UnityEngine.AndroidJNI::FindClass(string)
stsfld _stripboyLEDBridgeClassPtr
ldsfld _stripboyLEDBridgeClassPtr
ldstr "applyBrightness"
ldstr "(F)V"
call UnityEngine.AndroidJNI::GetStaticMethodID(IntPtr, string, string)
stsfld _stripboyApplyBrightnessMethodId
methodCached:

; Set jvalueArr[0].f = _stripboyLastFB
ldsfld _stripboyJvalueArr
ldc.i4.0
ldelema UnityEngine.jvalue
ldsfld _stripboyLastFB
stfld UnityEngine.jvalue::f

; AndroidJNI.CallStaticVoidMethod(classPtr, methodId, jvalueArr)
ldsfld _stripboyLEDBridgeClassPtr
ldsfld _stripboyApplyBrightnessMethodId
ldsfld _stripboyJvalueArr
call UnityEngine.AndroidJNI::CallStaticVoidMethod(IntPtr, IntPtr, UnityEngine.jvalue[])
```

Plus static-cctor work on PipboyPostEffect to allocate `_stripboyJvalueArr = new jvalue[1]` ONCE. After that: per-frame work is 4 sfld + 1 stfld + 1 native JNI call. No heap alloc.

**Cecil sketch** (combines A1+A2 — call from a single `Apply`):

```csharp
static class FlickerSyncA2
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")!;
        var update = type.Methods.First(m =>
            m.Name == "Update" && m.Parameters.Count == 0);

        if (type.Fields.Any(f => f.Name == "_stripboyLastFB"))
            return new(false, "A2 already installed");

        var unityRef = module.AssemblyReferences.First(a => a.Name == "UnityEngine");

        // ---- Types ----
        var intPtrType = module.ImportReference(typeof(IntPtr));
        var jvalueType = new TypeReference("UnityEngine", "jvalue",
            module, unityRef, valueType: true);
        var jvalueArrType = new ArrayType(jvalueType);
        var androidJniType = new TypeReference("UnityEngine", "AndroidJNI",
            module, unityRef, valueType: false);

        // ---- Method refs (no .Resolve()) ----
        var findClass = new MethodReference("FindClass", intPtrType, androidJniType);
        findClass.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var getStaticMethodID = new MethodReference("GetStaticMethodID",
            intPtrType, androidJniType);
        getStaticMethodID.Parameters.Add(new ParameterDefinition(intPtrType));
        getStaticMethodID.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        getStaticMethodID.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var callStaticVoid = new MethodReference("CallStaticVoidMethod",
            module.TypeSystem.Void, androidJniType);
        callStaticVoid.Parameters.Add(new ParameterDefinition(intPtrType));
        callStaticVoid.Parameters.Add(new ParameterDefinition(intPtrType));
        callStaticVoid.Parameters.Add(new ParameterDefinition(jvalueArrType));

        var jvalueFloatField = new FieldReference("f",
            module.TypeSystem.Single, jvalueType);

        // ---- Static fields on PipboyPostEffect ----
        var fbField = new FieldDefinition("_stripboyLastFB",
            FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Single);
        var classPtrField = new FieldDefinition("_stripboyLEDBridgeClassPtr",
            FieldAttributes.Private | FieldAttributes.Static, intPtrType);
        var methodIdField = new FieldDefinition("_stripboyApplyBrightnessMethodId",
            FieldAttributes.Private | FieldAttributes.Static, intPtrType);
        var jvalueArrField = new FieldDefinition("_stripboyJvalueArr",
            FieldAttributes.Private | FieldAttributes.Static, jvalueArrType);
        type.Fields.Add(fbField);
        type.Fields.Add(classPtrField);
        type.Fields.Add(methodIdField);
        type.Fields.Add(jvalueArrField);

        // ---- jvalueArr init in <cctor> ----
        var cctor = type.GetStaticConstructor();
        if (cctor is null)
        {
            cctor = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
                | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            cctor.Body = new MethodBody(cctor);
            cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(cctor);
        }
        var cctorIL = cctor.Body.GetILProcessor();
        var cctorRet = cctor.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        cctorIL.InsertBefore(cctorRet, cctorIL.Create(OpCodes.Ldc_I4_1));
        cctorIL.InsertBefore(cctorRet, cctorIL.Create(OpCodes.Newarr, jvalueType));
        cctorIL.InsertBefore(cctorRet, cctorIL.Create(OpCodes.Stsfld, jvalueArrField));
        if (cctor.Body.MaxStackSize < 1) cctor.Body.MaxStackSize = 1;

        // ---- Find the _Brightness SetFloat sequence in Update ----
        Instruction? brightCall = null;
        foreach (var i in update.Body.Instructions)
        {
            if (i.OpCode != OpCodes.Ldstr || (i.Operand as string) != "_Brightness") continue;
            var c = i.Next;
            while (c != null)
            {
                if ((c.OpCode == OpCodes.Call || c.OpCode == OpCodes.Callvirt)
                    && c.Operand is MethodReference mr && mr.Name == "QBrightness")
                { brightCall = c; break; }
                if (c.OpCode == OpCodes.Callvirt) break;
                c = c.Next;
            }
            break;
        }
        if (brightCall is null) throw new Exception("QBrightness call not found");

        var il = update.Body.GetILProcessor();
        var methodCachedAnchor = il.Create(OpCodes.Ldsfld, classPtrField);

        // After brightCall, the float fB is on the stack. Sequence:
        //   dup; stsfld fbField; (A1 part)
        //   if methodIdField is 0: do the FindClass + GetStaticMethodID dance
        //   set jvalueArr[0].f = fbField
        //   AndroidJNI.CallStaticVoidMethod(classPtr, methodId, jvalueArr)
        var seq = new List<Instruction>
        {
            // A1: dup, stsfld _stripboyLastFB
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Stsfld, fbField),

            // if (_stripboyApplyBrightnessMethodId.ToInt64() != 0) goto methodCachedAnchor
            il.Create(OpCodes.Ldsfld, methodIdField),
            // IntPtr.Zero check via call to IntPtr op_Inequality? Simpler:
            // ldsfld IntPtr; ldsfld IntPtr.Zero; call op_Inequality (bool)
            // For simplicity here, use brtrue on the IntPtr itself (non-zero IntPtr is "truthy" via unsigned cmp)
            il.Create(OpCodes.Brtrue, methodCachedAnchor),

            // _stripboyLEDBridgeClassPtr = AndroidJNI.FindClass("io/pipboy/thor/LEDBridge");
            il.Create(OpCodes.Ldstr, "io/pipboy/thor/LEDBridge"),
            il.Create(OpCodes.Call, findClass),
            il.Create(OpCodes.Stsfld, classPtrField),

            // _stripboyApplyBrightnessMethodId =
            //     AndroidJNI.GetStaticMethodID(classPtr, "applyBrightness", "(F)V");
            il.Create(OpCodes.Ldsfld, classPtrField),
            il.Create(OpCodes.Ldstr, "applyBrightness"),
            il.Create(OpCodes.Ldstr, "(F)V"),
            il.Create(OpCodes.Call, getStaticMethodID),
            il.Create(OpCodes.Stsfld, methodIdField),

            // methodCachedAnchor:  (ldsfld classPtrField — reused as the cached load)
            methodCachedAnchor,

            // jvalueArr[0].f = _stripboyLastFB
            il.Create(OpCodes.Ldsfld, jvalueArrField),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Ldelema, jvalueType),
            il.Create(OpCodes.Ldsfld, fbField),
            il.Create(OpCodes.Stfld, jvalueFloatField),

            // AndroidJNI.CallStaticVoidMethod(classPtr, methodId, jvalueArr)
            // Note: the methodCachedAnchor ldsfld already pushed classPtr; we
            // also need methodId and jvalueArr.
            il.Create(OpCodes.Ldsfld, methodIdField),
            il.Create(OpCodes.Ldsfld, jvalueArrField),
            il.Create(OpCodes.Call, callStaticVoid),
        };

        var cursor = brightCall;
        foreach (var ins in seq)
        {
            il.InsertAfter(cursor, ins);
            cursor = ins;
        }

        if (update.Body.MaxStackSize < 6) update.Body.MaxStackSize = 6;

        return new(true, "PipboyPostEffect::Update _Brightness tee'd → A2 JNI path");
    }
}
```

**Smali addition for A2** — append to `LEDBridge.smali`:

```smali
.method public static applyBrightness(F)V
    .registers 4
    .param p0, "fBrightness"

    sput p0, Lio/pipboy/thor/LEDBridge;->pendingFB:F

    sget-object v0, Lio/pipboy/thor/LEDBridge;->handler:Landroid/os/Handler;
    sget-object v1, Lio/pipboy/thor/LEDBridge;->writer:Ljava/lang/Runnable;
    invoke-virtual {v0, v1}, Landroid/os/Handler;->removeCallbacks(Ljava/lang/Runnable;)V
    invoke-virtual {v0, v1}, Landroid/os/Handler;->post(Ljava/lang/Runnable;)Z

    return-void
.end method
```

Writer already reads `pendingFB`. No other changes.

**Hypothesis**: if A1 worked but A2 breaks → the JNI call itself is
the culprit (sync barrier, GC interaction). Throttle: gate the JNI
call on a frame counter, fire only every Nth frame.

---

### Experiment C — Sidekick APK (the user's "Bifrost plugin" framing)

**Materialized at `flickerd/` in this repo.** Sources, manifest, and a
Gradle-less build script (`flickerd/scripts/build.sh`) are checked in.
~520 LOC across 5 files; produces an ~10 KB APK. See `flickerd/README.md`
for build / install / wire-up instructions. The sketch below remains
for reference but the actual code is what you'll ship from.

A standalone tiny APK, `io.pipboy.thor.flickerd`, lives alongside
Strip-Boy. Owns all LED writes. Receives colour via broadcast
intent. Generates Perlin-ish flicker on its own timer.

**`flickerd/AndroidManifest.xml`**:

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="io.pipboy.thor.flickerd">

    <uses-permission android:name="android.permission.FOREGROUND_SERVICE"/>

    <application
        android:label="Strip-Boy Flickerd"
        android:icon="@android:drawable/sym_def_app_icon">

        <service
            android:name=".TickerService"
            android:enabled="true"
            android:exported="false"
            android:foregroundServiceType="specialUse">
            <property
                android:name="android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE"
                android:value="LED control loop for Strip-Boy"/>
        </service>

        <receiver
            android:name=".ColorReceiver"
            android:enabled="true"
            android:exported="true">
            <intent-filter>
                <action android:name="io.pipboy.thor.SET_COLOR"/>
                <action android:name="io.pipboy.thor.STOP"/>
            </intent-filter>
        </receiver>
    </application>
</manifest>
```

**`flickerd/src/io/pipboy/thor/flickerd/ColorReceiver.java`**:

```java
package io.pipboy.thor.flickerd;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class ColorReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        String action = intent.getAction();
        if ("io.pipboy.thor.SET_COLOR".equals(action)) {
            int r = intent.getIntExtra("r", 0);
            int g = intent.getIntExtra("g", 0);
            int b = intent.getIntExtra("b", 0);
            TickerService.updateColor(r, g, b);
            // Ensure the ticker is alive
            Intent svc = new Intent(ctx, TickerService.class);
            ctx.startForegroundService(svc);
        } else if ("io.pipboy.thor.STOP".equals(action)) {
            ctx.stopService(new Intent(ctx, TickerService.class));
        }
    }
}
```

**`flickerd/src/io/pipboy/thor/flickerd/TickerService.java`**:

```java
package io.pipboy.thor.flickerd;

import android.app.*;
import android.content.*;
import android.os.*;
import android.provider.Settings;
import java.io.FileOutputStream;

public class TickerService extends Service {
    private static volatile int colorR, colorG, colorB;
    private static volatile boolean haveColor;

    private HandlerThread thread;
    private Handler handler;
    private long startMillis;

    public static void updateColor(int r, int g, int b) {
        colorR = r; colorG = g; colorB = b;
        haveColor = true;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        startForeground(1, buildNotif());
        thread = new HandlerThread("strip-boy-flickerd");
        thread.start();
        handler = new Handler(thread.getLooper());
        startMillis = System.currentTimeMillis();
        handler.post(tick);
    }

    private final Runnable tick = new Runnable() {
        @Override public void run() {
            try {
                if (haveColor) writeLeds();
            } catch (Throwable t) { /* swallow */ }
            handler.postDelayed(this, 33);   // ~30 Hz
        }
    };

    private void writeLeds() throws Exception {
        // Sub-1Hz pulse base + occasional flicker dip
        long t = System.currentTimeMillis() - startMillis;
        double phase = (t % 5000) / 5000.0 * 2 * Math.PI;
        double pulse = 0.85 + 0.15 * Math.sin(phase);
        double flicker = (Math.random() < 0.04)
            ? 0.3 + Math.random() * 0.3
            : 1.0;
        double mult = Math.min(1.0, pulse * flicker);

        int bottom = Settings.System.getInt(getContentResolver(),
            "dual_screen_brightness_level", 50);
        // 5% ceiling × bottom% × mult, applied to RGB magnitudes
        double scale = bottom * 0.1275 / 255.0 * mult;

        int r = (int)(colorR * scale);
        int g = (int)(colorG * scale);
        int b = (int)(colorB * scale);

        String payload = "1-" + r + ":" + g + ":" + b + ":255\n";
        for (String path : new String[]{
                "/sys/class/sn3112l/led/brightness",
                "/sys/class/sn3112r/led/brightness"}) {
            try (FileOutputStream f = new FileOutputStream(path)) {
                f.write(payload.getBytes());
            }
        }
    }

    private Notification buildNotif() {
        String chId = "flickerd";
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm.getNotificationChannel(chId) == null) {
            nm.createNotificationChannel(new NotificationChannel(
                chId, "Strip-Boy LED", NotificationManager.IMPORTANCE_MIN));
        }
        return new Notification.Builder(this, chId)
            .setContentTitle("Strip-Boy LED")
            .setSmallIcon(android.R.drawable.sym_def_app_icon)
            .build();
    }

    @Override public int onStartCommand(Intent i, int f, int s) { return START_STICKY; }
    @Override public IBinder onBind(Intent i) { return null; }
    @Override public void onDestroy() {
        super.onDestroy();
        if (thread != null) thread.quitSafely();
    }
}
```

**`flickerd/build.gradle`**:

```groovy
plugins { id 'com.android.application' }
android {
    namespace 'io.pipboy.thor.flickerd'
    compileSdk 34
    defaultConfig {
        applicationId "io.pipboy.thor.flickerd"
        minSdk 26
        targetSdk 34
        versionCode 1
        versionName "1.0"
    }
    buildTypes { release { minifyEnabled false } }
}
```

**Strip-Boy side change** — replace the `LEDBridge.apply` AJC call in
the SetColor hook with an `Activity.sendBroadcast(Intent)`. Smali
becomes:

```smali
# In place of AndroidJavaClass.CallStatic("apply", ...):
#   Intent i = new Intent("io.pipboy.thor.SET_COLOR")
#       .putExtra("r", r).putExtra("g", g).putExtra("b", b);
#   UnityPlayer.currentActivity.sendBroadcast(i);
```

About 15 IL instructions. No object[], no Java-side handler in
Strip-Boy at all (just the broadcast send). Sidekick owns the rest.

**Install order**: `adb install flickerd.apk` then
`adb install strip-boy.apk`. Strip-Boy detects flickerd via
`PackageManager.getPackageInfo("io.pipboy.thor.flickerd", 0)` (no
permission needed) and uses the broadcast path; otherwise falls back
to the direct-sysfs path it already has.

---

## Wiring Strip-Boy to flickerd (auto-detect dispatch)

The flickerd APK at `flickerd/` is buildable, but Strip-Boy's current
`LEDBridge.smali` still writes sysfs directly. To make the in-Pip-Boy
side seamless, modify `LEDBridge.apply` to:

1. On first call, check via `PackageManager.getPackageInfo` whether
   `io.pipboy.thor.flickerd` is installed.
2. If yes → fire-and-forget broadcast Intent (`io.pipboy.thor.SET_COLOR`).
3. If no → fall through to the existing async sysfs path.

No Cecil change needed — `LEDStickBridge` keeps calling
`LEDBridge.apply` exactly as before. The dispatch lives entirely in
smali.

**Drop-in additions to `patcher/smali/io/pipboy/thor/LEDBridge.smali`**:

Add two static fields:

```smali
# 0 = not yet detected, 1 = sysfs path, 2 = broadcast path
.field private static dispatchMode:I
```

Replace `apply(IIIF)V`'s body with the dispatcher:

```smali
.method public static apply(IIIF)V
    .registers 7
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"
    .param p3, "fBrightness"

    sput p0, Lio/pipboy/thor/LEDBridge;->pendingR:I
    sput p1, Lio/pipboy/thor/LEDBridge;->pendingG:I
    sput p2, Lio/pipboy/thor/LEDBridge;->pendingB:I
    sput p3, Lio/pipboy/thor/LEDBridge;->pendingFB:F

    sget v0, Lio/pipboy/thor/LEDBridge;->dispatchMode:I
    if-nez v0, :have_mode
    invoke-static {}, Lio/pipboy/thor/LEDBridge;->detectDispatchMode()I
    move-result v0
    sput v0, Lio/pipboy/thor/LEDBridge;->dispatchMode:I

    :have_mode
    const/4 v1, 0x2
    if-ne v0, v1, :sysfs_path

    invoke-static {p0, p1, p2}, Lio/pipboy/thor/LEDBridge;->sendColorBroadcast(III)V
    return-void

    :sysfs_path
    sget-object v0, Lio/pipboy/thor/LEDBridge;->handler:Landroid/os/Handler;
    sget-object v1, Lio/pipboy/thor/LEDBridge;->writer:Ljava/lang/Runnable;
    invoke-virtual {v0, v1}, Landroid/os/Handler;->removeCallbacks(Ljava/lang/Runnable;)V
    invoke-virtual {v0, v1}, Landroid/os/Handler;->post(Ljava/lang/Runnable;)Z
    return-void
.end method
```

Add two helper methods:

```smali
.method private static detectDispatchMode()I
    .registers 5

    :try_start
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :default_sysfs
    invoke-virtual {v0}, Landroid/content/Context;->getPackageManager()Landroid/content/pm/PackageManager;
    move-result-object v1
    const-string v2, "io.pipboy.thor.flickerd"
    const/4 v3, 0x0
    invoke-virtual {v1, v2, v3}, Landroid/content/pm/PackageManager;->getPackageInfo(Ljava/lang/String;I)Landroid/content/pm/PackageInfo;
    # No exception → installed
    const-string v0, "strip-boy"
    const-string v1, "dispatch = broadcast (flickerd detected)"
    invoke-static {v0, v1}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    const/4 v0, 0x2
    return v0
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch_default

    :catch_default
    move-exception v0
    :default_sysfs
    const-string v0, "strip-boy"
    const-string v1, "dispatch = sysfs (flickerd not installed)"
    invoke-static {v0, v1}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    const/4 v0, 0x1
    return v0
.end method


.method private static sendColorBroadcast(III)V
    .registers 8
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"

    :try_start
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    # intent = new Intent("io.pipboy.thor.SET_COLOR")
    new-instance v1, Landroid/content/Intent;
    const-string v2, "io.pipboy.thor.SET_COLOR"
    invoke-direct {v1, v2}, Landroid/content/Intent;-><init>(Ljava/lang/String;)V

    # intent.setPackage("io.pipboy.thor.flickerd")
    const-string v2, "io.pipboy.thor.flickerd"
    invoke-virtual {v1, v2}, Landroid/content/Intent;->setPackage(Ljava/lang/String;)Landroid/content/Intent;

    # intent.putExtra("r", r) / "g", g / "b", b
    const-string v2, "r"
    invoke-virtual {v1, v2, p0}, Landroid/content/Intent;->putExtra(Ljava/lang/String;I)Landroid/content/Intent;
    const-string v2, "g"
    invoke-virtual {v1, v2, p1}, Landroid/content/Intent;->putExtra(Ljava/lang/String;I)Landroid/content/Intent;
    const-string v2, "b"
    invoke-virtual {v1, v2, p2}, Landroid/content/Intent;->putExtra(Ljava/lang/String;I)Landroid/content/Intent;

    # activity.sendBroadcast(intent)
    invoke-virtual {v0, v1}, Landroid/app/Activity;->sendBroadcast(Landroid/content/Intent;)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "sendColorBroadcast threw"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method
```

That's it. The Cecil patch (`LEDStickBridge` in `patcher/Program.cs`)
needs no change — it still emits the same
`AndroidJavaClass("io.pipboy.thor.LEDBridge").CallStatic("apply", ...)`
sequence.

**Behaviour matrix** after this change:

| flickerd installed? | LED writer | Flicker visible? |
|---|---|---|
| No  | Strip-Boy's existing async-sysfs HandlerThread | No (event-driven only) |
| Yes | Broadcast to flickerd; flickerd ticks at 30 Hz | Yes (Perlin-ish fake flicker, see flickerd/README.md) |

**Note**: the broadcast path is fire-and-forget on the main thread —
`Activity.sendBroadcast` is non-blocking, returns immediately, no GC
pressure beyond the one `Intent` allocation per colour change. Safe to
do in `SetColor`'s tail. Doesn't risk the shader.

## Investigation status

| Tick | Output |
|------|--------|
| 1 | Identified three experiments (A, B, C). Picked SetFloat tee as smallest test. |
| 2 | Sketched A1 Cecil patch (minimal IL, no JNI). Doc committed. |
| 3 | Sketched A2 Cecil (zero-alloc JNI via jvalue[]) and C (sidekick APK skeleton). Doc committed. |
| 4 | Materialized `flickerd/` as buildable directory (~520 LOC, ~10 KB APK). |
| 5 | Wrote Strip-Boy-side smali dispatcher: auto-detects flickerd via PackageManager, chooses broadcast vs sysfs. End-to-end C path is now fully spec'd. |
| 6 | Audit pass — actually ran `flickerd/scripts/build.sh` against JDK 26 + Android SDK build-tools 36.0.0. Caught two real bugs: (1) dead `catch (SettingNotFoundException)` on the 3-arg `Settings.System.getInt` overload (Java rejects "exception never thrown in body" as an error); removed the catch. (2) deprecated `-bootclasspath` produced 3 warnings on JDK 26 — switched to `-classpath` + `-Xlint:-options`. Build now produces a valid 12,715-byte signed APK (verified manifest, classes.dex, V1 signature). Ready to `adb install`. |
| 7 | **A1 implemented + verified.** `FlickerProbeA1` class added to `patcher/Program.cs` (un-registered in patches array by default). dotnet build clean. Patcher ran against `apk/managed/Assembly-CSharp.original.dll` with FlickerProbeA1 enabled — it correctly found Material.SetFloat("\_Brightness", ...) at IL_035C, inserted `dup; stsfld _stripboyLastFB`, and produced a valid 547,840-byte output DLL. Round-trip through the patcher re-reads cleanly and the idempotence check fires ("already installed"). Cecil logic is sound. To run A1 the user just uncomments the registration line in the patches array. |
| 8 | **A2 implemented + verified.** `FlickerSyncA2` class added to `patcher/Program.cs` — the zero-allocation JNI variant (uses cached `IntPtr` classPtr + methodId + pre-allocated `jvalue[1]`, all per-frame work via static fields, no `object[]`, no boxing). Static cctor on PipboyPostEffect extended to allocate the jvalue array once. Compiled and ran against the pristine DLL with FlickerSyncA2 temporarily registered: applied cleanly, found the same IL_035C site, emitted the full 9-instruction dispatch. Inspected the actual injected IL via ilspycmd: every instruction in the right order, branch target resolves to the post-cache-init anchor at IL_0394, all field references valid. Round-trip clean (idempotence fires). Full Strip-Boy build pipeline (apktool decompile → Cecil patch → smali assemble → apktool rebuild → zipalign → apksigner) ran end-to-end with no errors, producing a 39.6 MB APK. Smali side: `applyBrightness(F)V` method added to `patcher/smali/io/pipboy/thor/LEDBridge.smali` (dead code unless A2 is enabled). To run A2 the user uncomments one line in the patches array — A2 subsumes A1 (detects existing A1 tee and skips its redundant dup+stsfld). |

Pick-order when at the keyboard: **A2 → A1 → C** (revised — A2 subsumes A1 and is just as well-verified, so skip the A1 detour unless you specifically want to test "minimal IL injection without JNI"). A1 takes 5 min and
disambiguates the entire investigation.

If you skip straight to C without testing A1/A2: build flickerd
(`flickerd/scripts/build.sh`), install it (`adb install -r out/flickerd.apk`),
paste the smali dispatcher above into
`patcher/smali/io/pipboy/thor/LEDBridge.smali`, rebuild Strip-Boy
(`FORCE_RESTART=1 scripts/build.sh`), install. First launch will
`adb logcat -s strip-boy` confirm with `dispatch = broadcast`.

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
