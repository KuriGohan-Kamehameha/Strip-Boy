// Strip-Boy patcher — surgical IL edits to Bethesda's Fallout 4 Companion
// app so it works as a loopback companion on a single Android device
// (e.g. AYN Thor + GameNative) and behaves cleanly in airplane mode.
//
// Three patches, each idempotent:
//
//   1. LoopbackDiscovery
//        SocketDiscoveryChannel.CoreInitialize gains an extra
//        UDP Send to 127.0.0.1:28000 right after the broadcast Send.
//        Lets the game's listener (bound 0.0.0.0:28000 inside Wine)
//        receive the autodiscover probe via loopback.
//
//   2. SwrveDisabled
//        SwrveSettings.IsValid() returns false.
//        SwrveManager.SendEvent early-returns when IsValid is false,
//        so the entire Swrve analytics path (POSTs to swrve-content.s3.amazonaws.com)
//        is short-circuited.
//
//   3. HockeyAppDisabled
//        HockeyAppSettings.IsValid() returns false.
//        HockeyAppManager.Init disables the iOS+Android crash-report
//        components when IsValid is false, so HockeyApp WWW POSTs
//        (rink.hockeyapp.net — Microsoft sunset that service in 2019)
//        never fire.
//
//   4. AutoPickPC
//        PlatformSelectionMenu.OnFlashReady ends with a call to
//        OnItemSelected((int)eButtonId.PC), so the selection page
//        auto-advances as if the PC button was tapped. The page
//        flashes briefly; we don't suppress the paint, just the wait
//        for input.
//
//   5. SkipWiFiCheck
//        FlowState.CheckForConnectivity.OnEntering normally fires
//        WIFIEnabled only if Application.internetReachability == 2
//        (ReachableViaLocalAreaNetwork), else WIFINotEnabled. That
//        blocks the loopback path in airplane mode without WiFi.
//        Patched to unconditionally fire WIFIEnabled. SocketDiscovery
//        handles a missing LAN interface gracefully.
//
//   6. ShieldBroadcastSend
//        Wraps the broadcast UDP Send in try/catch (Exception). In
//        full airplane mode (no WiFi at all) the broadcast Send can
//        throw SocketException "network unreachable". Without this
//        shield the throw aborts CoreInitialize before the loopback
//        Send or BeginReceive ever run — so the loopback path stays
//        dark exactly when we need it. The catch body just swallows.
//
//   7. AutoPickLoopback
//        IPListMenu.SetPossibleConnections scans the discovered
//        device list; if any entry has IP == "127.0.0.1" and Platform
//        == PC, it calls OnListItemSelected(idx) immediately. Net
//        effect: as soon as discovery returns a loopback responder,
//        the app connects without a tap.
//
//   8. RewriteNoConsoleFoundDesc
//        FontConfigManager.GetText, given the key
//        "$Companion_NoConsoleFoundDesc", returns a GameNative-aware
//        message instead of Bethesda's "check your Fallout 4 Gameplay
//        Settings" text. All other keys fall through unchanged.
//
//   9. AutoPickFullscreenDisplayMode
//        DisplayModeSelectionMenu.OnFlashReady gains a call to
//        OnItemSelected(1) — same shape as patch #4 — which is the
//        "Fullscreen" enum value. Bypasses the one-time
//        "Hardware vs Fullscreen" prompt on first launch. Hardware
//        mode is for the physical Pip-Boy wrist-holder accessory
//        that Bethesda shipped; not relevant on a handheld's flat
//        bottom screen.
//
//  10. HUDColorBridge
//        PipboyStatusManager.UpdatePipboyEffectColor reads the
//        EffectColor PipboyArray (3 doubles) from the game's Status
//        data tree each tick. Bethesda's original guards on
//        `if (member.IsDirty)` so the colour is only re-applied on
//        protocol-flagged delta updates — and uses the strict
//        GetMember overload that throws ExpectedDataMissingException
//        if F4 isn't sending the node in this build.
//
//        The patch rewrites the body to:
//          - use the tolerant GetMember(string, bool) overload
//            (harvested from UpdateMinigameFormIds in the same class)
//          - skip silently if the node is absent
//          - drop the IsDirty gate so the colour applies even when
//            the game updates it without re-marking dirty
//          - Debug.Log once (visible via adb logcat) on first
//            successful receipt, so we can diagnose whether F4 is
//            actually transmitting EffectColor in this build.
//
//        Downstream chain (game → app HUD) is already wired and
//        unchanged: setter writes PlayerPrefs + fires
//        PipboyEffectColorChanged event, CompanionFlashMenu
//        subscribers call PipboyPostEffect.SetColor which sets the
//        shader's _Color uniform.
//
//  11. LEDStickBridge
//        Appends a single call to io.pipboy.thor.LEDBridge.apply
//        at the tail of PipboyPostEffect.SetColor (right before its
//        final ret, after the shader's _Color uniform has been set).
//        The smali helper drives the AYN Thor's SN3112L/R analog-
//        stick LED controllers directly via
//        /sys/class/sn3112{l,r}/led/brightness (world-writable on
//        stock firmware — same path Moonbench's Bifrost utility
//        uses), with brightness mirroring the bottom-screen
//        brightness slider, capped at 70 %.
//
//        Hook point picked deliberately: PipboyPostEffect.SetColor
//        is the single leaf method that mutates the visible screen
//        colour, so the stick colour changes exactly once per
//        screen colour change — no dedupe, no extra ticks. Sits
//        outside Unity's menu Init critical path, so even if the
//        AndroidJavaClass dispatch ever throws, the screen-colour
//        event chain that drives it has already completed.
//
// Nothing else is touched. UI, audio, in-game protocol, asset loading,
// localisation, debug menu — all byte-identical to Bethesda's release.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: pipboy-patcher <input.dll> <output.dll>");
    return 2;
}

var input = args[0];
var output = args[1];

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input not found: {input}");
    return 2;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);

var readerParams = new ReaderParameters
{
    AssemblyResolver = resolver,
    ReadWrite = false,
    InMemory = true,
};

using var asm = AssemblyDefinition.ReadAssembly(input, readerParams);
var module = asm.MainModule;

var patches = new (string Name, Func<ModuleDefinition, PatchResult> Apply)[]
{
    ("LoopbackDiscovery",  LoopbackDiscovery.Apply),
    ("SwrveDisabled",      m => IsValidNeutralizer.ApplyToType(m, "SwrveSettings")),
    ("HockeyAppDisabled",  m => IsValidNeutralizer.ApplyToType(m, "HockeyAppSettings")),
    ("AutoPickPC",                AutoPickPC.Apply),
    ("SkipWiFiCheck",             SkipWiFiCheck.Apply),
    ("ShieldBroadcastSend",       ShieldBroadcastSend.Apply),
    ("AutoPickLoopback",          AutoPickLoopback.Apply),
    ("RewriteNoConsoleFoundDesc", RewriteNoConsoleFoundDesc.Apply),
    ("AutoPickFullscreenMode",    AutoPickFullscreenMode.Apply),
    ("HUDColorBridge",            HUDColorBridge.Apply),
    ("LEDStickBridge",            LEDStickBridge.Apply),
    ("FlickerSeed",               FlickerSeed.Apply),
    ("BurstFeed",                 BurstFeed.Apply),
    ("MenuPulse",                 MenuPulse.Apply),
    // B disabled — on-device test 2026-06-21 affected the shader
    // (gradient banding less severe than the original Update-hook
    // failure, but still visible). Per-frame AJC.CallStatic has
    // non-zero renderer impact even without Material.GetColor.
    // A1 disabled — diagnostic only, no LED behaviour change.
    // A2 disabled — on-device test crashed in libmono.so
    // (mono_class_vtable -> mono_exception_from_name) on first Update tick.
    // Working theory: UnityEngine.AndroidJNI doesn't exist in this Unity
    // 5.x build (only AndroidJavaClass does). Cecil wrote the reference
    // but Mono couldn't resolve it at runtime and SIGSEGV'd trying to
    // throw the corresponding TypeLoad exception.
    // Re-enable AFTER changing FlickerSyncA2 to use AndroidJavaClass
    // (with the boxing cost) instead of raw AndroidJNI.
    //
    // FlickerProbeA1 and FlickerSyncA2 still live in this file as
    // un-registered classes. See docs/FLICKER_EXPERIMENTS.md.
};

var anyChanged = false;
foreach (var (name, apply) in patches)
{
    try
    {
        var result = apply(module);
        Console.WriteLine($"[{(result.Changed ? "patch" : "skip ")}] {name}: {result.Message}");
        anyChanged |= result.Changed;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FAIL ] {name}: {ex.Message}");
        return 1;
    }
}

if (!anyChanged)
{
    Console.WriteLine("All patches already applied. Copying input to output unchanged.");
}

asm.Write(output);
Console.WriteLine($"Wrote {output}");
return 0;


/* ===================================================================== */

readonly record struct PatchResult(bool Changed, string Message);

// MenuPulse — flash the analog-stick LEDs brighter for a beat whenever the
// user navigates between Pip-Boy pages/tabs. Injects a no-arg
// LEDBridge.menuPulse() call at the top of PipboyMenuMovie::onNewPage and
// ::onNewTab (the page/tab navigation entry points, each of which already
// plays a rotary nav sound). menuPulse() (Java side) sends a brief brighter
// PIPBOY command to Bifrost then returns to the resting level.
static class MenuPulse
{
    const string BridgeClassFqn = "io.pipboy.thor.LEDBridge";

    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyMenuMovie")
            ?? throw new Exception("PipboyMenuMovie type not found");

        var unityRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "UnityEngine")
            ?? throw new Exception("UnityEngine assembly reference not present");
        var ajcType = new TypeReference("UnityEngine", "AndroidJavaClass",
            module, unityRef, valueType: false);
        var ajcCtor = new MethodReference(".ctor", module.TypeSystem.Void, ajcType) { HasThis = true };
        ajcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var callStatic = new MethodReference("CallStatic", module.TypeSystem.Void, ajcType) { HasThis = true };
        callStatic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        callStatic.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));

        int hooked = 0;
        foreach (var methodName in new[] { "onNewPage", "onNewTab" })
        {
            var m = type.Methods.FirstOrDefault(x => x.Name == methodName && x.Parameters.Count == 1);
            if (m is null || m.Body is null) continue;

            // Idempotence: skip if our menuPulse ldstr is already present.
            if (m.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Ldstr && (i.Operand as string) == "menuPulse"))
                continue;

            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            // new AndroidJavaClass("io.pipboy.thor.LEDBridge").CallStatic("menuPulse", new object[0]);
            var seq = new[]
            {
                il.Create(OpCodes.Ldstr, BridgeClassFqn),
                il.Create(OpCodes.Newobj, ajcCtor),
                il.Create(OpCodes.Ldstr, "menuPulse"),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Newarr, module.TypeSystem.Object),
                il.Create(OpCodes.Callvirt, callStatic),
            };
            foreach (var ins in seq) il.InsertBefore(first, ins);
            if (m.Body.MaxStackSize < 4) m.Body.MaxStackSize = 4;
            hooked++;
        }

        // Also hook PipboyPostEffect.TriggerVHold → LEDBridge.staticBurst().
        // TriggerVHold is the dramatic vertical-hold "channel swap" roll
        // (fired 5% of page switches, always on world/local-map switch, and on
        // menu open) — mirror it on the sticks with a TV-static scramble.
        var ppe = module.GetType("PipboyPostEffect");
        var vhold = ppe?.Methods.FirstOrDefault(x => x.Name == "TriggerVHold" && x.Parameters.Count == 0);
        if (vhold?.Body != null
            && !vhold.Body.Instructions.Any(i =>
                   i.OpCode == OpCodes.Ldstr && (i.Operand as string) == "staticBurst"))
        {
            var il = vhold.Body.GetILProcessor();
            var first = vhold.Body.Instructions[0];
            var seq = new[]
            {
                il.Create(OpCodes.Ldstr, BridgeClassFqn),
                il.Create(OpCodes.Newobj, ajcCtor),
                il.Create(OpCodes.Ldstr, "staticBurst"),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Newarr, module.TypeSystem.Object),
                il.Create(OpCodes.Callvirt, callStatic),
            };
            foreach (var ins in seq) il.InsertBefore(first, ins);
            if (vhold.Body.MaxStackSize < 4) vhold.Body.MaxStackSize = 4;
            hooked++;
        }

        if (hooked == 0) return new(false, "MenuPulse already installed (or hook targets absent)");
        return new(true, $"LED reactions injected into {hooked} site(s) "
          + "(PipboyMenuMovie nav → menuPulse, PipboyPostEffect.TriggerVHold → staticBurst)");
    }
}

// FlickerSeed — make the screen's flicker deterministic + seed-matched with
// the Bifrost PIPBOY plugin, so the stick LEDs reproduce the screen's exact
// flicker sequence with no runtime signalling.
//
// Three injections into PipboyPostEffect:
//   1. A dedicated seeded LCG (static int _stripboyFlickerRng = SEED) plus a
//      _stripboyFlickerRange(float,float) method — bit-identical maths to the
//      plugin's seededRange (Numerical-Recipes LCG, value/2^32).
//   2. Redirect the flicker Random.Range calls in Update (the duration/delay
//      draws + the burst-chance roll, isolated to the window between the
//      bFlickering toggle and the first PerlinNoise) to that seeded method.
//      Call-target swap only — A1-class IL, which the renderer tolerates.
//   3. Tee the per-frame fTime into a public static _stripboyFTime (read by
//      LEDBridge.smali) so the plugin can fast-forward to the screen's clock.
//
// SEED + LCG constants MUST equal PipBoyAnimation.FLICKER_SEED/LCG_MUL/LCG_ADD.
static class FlickerSeed
{
    const int SEED = 0x50B0FF;        // flicker RNG seed (== PipBoyAnimation.FLICKER_SEED)
    const int VSCAN_SEED = 0xC0FFEE;  // vscan RNG seed  (== PipBoyAnimation.VSCAN_SEED)
    const int LCG_MUL = 1664525;
    const int LCG_ADD = 1013904223;

    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");
        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("PipboyPostEffect::Update() not found");

        const string rngFieldName = "_stripboyFlickerRng";
        if (type.Fields.Any(f => f.Name == rngFieldName))
            return new(false, "FlickerSeed already installed");

        // ---- fields ----
        var rngField = new FieldDefinition(rngFieldName,
            FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
        type.Fields.Add(rngField);
        var vscanRngField = new FieldDefinition("_stripboyVScanRng",
            FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
        type.Fields.Add(vscanRngField);
        var fTimeField = new FieldDefinition("_stripboyFTime",
            FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Single);
        type.Fields.Add(fTimeField);

        // ---- seed both RNGs in <cctor> ----
        var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor is null)
        {
            cctor = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
                | MethodAttributes.HideBySig, module.TypeSystem.Void);
            cctor.Body = new MethodBody(cctor);
            cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(cctor);
        }
        var cil = cctor.Body.GetILProcessor();
        var cret = cctor.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        cil.InsertBefore(cret, cil.Create(OpCodes.Ldc_I4, SEED));
        cil.InsertBefore(cret, cil.Create(OpCodes.Stsfld, rngField));
        cil.InsertBefore(cret, cil.Create(OpCodes.Ldc_I4, VSCAN_SEED));
        cil.InsertBefore(cret, cil.Create(OpCodes.Stsfld, vscanRngField));
        if (cctor.Body.MaxStackSize < 1) cctor.Body.MaxStackSize = 1;

        // ---- re-seed both RNGs at the start of Awake ----
        // fTime is monotonic within an instance (only ever += deltaTime; never
        // reassigned), so it can only return to 0 on a FRESH PipboyPostEffect
        // instance. These RNG fields are static — they outlive the instance — so
        // without this, a new instance starts its fTime=0 schedule on the previous
        // instance's advanced RNG state, desyncing from the plugin (which replays
        // from seed whenever it re-anchors). Re-seeding on Awake makes every
        // instance restart the seeded flicker/vscan schedule from scratch, exactly
        // matching the plugin's setPhaseOrigin(0). fFlickerDelay (=5f) and fTime
        // (=0) reset via their own field initializers in the instance ctor.
        var awake = type.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0);
        if (awake != null && awake.Body.Instructions.Count > 0)
        {
            var ail = awake.Body.GetILProcessor();
            var afirst = awake.Body.Instructions.First();
            ail.InsertBefore(afirst, ail.Create(OpCodes.Ldc_I4, SEED));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Stsfld, rngField));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Ldc_I4, VSCAN_SEED));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Stsfld, vscanRngField));
            // DIAGNOSTIC (remove after): log each Awake + instance id, to map the
            // PipboyPostEffect lifecycle. If these fire mid-session (while SCRNFLK's
            // fTime climbs continuously) a transient instance is re-seeding the
            // shared RNG out from under the visible one — the residual-drift cause.
            var unityRef2 = module.AssemblyReferences.First(a => a.Name == "UnityEngine");
            var objType = new TypeReference("UnityEngine", "Object", module, unityRef2, valueType: false);
            var getInstId = new MethodReference("GetInstanceID", module.TypeSystem.Int32, objType) { HasThis = true };
            var dbgType = new TypeReference("UnityEngine", "Debug", module, unityRef2, valueType: false);
            var dbgLog = new MethodReference("Log", module.TypeSystem.Void, dbgType);
            dbgLog.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            var concatOO = new MethodReference("Concat", module.TypeSystem.String, module.TypeSystem.String);
            concatOO.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            concatOO.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Ldstr, "STRIPBOY_AWAKE id="));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Ldarg_0));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Call, getInstId));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Box, module.TypeSystem.Int32));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Call, concatOO));
            ail.InsertBefore(afirst, ail.Create(OpCodes.Call, dbgLog));
            if (awake.Body.MaxStackSize < 2) awake.Body.MaxStackSize = 2;
        }

        // ---- LCG range method builder: float Range(float lo, float hi) over
        //      the given seeded int field. Bit-identical to PipBoyAnimation's
        //      seededRange/vscanRange (value/2^32). ----
        MethodDefinition BuildRange(string name, FieldDefinition rf, string? logTag)
        {
            var m = new MethodDefinition(name,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Single);
            m.Parameters.Add(new ParameterDefinition("lo", ParameterAttributes.None, module.TypeSystem.Single));
            m.Parameters.Add(new ParameterDefinition("hi", ParameterAttributes.None, module.TypeSystem.Single));
            m.Body = new MethodBody(m);
            m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Double)); // [0] frac
            m.Body.InitLocals = true;
            var il = m.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldsfld, rf));
            il.Append(il.Create(OpCodes.Ldc_I4, LCG_MUL));
            il.Append(il.Create(OpCodes.Mul));
            il.Append(il.Create(OpCodes.Ldc_I4, LCG_ADD));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stsfld, rf));
            // DIAGNOSTIC (drop-proof RNG compare): log the post-advance state int,
            // matched by VALUE against the plugin's PRNG sequence (immune to
            // Unity Debug.Log drops). Flicker only — vscan passes logTag null.
            if (logTag != null)
            {
                var uref = module.AssemblyReferences.First(a => a.Name == "UnityEngine");
                var dt = new TypeReference("UnityEngine", "Debug", module, uref, valueType: false);
                var dl = new MethodReference("Log", module.TypeSystem.Void, dt);
                dl.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                var cc = new MethodReference("Concat", module.TypeSystem.String, module.TypeSystem.String);
                cc.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                cc.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                il.Append(il.Create(OpCodes.Ldstr, logTag));
                il.Append(il.Create(OpCodes.Ldsfld, rf));
                il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
                il.Append(il.Create(OpCodes.Call, cc));
                il.Append(il.Create(OpCodes.Call, dl));
            }
            il.Append(il.Create(OpCodes.Ldsfld, rf));
            // frac = (unsigned 32-bit)_rng / 2^32. conv.u8 alone is ambiguous
            // for a negative int32 (it sign-extends to int64 first, blowing the
            // value up to ~1.8e19); mask the low 32 bits explicitly so this
            // matches the plugin's UInt.toDouble()/2^32 bit-for-bit.
            il.Append(il.Create(OpCodes.Conv_I8));            // sign-extend int32 → int64
            il.Append(il.Create(OpCodes.Ldc_I8, 0xFFFFFFFFL)); // mask low 32 bits
            il.Append(il.Create(OpCodes.And));                // → 0 .. 2^32-1
            il.Append(il.Create(OpCodes.Conv_R8));
            il.Append(il.Create(OpCodes.Ldc_R8, 4294967296.0));
            il.Append(il.Create(OpCodes.Div));
            il.Append(il.Create(OpCodes.Stloc_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Conv_R8));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Conv_R8));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Conv_R8));
            il.Append(il.Create(OpCodes.Sub));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Mul));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Conv_R4));
            il.Append(il.Create(OpCodes.Ret));
            m.Body.MaxStackSize = 4;
            type.Methods.Add(m);
            return m;
        }

        var range = BuildRange("_stripboyFlickerRange", rngField, null);
        var vrange = BuildRange("_stripboyVScanRange", vscanRngField, null);

        // ---- tee fTime: after `stfld fTime` near the top of Update ----
        var instrs = update.Body.Instructions;
        Instruction? fTimeStore = instrs.FirstOrDefault(i =>
            i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr && fr.Name == "fTime");
        if (fTimeStore is null) throw new Exception("fTime store not found in Update");
        var uil = update.Body.GetILProcessor();
        var teeC = uil.Create(OpCodes.Stsfld, fTimeField);
        var teeB = uil.Create(OpCodes.Ldfld, new FieldReference("fTime", module.TypeSystem.Single, type));
        var teeA = uil.Create(OpCodes.Ldarg_0);
        uil.InsertAfter(fTimeStore, teeA);
        uil.InsertAfter(teeA, teeB);
        uil.InsertAfter(teeB, teeC);

        // ---- redirect flicker Random.Range calls ----
        // Window: from the bFlickering toggle (stfld bFlickering) to the first
        // Mathf.PerlinNoise call. Excludes the earlier vscan Random.Range.
        int toggleIdx = -1, perlinIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            var ins = instrs[i];
            if (toggleIdx < 0 && ins.OpCode == OpCodes.Stfld
                && ins.Operand is FieldReference bf && bf.Name == "bFlickering")
                toggleIdx = i;
            if (toggleIdx >= 0 && ins.Operand is MethodReference pm
                && pm.Name == "PerlinNoise") { perlinIdx = i; break; }
        }
        if (toggleIdx < 0 || perlinIdx < 0)
            throw new Exception("flicker window (bFlickering..PerlinNoise) not found");

        static bool IsRandomRangeFF(Instruction ins) =>
            (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
            && ins.Operand is MethodReference mr
            && mr.Name == "Range" && mr.DeclaringType.Name == "Random"
            && mr.Parameters.Count == 2
            && mr.Parameters[0].ParameterType.MetadataType == MetadataType.Single;

        // Flicker draws: the Random.Range calls between the bFlickering toggle
        // and the first PerlinNoise → seeded flicker RNG.
        int redirected = 0;
        for (int i = toggleIdx; i < perlinIdx; i++)
        {
            if (IsRandomRangeFF(instrs[i]))
            {
                instrs[i].OpCode = OpCodes.Call;
                instrs[i].Operand = range;
                redirected++;
            }
        }
        if (redirected == 0) throw new Exception("no flicker Random.Range calls redirected");

        // VScan draw: the Random.Range BEFORE the flicker block (fVScanDelay =
        // Random.Range(min,max)) → seeded vscan RNG. Independent sequence so it
        // never perturbs the flicker draws.
        int vredirected = 0;
        for (int i = 0; i < toggleIdx; i++)
        {
            if (IsRandomRangeFF(instrs[i]))
            {
                instrs[i].OpCode = OpCodes.Call;
                instrs[i].Operand = vrange;
                vredirected++;
            }
        }

        // AndroidJavaClass refs (Unity 5 has no raw AndroidJNI — only this path)
        // + cached class + visible-instance gate, shared by the flicker hook.
        var ajcUnityRef = module.AssemblyReferences.First(a => a.Name == "UnityEngine");
        var ajcType = new TypeReference("UnityEngine", "AndroidJavaClass", module, ajcUnityRef, valueType: false);
        var ajcCtor = new MethodReference(".ctor", module.TypeSystem.Void, ajcType) { HasThis = true };
        ajcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var callStatic = new MethodReference("CallStatic", module.TypeSystem.Void, ajcType) { HasThis = true };
        callStatic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        callStatic.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));

        // Reuse the cached AndroidJavaClass LEDStickBridge created (it patches
        // first); create it if this patch ever runs standalone.
        const string cachedFieldName = "_stripboyLedBridgeCls";
        var cachedField = type.Fields.FirstOrDefault(f => f.Name == cachedFieldName);
        if (cachedField is null)
        {
            cachedField = new FieldDefinition(cachedFieldName,
                FieldAttributes.Private | FieldAttributes.Static, ajcType);
            type.Fields.Add(cachedField);
        }

        var objTypeH = new TypeReference("UnityEngine", "Object", module, ajcUnityRef, valueType: false);
        var getInstIdH = new MethodReference("GetInstanceID", module.TypeSystem.Int32, objTypeH) { HasThis = true };
        var visibleIdFieldH = type.Fields.FirstOrDefault(f => f.Name == "_stripboyVisibleInstanceId")
            ?? throw new Exception("_stripboyVisibleInstanceId missing — LEDStickBridge must patch before FlickerSeed");
        var hil = update.Body.GetILProcessor();

        // ---- flicker toggle feed: on each bFlickering flip (visible instance),
        //      call LEDBridge.onFlickerToggle(bFlickering). Same event-feed model
        //      as onVScanRoll — the flicker schedule can't be seed-replayed (game
        //      triggers + multi-instance draws on the shared RNG), so feed the
        //      actual on/off state. Injected after the stfld bFlickering (stack
        //      empty); gated to the visible instance.
        var flickerToggle = instrs.FirstOrDefault(i =>
            i.OpCode == OpCodes.Stfld && i.Operand is FieldReference bf2 && bf2.Name == "bFlickering")
            ?? throw new Exception("stfld bFlickering not found for flicker feed");
        var bFlickeringRef = new FieldReference("bFlickering", module.TypeSystem.Boolean, type);
        var afterFlicker = flickerToggle.Next
            ?? throw new Exception("no instruction after bFlickering toggle");
        var flCachedLoad = hil.Create(OpCodes.Ldsfld, cachedField);
        var flickerHook = new List<Instruction>
        {
            // if (this.GetInstanceID() != _stripboyVisibleInstanceId) goto afterFlicker;
            hil.Create(OpCodes.Ldarg_0),
            hil.Create(OpCodes.Call, getInstIdH),
            hil.Create(OpCodes.Ldsfld, visibleIdFieldH),
            hil.Create(OpCodes.Bne_Un, afterFlicker),
            // ensure cached AndroidJavaClass
            hil.Create(OpCodes.Ldsfld, cachedField),
            hil.Create(OpCodes.Brtrue, flCachedLoad),
            hil.Create(OpCodes.Ldstr, "io.pipboy.thor.LEDBridge"),
            hil.Create(OpCodes.Newobj, ajcCtor),
            hil.Create(OpCodes.Stsfld, cachedField),
            flCachedLoad,
            // CallStatic("onFlickerToggle", new object[]{ (object)this.bFlickering })
            hil.Create(OpCodes.Ldstr, "onFlickerToggle"),
            hil.Create(OpCodes.Ldc_I4_1),
            hil.Create(OpCodes.Newarr, module.TypeSystem.Object),
            hil.Create(OpCodes.Dup),
            hil.Create(OpCodes.Ldc_I4_0),
            hil.Create(OpCodes.Ldarg_0),
            hil.Create(OpCodes.Ldfld, bFlickeringRef),
            hil.Create(OpCodes.Box, module.TypeSystem.Boolean),
            hil.Create(OpCodes.Stelem_Ref),
            hil.Create(OpCodes.Callvirt, callStatic),
        };
        Instruction flPrev = flickerToggle;
        foreach (var ins in flickerHook) { hil.InsertAfter(flPrev, ins); flPrev = ins; }

        if (update.Body.MaxStackSize < 8) update.Body.MaxStackSize = 8;

        return new(true,
            $"PipboyPostEffect: {redirected} flicker + {vredirected} vscan "
          + $"Random.Range redirected; onFlickerToggle hook installed (visible instance)");
    }
}

// BurstFeed — feeds LEDBridge.onBurst() from PipboyPostEffect.TriggerBurst, so
// the plugin flashes when the screen bursts. TriggerBurst is the single funnel
// for all bursts (the Update 15%-roll AND game-triggered Large/SmallBurst), so
// hooking it catches every flash. Gated to the visible instance (id recorded by
// LEDStickBridge). Event-driven (~rare), reuses the apply() JNI path.
static class BurstFeed
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");
        var method = type.Methods.FirstOrDefault(m =>
            m.Name == "TriggerBurst" && m.Parameters.Count == 2)
            ?? throw new Exception("PipboyPostEffect::TriggerBurst(float,float) not found");
        if (method.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldstr && (i.Operand as string) == "onBurst"))
            return new(false, "BurstFeed already installed");

        var unityRef = module.AssemblyReferences.First(a => a.Name == "UnityEngine");
        var ajcType = new TypeReference("UnityEngine", "AndroidJavaClass", module, unityRef, valueType: false);
        var ajcCtor = new MethodReference(".ctor", module.TypeSystem.Void, ajcType) { HasThis = true };
        ajcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var callStatic = new MethodReference("CallStatic", module.TypeSystem.Void, ajcType) { HasThis = true };
        callStatic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        callStatic.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));
        var objType = new TypeReference("UnityEngine", "Object", module, unityRef, valueType: false);
        var getInstId = new MethodReference("GetInstanceID", module.TypeSystem.Int32, objType) { HasThis = true };
        var visibleIdField = type.Fields.FirstOrDefault(f => f.Name == "_stripboyVisibleInstanceId")
            ?? throw new Exception("_stripboyVisibleInstanceId missing — LEDStickBridge must patch before BurstFeed");
        var cachedField = type.Fields.FirstOrDefault(f => f.Name == "_stripboyLedBridgeCls")
            ?? throw new Exception("_stripboyLedBridgeCls missing — LEDStickBridge must patch before BurstFeed");

        var il = method.Body.GetILProcessor();
        var ret = method.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? throw new Exception("TriggerBurst has no ret");
        var cachedLoad = il.Create(OpCodes.Ldsfld, cachedField);
        var seq = new List<Instruction>
        {
            // if (this.GetInstanceID() != _stripboyVisibleInstanceId) return;
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, getInstId),
            il.Create(OpCodes.Ldsfld, visibleIdField),
            il.Create(OpCodes.Bne_Un, ret),
            il.Create(OpCodes.Ldsfld, cachedField),
            il.Create(OpCodes.Brtrue, cachedLoad),
            il.Create(OpCodes.Ldstr, "io.pipboy.thor.LEDBridge"),
            il.Create(OpCodes.Newobj, ajcCtor),
            il.Create(OpCodes.Stsfld, cachedField),
            cachedLoad,
            il.Create(OpCodes.Ldstr, "onBurst"),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Newarr, module.TypeSystem.Object),
            il.Create(OpCodes.Callvirt, callStatic),
        };
        foreach (var ins in seq) il.InsertBefore(ret, ins);
        if (method.Body.MaxStackSize < 4) method.Body.MaxStackSize = 4;
        return new(true, "PipboyPostEffect::TriggerBurst now feeds LEDBridge.onBurst() (visible instance)");
    }
}

// FlickerToggleLog — DIAGNOSTIC. Logs "SCRNFLK <fTime>" via Unity Debug.Log
// each time PipboyPostEffect.Update flips bFlickering. Paired with the
// plugin's PLUGFLK log, this is a hard yes/no on whether the seeded flicker
// schedules are bit-matched: if SCRNFLK and PLUGFLK fire at the same fTime /
// logcat instant, they're in lockstep. Low-rate (toggles ~every 5-15s), safe
// (Unity logging, no per-frame JNI). Remove once the question is answered.
static class FlickerToggleLog
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");
        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("PipboyPostEffect::Update() not found");

        var instrs = update.Body.Instructions;
        // Idempotence: our injected ldstr "SCRNFLK " marks it.
        if (instrs.Any(i => i.OpCode == OpCodes.Ldstr && (i.Operand as string) == "SCRNFLK "))
            return new(false, "FlickerToggleLog already installed");

        var unityRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "UnityEngine")
            ?? throw new Exception("UnityEngine assembly reference not present");
        var debugType = new TypeReference("UnityEngine", "Debug", module, unityRef, valueType: false);
        var debugLog = new MethodReference("Log", module.TypeSystem.Void, debugType);
        debugLog.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

        var fTimeRef = new FieldReference("fTime", module.TypeSystem.Single, type);
        var bFlickRef = new FieldReference("bFlickering", module.TypeSystem.Boolean, type);
        // Single.ToString() — instance method, needs the field address (ldflda).
        var singleToString = new MethodReference("ToString", module.TypeSystem.String, module.TypeSystem.Single)
            { HasThis = true };
        // String.Concat(string, string) — static.
        var concat = new MethodReference("Concat", module.TypeSystem.String, module.TypeSystem.String);
        concat.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        concat.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        // Find the bFlickering toggle (stfld bFlickering) in Update.
        Instruction? toggle = instrs.FirstOrDefault(i =>
            i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr && fr.Name == "bFlickering");
        if (toggle is null) throw new Exception("stfld bFlickering not found in Update");

        var il = update.Body.GetILProcessor();
        // Debug.Log(string.Concat("SCRNFLK ", this.fTime.ToString()));
        var seq = new[]
        {
            il.Create(OpCodes.Ldstr, "SCRNFLK "),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldflda, fTimeRef),
            il.Create(OpCodes.Call, singleToString),
            il.Create(OpCodes.Call, concat),
            il.Create(OpCodes.Call, debugLog),
        };
        var cursor = toggle;
        foreach (var ins in seq) { il.InsertAfter(cursor, ins); cursor = ins; }

        if (update.Body.MaxStackSize < 3) update.Body.MaxStackSize = 3;
        return new(true, "PipboyPostEffect::Update logs SCRNFLK <fTime> on each bFlickering toggle");
    }
}

static class LoopbackDiscovery
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("SocketDiscoveryChannel")
            ?? throw new Exception("SocketDiscoveryChannel type not found");

        var method = type.Methods.FirstOrDefault(m => m.Name == "CoreInitialize")
            ?? throw new Exception("SocketDiscoveryChannel::CoreInitialize not found");

        var discoverySocketField = type.Fields.FirstOrDefault(f => f.Name == "_discoverySocket")
            ?? throw new Exception("_discoverySocket field not found");

        // Steal existing references from the method body so signatures match
        // the exact runtime assemblies this DLL was compiled against.
        FieldReference? broadcastFieldRef = null;
        MethodReference? ipEndPointCtorRef = null;
        MethodReference? sendMethodRef = null;

        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldReference fr
                && fr.Name == "Broadcast" && fr.DeclaringType.Name == "IPAddress")
                broadcastFieldRef = fr;
            else if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor
                && ctor.DeclaringType.Name == "IPEndPoint" && ctor.Name == ".ctor")
                ipEndPointCtorRef = ctor;
            else if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference call
                && call.DeclaringType.Name == "UdpClient" && call.Name == "Send"
                && call.Parameters.Count == 3)
                sendMethodRef = call;
        }

        if (broadcastFieldRef == null) throw new Exception("Couldn't find IPAddress.Broadcast ref");
        if (ipEndPointCtorRef == null) throw new Exception("Couldn't find IPEndPoint::.ctor ref");
        if (sendMethodRef == null) throw new Exception("Couldn't find UdpClient::Send(3-arg) ref");

        var loopbackFieldRef = new FieldReference(
            "Loopback",
            broadcastFieldRef.FieldType,
            broadcastFieldRef.DeclaringType);

        // Find insertion point: the first (Callvirt Send) + Pop pair.
        Instruction? insertAfter = null;
        var ins0 = method.Body.Instructions;
        for (var i = 0; i < ins0.Count - 1; i++)
        {
            if (ins0[i].OpCode == OpCodes.Callvirt
                && ins0[i].Operand is MethodReference mr
                && mr.DeclaringType.Name == "UdpClient" && mr.Name == "Send"
                && ins0[i + 1].OpCode == OpCodes.Pop)
            {
                insertAfter = ins0[i + 1];
                break;
            }
        }
        if (insertAfter == null) throw new Exception("Couldn't find existing Send+Pop pair");

        // Idempotence: if the next instructions match our injection, skip.
        if (LooksAlreadyPatched(insertAfter))
            return new(false, "already patched");

        var il = method.Body.GetILProcessor();
        var toInsert = new[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldfld, discoverySocketField),
            il.Create(OpCodes.Ldloc_1),                           // bytes
            il.Create(OpCodes.Ldloc_1),
            il.Create(OpCodes.Ldlen),
            il.Create(OpCodes.Conv_I4),                           // bytes.Length
            il.Create(OpCodes.Ldsfld, loopbackFieldRef),
            il.Create(OpCodes.Ldc_I4, 28000),
            il.Create(OpCodes.Newobj, ipEndPointCtorRef),
            il.Create(OpCodes.Callvirt, sendMethodRef),
            il.Create(OpCodes.Pop),
        };

        var anchor = insertAfter;
        foreach (var newIns in toInsert)
        {
            il.InsertAfter(anchor, newIns);
            anchor = newIns;
        }

        return new(true, $"inserted {toInsert.Length} IL after IL_{insertAfter.Offset:X4}");
    }

    static bool LooksAlreadyPatched(Instruction insertAfter)
    {
        var probe = insertAfter.Next;
        for (var i = 0; i < 12 && probe != null; i++)
        {
            if (probe.OpCode == OpCodes.Ldsfld
                && probe.Operand is FieldReference lf
                && lf.Name == "Loopback")
                return true;
            probe = probe.Next;
        }
        return false;
    }
}

static class IsValidNeutralizer
{
    public static PatchResult ApplyToType(ModuleDefinition module, string typeName)
    {
        var type = module.GetType(typeName)
            ?? throw new Exception($"{typeName} type not found");
        return ReplaceBody(type, "IsValid");
    }

    // Replace MethodName's body with `return false` (ldc.i4.0; ret).
    // Idempotent: if the body is already exactly that, no change.
    public static PatchResult ReplaceBody(TypeDefinition type, string methodName)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName)
            ?? throw new Exception($"{type.Name}::{methodName} not found");

        if (method.ReturnType.MetadataType != MetadataType.Boolean)
            throw new Exception($"{type.Name}::{methodName} return type is {method.ReturnType.Name}, expected Boolean");

        var body = method.Body;
        var ins = body.Instructions;

        if (ins.Count == 2
            && ins[0].OpCode == OpCodes.Ldc_I4_0
            && ins[1].OpCode == OpCodes.Ret)
        {
            return new(false, $"{type.Name}::{methodName} already returns false");
        }

        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        ins.Clear();

        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 1;

        return new(true, $"{type.Name}::{methodName} body replaced with `return false`");
    }
}

static class AutoPickPC
{
    public static PatchResult Apply(ModuleDefinition module) =>
        FlashMenuAutoPicker.Inject(module, "PlatformSelectionMenu", 0, "PC");
}

static class AutoPickFullscreenMode
{
    public static PatchResult Apply(ModuleDefinition module) =>
        FlashMenuAutoPicker.Inject(module, "DisplayModeSelectionMenu", 1, "FullscreenMode");
}

// Shared body for the AutoPick* patches: append OnItemSelected(value) to the
// end of OnFlashReady on the given Scaleform menu type. Re-uses the menu's
// own OnItemSelected (so we mimic the exact button-tap code path).
static class FlashMenuAutoPicker
{
    public static PatchResult Inject(ModuleDefinition module, string typeName, int itemId, string label)
    {
        var type = module.GetType(typeName)
            ?? throw new Exception($"{typeName} type not found");

        var onReady = type.Methods.FirstOrDefault(m => m.Name == "OnFlashReady")
            ?? throw new Exception($"{typeName}::OnFlashReady not found");
        var onItemSelected = type.Methods.FirstOrDefault(m =>
            m.Name == "OnItemSelected" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
            ?? throw new Exception($"{typeName}::OnItemSelected(int32) not found");

        // Idempotence: if we've already injected a call to OnItemSelected, bail.
        foreach (var ins in onReady.Body.Instructions)
        {
            if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call)
                && ins.Operand is MethodReference mr
                && mr.Resolve() == onItemSelected)
            {
                return new(false, $"{typeName}::OnFlashReady already auto-picks");
            }
        }

        var body = onReady.Body;
        var instructions = body.Instructions;
        var lastRet = instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? throw new Exception($"{typeName}::OnFlashReady has no ret");

        var il = body.GetILProcessor();
        il.InsertBefore(lastRet, il.Create(OpCodes.Ldarg_0));                // this
        il.InsertBefore(lastRet, OpCodeForIntConstant(il, itemId));          // itemID
        il.InsertBefore(lastRet, il.Create(OpCodes.Callvirt, onItemSelected));

        return new(true, $"{typeName}::OnFlashReady now auto-calls OnItemSelected({itemId}={label})");
    }

    static Instruction OpCodeForIntConstant(ILProcessor il, int v) => v switch
    {
        0 => il.Create(OpCodes.Ldc_I4_0),
        1 => il.Create(OpCodes.Ldc_I4_1),
        2 => il.Create(OpCodes.Ldc_I4_2),
        3 => il.Create(OpCodes.Ldc_I4_3),
        _ => il.Create(OpCodes.Ldc_I4, v),
    };
}

static class SkipWiFiCheck
{
    // Find the FlowState lambda whose body uses Application.internetReachability
    // and fires FlowTrigger.WIFIEnabled / WIFINotEnabled. Replace its body
    // with: ldsfld FlowTrigger::WIFIEnabled; callvirt Fire(); ret.
    public static PatchResult Apply(ModuleDefinition module)
    {
        MethodDefinition? target = null;
        FieldReference? wifiEnabledField = null;
        MethodReference? fireMethod = null;

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var usesReach = false;
                FieldReference? localWifiEnabled = null;
                MethodReference? localFire = null;

                foreach (var ins in method.Body.Instructions)
                {
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                        && ins.Operand is MethodReference mr1
                        && mr1.Name == "get_internetReachability"
                        && mr1.DeclaringType.Name == "Application")
                        usesReach = true;

                    if (ins.OpCode == OpCodes.Ldsfld
                        && ins.Operand is FieldReference fr
                        && fr.DeclaringType.Name == "FlowTrigger"
                        && fr.Name == "WIFIEnabled")
                        localWifiEnabled = fr;

                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                        && ins.Operand is MethodReference mr2
                        && mr2.DeclaringType.Name == "FlowTrigger"
                        && mr2.Name == "Fire")
                        localFire = mr2;
                }

                if (usesReach && localWifiEnabled != null && localFire != null)
                {
                    if (target != null)
                        throw new Exception($"ambiguous: {target.FullName} and {method.FullName} both look like the WiFi gate");
                    target = method;
                    wifiEnabledField = localWifiEnabled;
                    fireMethod = localFire;
                }
            }
        }

        if (target == null)
        {
            // Defense in depth: if we can't find the original WiFi-gate
            // (uses internetReachability AND fires WIFIEnabled/Fire), check
            // whether some method has the exact 3-instruction body that this
            // patch produces. If so, we've already patched.
            foreach (var t in module.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var bodyIns = m.Body.Instructions;
                    if (bodyIns.Count == 3
                        && bodyIns[0].OpCode == OpCodes.Ldsfld
                        && bodyIns[0].Operand is FieldReference lf
                        && lf.DeclaringType.Name == "FlowTrigger" && lf.Name == "WIFIEnabled"
                        && (bodyIns[1].OpCode == OpCodes.Callvirt || bodyIns[1].OpCode == OpCodes.Call)
                        && bodyIns[1].Operand is MethodReference lm
                        && lm.DeclaringType.Name == "FlowTrigger" && lm.Name == "Fire"
                        && bodyIns[2].OpCode == OpCodes.Ret)
                    {
                        return new(false, $"{t.Name}::{m.Name} already unconditionally fires WIFIEnabled");
                    }
                }
            }
            throw new Exception("Couldn't find WiFi-gate lambda (not original, not patched)");
        }

        // Idempotence: body is already exactly {Ldsfld WIFIEnabled, Callvirt Fire, Ret}.
        var body = target.Body;
        var existing = body.Instructions;
        if (existing.Count == 3
            && existing[0].OpCode == OpCodes.Ldsfld
            && existing[0].Operand is FieldReference ef && ef.Name == "WIFIEnabled"
            && (existing[1].OpCode == OpCodes.Callvirt || existing[1].OpCode == OpCodes.Call)
            && existing[2].OpCode == OpCodes.Ret)
        {
            return new(false, $"{target.FullName} already unconditionally fires WIFIEnabled");
        }

        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        existing.Clear();

        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldsfld, wifiEnabledField));
        il.Append(il.Create(OpCodes.Callvirt, fireMethod));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 1;

        return new(true, $"{target.DeclaringType.Name}::{target.Name} body replaced with `WIFIEnabled.Fire()`");
    }
}

static class ShieldBroadcastSend
{
    // Wrap the existing broadcast UdpClient.Send (the first Send+Pop pair in
    // SocketDiscoveryChannel.CoreInitialize) in a try/catch(Exception) that
    // swallows the exception. The pop+leave keeps the stack balanced.
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("SocketDiscoveryChannel")
            ?? throw new Exception("SocketDiscoveryChannel type not found");
        var method = type.Methods.FirstOrDefault(m => m.Name == "CoreInitialize")
            ?? throw new Exception("CoreInitialize not found");

        var body = method.Body;
        var instructions = body.Instructions;

        // Idempotence: if the method already has an exception handler whose
        // try block covers a UdpClient.Send, we've already patched.
        foreach (var eh in body.ExceptionHandlers)
        {
            var probe = eh.TryStart;
            while (probe != null && probe != eh.TryEnd)
            {
                if (probe.OpCode == OpCodes.Callvirt
                    && probe.Operand is MethodReference mr
                    && mr.Name == "Send" && mr.DeclaringType.Name == "UdpClient")
                    return new(false, "broadcast Send already shielded");
                probe = probe.Next;
            }
        }

        // Locate the first Send+Pop pair (the broadcast one — appears before
        // our LoopbackDiscovery injection, which is at the next Send+Pop pair).
        Instruction? callSend = null;
        Instruction? popAfter = null;
        for (var i = 0; i < instructions.Count - 1; i++)
        {
            if (instructions[i].OpCode == OpCodes.Callvirt
                && instructions[i].Operand is MethodReference mr
                && mr.Name == "Send" && mr.DeclaringType.Name == "UdpClient"
                && instructions[i + 1].OpCode == OpCodes.Pop)
            {
                callSend = instructions[i];
                popAfter = instructions[i + 1];
                break;
            }
        }
        if (callSend == null || popAfter == null)
            throw new Exception("broadcast Send+Pop pair not found");

        // The try block covers the 9-instruction broadcast Send sequence:
        //   ldarg.0, ldfld _discoverySocket, ldloc.1, ldloc.1, ldlen,
        //   conv.i4, ldloc.0, callvirt Send, pop
        // i.e. eight instructions ending at the pop. Walk back 8 from popAfter.
        var sendStart = popAfter;
        for (var i = 0; i < 8; i++)
        {
            sendStart = sendStart.Previous
                ?? throw new Exception("walked off the front while locating Send start");
        }
        // sendStart should now be the ldarg.0 that begins the broadcast Send sequence.
        if (sendStart.OpCode != OpCodes.Ldarg_0)
            throw new Exception($"expected ldarg.0 at start of broadcast Send, got {sendStart.OpCode}");

        // afterTry is the instruction right after the existing pop — that's where
        // both the try's leave and the catch's leave will jump to.
        var afterTry = popAfter.Next
            ?? throw new Exception("no instruction after broadcast Send's pop");

        // System.Exception type reference. Steal one from an EXISTING catch
        // handler in the assembly so the AssemblyRef matches what Unity Mono
        // is bound against (importing typeof(Exception) here would bind
        // against the patcher's mscorlib v4, which Mono rejects with
        // TypeLoadException at runtime).
        TypeReference? exceptionTypeRef = null;
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var eh in m.Body.ExceptionHandlers)
                {
                    if (eh.HandlerType == ExceptionHandlerType.Catch
                        && eh.CatchType != null
                        && eh.CatchType.FullName == "System.Exception")
                    {
                        exceptionTypeRef = eh.CatchType;
                        break;
                    }
                }
                if (exceptionTypeRef != null) break;
            }
            if (exceptionTypeRef != null) break;
        }
        if (exceptionTypeRef == null)
            throw new Exception("No existing catch(Exception) site found to steal type reference from");

        var il = body.GetILProcessor();

        // Insert "leave afterTry" right after the pop — this becomes the end
        // of the try block.
        var leaveFromTry = il.Create(OpCodes.Leave, afterTry);
        il.InsertAfter(popAfter, leaveFromTry);

        // Insert the catch body right after the leave: pop the exception, leave.
        var popException = il.Create(OpCodes.Pop);
        var leaveFromCatch = il.Create(OpCodes.Leave, afterTry);
        il.InsertAfter(leaveFromTry, popException);
        il.InsertAfter(popException, leaveFromCatch);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = sendStart,
            TryEnd = popException,        // exclusive — first ins of catch
            HandlerStart = popException,
            HandlerEnd = afterTry,         // exclusive — first ins after catch
            CatchType = exceptionTypeRef,
        });

        return new(true, $"wrapped broadcast Send (IL_{sendStart.Offset:X4}..IL_{popAfter.Offset:X4}) in try/catch");
    }
}

static class AutoPickLoopback
{
    // At the end of IPListMenu.SetPossibleConnections, scan the freshly-set
    // _possibleConnections list; if any entry has IP == "127.0.0.1", call
    // OnListItemSelected(i) and break. Emits a manual for-loop in IL.
    //
    // NB: never call .Resolve() on system types (String, Int32, List<T>).
    // Cecil's default resolver falls back to the patcher's own runtime
    // (System.Private.CoreLib on .NET 10) when it can't find the target
    // assembly's mscorlib in the search path. That produces references
    // scoped to a corlib that doesn't exist at runtime, and the JIT
    // throws FileNotFoundException when the patched method is invoked.
    // We construct MethodReferences manually using module.TypeSystem.*
    // (which is already scoped to the module's CorlibReference, i.e.
    // Unity Mono's mscorlib).
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("IPListMenu")
            ?? throw new Exception("IPListMenu type not found");

        var setConn = type.Methods.FirstOrDefault(m =>
            m.Name == "SetPossibleConnections" && m.Parameters.Count == 1)
            ?? throw new Exception("IPListMenu::SetPossibleConnections(IEnumerable<RemoteDeviceDescription>) not found");

        var onItemSelected = type.Methods.FirstOrDefault(m =>
            m.Name == "OnListItemSelected" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
            ?? throw new Exception("IPListMenu::OnListItemSelected(int) not found");

        var possibleConnectionsField = type.Fields.FirstOrDefault(f => f.Name == "_possibleConnections")
            ?? throw new Exception("_possibleConnections field not found");

        if (possibleConnectionsField.FieldType is not GenericInstanceType listType)
            throw new Exception("_possibleConnections is not a generic List<>");
        var elementType = listType.GenericArguments[0]; // RemoteDeviceDescription, same module

        // Avoid List<T>'s generic instance methods entirely — constructing
        // a MethodReference for a method that returns the open generic T
        // requires resolving List`1, which would pull in System.Private.CoreLib
        // via Cecil's resolver fallback. Instead, dispatch through the
        // non-generic ICollection.get_Count and IList.get_Item — List<T>
        // implements both, and the references have no generics.
        var corlibScope = module.TypeSystem.CoreLibrary;
        var iCollectionType = new TypeReference("System.Collections", "ICollection",
            module, corlibScope, valueType: false);
        var iListType = new TypeReference("System.Collections", "IList",
            module, corlibScope, valueType: false);

        // ICollection.get_Count() -> int
        var getCount = new MethodReference("get_Count", module.TypeSystem.Int32, iCollectionType)
            { HasThis = true };

        // IList.get_Item(int) -> object  (we'll castclass RemoteDeviceDescription)
        var getItem = new MethodReference("get_Item", module.TypeSystem.Object, iListType)
            { HasThis = true };
        getItem.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

        // RemoteDeviceDescription.get_IP -> string. Custom type in this same
        // module, fine to construct manually with module's type-system.
        var getIP = new MethodReference("get_IP", module.TypeSystem.String, elementType)
            { HasThis = true };

        // String.op_Equality(string, string) -> bool. Static.
        var stringEquals = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String);
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var body = setConn.Body;
        var instructions = body.Instructions;

        // Idempotence: if the method already references "127.0.0.1", skip.
        foreach (var ins in instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && (ins.Operand as string) == "127.0.0.1")
                return new(false, "IPListMenu::SetPossibleConnections already auto-picks loopback");
        }

        // We need two local ints: idx and a slot for the current device.
        // For simplicity use just one int local (idx) and re-fetch the device
        // each iteration via _possibleConnections[idx].
        var int32Type = module.TypeSystem.Int32;
        var idxLocal = new VariableDefinition(int32Type);
        body.Variables.Add(idxLocal);

        var il = body.GetILProcessor();

        // The original method ends with a ret. Insert our loop before it.
        var oldRet = instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? throw new Exception("SetPossibleConnections has no ret");

        // Build the loop instructions:
        //   ldc.i4.0; stloc idx
        // loop:
        //   ldloc idx
        //   ldarg.0; ldfld _possibleConnections
        //   callvirt get_Count
        //   bge done
        //   ldarg.0; ldfld _possibleConnections
        //   ldloc idx
        //   callvirt get_Item
        //   callvirt get_IP
        //   ldstr "127.0.0.1"
        //   call op_Equality
        //   brfalse next
        //   ldarg.0
        //   ldloc idx
        //   callvirt OnListItemSelected
        //   br done
        // next:
        //   ldloc idx; ldc.i4.1; add; stloc idx
        //   br loop
        // done:
        //   (ret follows)

        var doneAnchor = oldRet;  // jump here to exit
        var initIdx0 = il.Create(OpCodes.Ldc_I4_0);
        var initIdx1 = il.Create(OpCodes.Stloc, idxLocal);

        var loopHead = il.Create(OpCodes.Ldloc, idxLocal);
        var loadCount0 = il.Create(OpCodes.Ldarg_0);
        var loadCount1 = il.Create(OpCodes.Ldfld, possibleConnectionsField);
        var loadCount2 = il.Create(OpCodes.Callvirt, getCount);
        var bgeDone   = il.Create(OpCodes.Bge, doneAnchor);

        var getItem0 = il.Create(OpCodes.Ldarg_0);
        var getItem1 = il.Create(OpCodes.Ldfld, possibleConnectionsField);
        var getItem2 = il.Create(OpCodes.Ldloc, idxLocal);
        var getItem3 = il.Create(OpCodes.Callvirt, getItem);     // returns object
        var castDev  = il.Create(OpCodes.Castclass, elementType); // narrow to RemoteDeviceDescription
        var getIP0   = il.Create(OpCodes.Callvirt, getIP);
        var ldLiteral = il.Create(OpCodes.Ldstr, "127.0.0.1");
        var callEq   = il.Create(OpCodes.Call, stringEquals);

        // We need the brfalse target = the "next" iteration label. We'll
        // build the increment block and use its first instruction as the
        // jump target.
        var incIdx0 = il.Create(OpCodes.Ldloc, idxLocal);
        var incIdx1 = il.Create(OpCodes.Ldc_I4_1);
        var incIdx2 = il.Create(OpCodes.Add);
        var incIdx3 = il.Create(OpCodes.Stloc, idxLocal);
        var jumpBack = il.Create(OpCodes.Br, loopHead);

        var brfalseNext = il.Create(OpCodes.Brfalse, incIdx0);

        var callSelected0 = il.Create(OpCodes.Ldarg_0);
        var callSelected1 = il.Create(OpCodes.Ldloc, idxLocal);
        var callSelected2 = il.Create(OpCodes.Callvirt, onItemSelected);
        var brDone = il.Create(OpCodes.Br, doneAnchor);

        // Now wire them up in order, inserting before the existing ret.
        var sequence = new[]
        {
            initIdx0, initIdx1,
            loopHead,
            loadCount0, loadCount1, loadCount2, bgeDone,
            getItem0, getItem1, getItem2, getItem3, castDev,
            getIP0, ldLiteral, callEq, brfalseNext,
            callSelected0, callSelected1, callSelected2, brDone,
            incIdx0, incIdx1, incIdx2, incIdx3, jumpBack,
        };
        foreach (var ins in sequence)
            il.InsertBefore(oldRet, ins);

        // Ensure max stack is enough (we push up to 2 args + 1 result for OnListItemSelected).
        if (body.MaxStackSize < 3) body.MaxStackSize = 3;

        return new(true, $"IPListMenu::SetPossibleConnections now auto-calls OnListItemSelected on 127.0.0.1 entry");
    }
}

static class RewriteNoConsoleFoundDesc
{
    public const string NewMessage =
        "GameNative must be running Fallout 4 on the top screen, in the foreground. " +
        "Select the 127.0.0.1 entry; any other addresses can be ignored.";

    const string InterceptedKey = "$Companion_NoConsoleFoundDesc";

    // Prepend an early-return to FontConfigManager.GetText:
    //   if (key == "$Companion_NoConsoleFoundDesc") return <NewMessage>;
    //   ... original body ...
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("FontConfigManager")
            ?? throw new Exception("FontConfigManager type not found");

        var method = type.Methods.FirstOrDefault(m =>
            m.Name == "GetText" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String"
            && m.ReturnType.FullName == "System.String")
            ?? throw new Exception("FontConfigManager::GetText(string) -> string not found");

        var body = method.Body;
        var instructions = body.Instructions;

        // Idempotence: the patched prologue is `ldarg.1; ldstr "<InterceptedKey>";
        // call op_Equality; brfalse; ldstr <NewMessage>; ret`. The InterceptedKey
        // ldstr sits before the first call, so scan for it within the prologue.
        // The original GetText has no reason to ldstr this specific resource key
        // (the patch exists because the key isn't otherwise handled here).
        for (int i = 0; i < instructions.Count; i++)
        {
            var ins = instructions[i];
            if (ins.OpCode == OpCodes.Ldstr && (ins.Operand as string) == InterceptedKey)
                return new(false, "FontConfigManager::GetText already rewritten");
            if (ins.OpCode == OpCodes.Callvirt) break;
        }

        // String.op_Equality(string, string) -> bool, static.
        // (See note in AutoPickLoopback for why we don't .Resolve().)
        var stringEquals = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String);
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var firstOriginal = instructions[0];

        var il = body.GetILProcessor();

        var loadKey      = il.Create(OpCodes.Ldarg_1);
        var loadCmpKey   = il.Create(OpCodes.Ldstr, InterceptedKey);
        var callEqOp     = il.Create(OpCodes.Call, stringEquals);
        var brFalseToOrig= il.Create(OpCodes.Brfalse, firstOriginal);
        var loadNewMsg   = il.Create(OpCodes.Ldstr, NewMessage);
        var ret          = il.Create(OpCodes.Ret);

        // Insert in order BEFORE the original first instruction.
        var sequence = new[] { loadKey, loadCmpKey, callEqOp, brFalseToOrig, loadNewMsg, ret };
        foreach (var ins in sequence)
            il.InsertBefore(firstOriginal, ins);

        if (body.MaxStackSize < 2) body.MaxStackSize = 2;

        return new(true, "FontConfigManager::GetText now intercepts $Companion_NoConsoleFoundDesc");
    }
}

static class HUDColorBridge
{
    // Rewrite PipboyStatusManager::UpdatePipboyEffectColor to:
    //   - use the tolerant GetMember<PipboyArray>(string, bool) overload
    //     so missing nodes return null instead of throwing
    //   - drop the `IsDirty` gate so colour applies even when the protocol
    //     doesn't flag delta-dirty
    //   - log once via UnityEngine.Debug.Log on first delivery from the game
    //
    // We never call `.Resolve()` on system types (same reason as in
    // AutoPickLoopback — Cecil's default resolver would fall through to
    // the patcher's own .NET 10 corlib and break at runtime). All method
    // references are harvested from the existing IL of this method, its
    // sibling UpdateMinigameFormIds, and arbitrary Debug.Log call sites
    // already in the module.
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyStatusManager")
            ?? throw new Exception("PipboyStatusManager type not found");

        var method = type.Methods.FirstOrDefault(m => m.Name == "UpdatePipboyEffectColor")
            ?? throw new Exception("PipboyStatusManager::UpdatePipboyEffectColor not found");

        // Idempotence: the patched body no longer contains a `get_IsDirty`
        // callvirt. If the original gate is gone, we already patched.
        var hasIsDirty = method.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.Name == "get_IsDirty");
        if (!hasIsDirty)
            return new(false, "PipboyStatusManager::UpdatePipboyEffectColor already patched");

        // ---- Harvest method references from existing IL -----------------
        MethodReference? statusObjectGetter = null;
        MethodReference? getMember = null;            // GetMember<PipboyArray>(string, bool) — already 2-arg in original IL
        MethodReference? getElement = null;           // GetElement<PipboyPrimitiveValue<double>>(int)
        MethodReference? primValueToDouble = null;    // op_Implicit (returns generic T, instantiated to double)
        MethodReference? appSettingsSetColor = null;  // AppSettings.set_PipboyEffectColor
        MethodReference? colorCtor = null;            // UnityEngine.Color..ctor(float,float,float)

        foreach (var i in method.Body.Instructions)
        {
            if (i.Operand is not MethodReference mr) continue;
            switch (mr.Name)
            {
                case "get_StatusObject":
                    statusObjectGetter ??= mr; break;
                case "GetMember":
                    // Original IL uses the 2-arg GetMember<PipboyArray>(string, bool)
                    // overload with `false` for the bool. We re-emit the same ref but
                    // pass `true` for tolerateAbsentValue.
                    getMember ??= mr; break;
                case "GetElement":
                    getElement ??= mr; break;
                case "op_Implicit":
                    // Return type is generic T (Var/MVar) — don't filter on metadata
                    // type. Only one op_Implicit appears in this method, on
                    // PipboyPrimitiveValue<double>; safe to grab by name alone.
                    primValueToDouble ??= mr; break;
                case "set_PipboyEffectColor":
                    appSettingsSetColor ??= mr; break;
                case ".ctor"
                    when mr.DeclaringType.Name == "Color"
                      && mr.Parameters.Count == 3:
                    colorCtor ??= mr; break;
            }
        }

        if (statusObjectGetter is null) throw new Exception("get_StatusObject ref not harvested");
        if (getMember is null) throw new Exception("GetMember ref not harvested");
        if (getElement is null) throw new Exception("GetElement ref not harvested");
        if (primValueToDouble is null) throw new Exception("op_Implicit ref not harvested");
        if (appSettingsSetColor is null) throw new Exception("set_PipboyEffectColor ref not harvested");
        if (colorCtor is null) throw new Exception("Color..ctor(float,float,float) ref not harvested");

        // The 2-arg overload takes (string, bool) — verify before relying on it.
        var useTolerantOverload =
            getMember.Parameters.Count == 2 &&
            getMember.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean;

        // ---- Harvest UnityEngine.Debug.Log(object) from anywhere in module
        MethodReference? debugLog = null;
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (m.Body is null) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.Operand is MethodReference mr3
                        && mr3.Name == "Log"
                        && mr3.DeclaringType.FullName == "UnityEngine.Debug"
                        && mr3.Parameters.Count == 1)
                    {
                        debugLog = mr3;
                        break;
                    }
                }
                if (debugLog != null) break;
            }
            if (debugLog != null) break;
        }
        if (debugLog is null) throw new Exception("UnityEngine.Debug.Log(object) ref not harvested");

        // ---- Find the PipboyArray local's TypeReference ----------------
        TypeReference? pipboyArrayType = null;
        foreach (var v in method.Body.Variables)
        {
            if (v.VariableType.Name == "PipboyArray")
            {
                pipboyArrayType = v.VariableType;
                break;
            }
        }
        pipboyArrayType ??= module.GetType("PipboyArray")
            ?? throw new Exception("PipboyArray type not found");

        // ---- Add (or reuse) a private static bool marker field --------
        // Doubles as our one-shot "have we logged yet" flag.
        const string markerFieldName = "_stripboyHudColorLogged";
        var markerField = type.Fields.FirstOrDefault(f => f.Name == markerFieldName);
        if (markerField is null)
        {
            markerField = new FieldDefinition(markerFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                module.TypeSystem.Boolean);
            type.Fields.Add(markerField);
        }

        // ---- Rewrite the body -------------------------------------------
        var body = method.Body;
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.Instructions.Clear();

        var memberLocal = new VariableDefinition(pipboyArrayType);
        body.Variables.Add(memberLocal);

        var il = body.GetILProcessor();
        var retIns = il.Create(OpCodes.Ret);
        var skipLogIns = il.Create(OpCodes.Nop);

        //   member = this.StatusObject.GetMember<PipboyArray>("EffectColor", true)
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, statusObjectGetter));
        il.Append(il.Create(OpCodes.Ldstr, "EffectColor"));
        if (useTolerantOverload)
            il.Append(il.Create(OpCodes.Ldc_I4_1));   // tolerateAbsentValue: true (was: false)
        il.Append(il.Create(OpCodes.Callvirt, getMember));
        il.Append(il.Create(OpCodes.Stloc, memberLocal));

        //   if (member == null) return;
        il.Append(il.Create(OpCodes.Ldloc, memberLocal));
        il.Append(il.Create(OpCodes.Brfalse, retIns));

        //   if (!_stripboyHudColorLogged) {
        //       Debug.Log("[strip-boy] EffectColor delivered by F4 protocol");
        //       _stripboyHudColorLogged = true;
        //   }
        il.Append(il.Create(OpCodes.Ldsfld, markerField));
        il.Append(il.Create(OpCodes.Brtrue, skipLogIns));
        il.Append(il.Create(OpCodes.Ldstr,
            "[strip-boy] HUD EffectColor delivered by F4 protocol — bridge active"));
        il.Append(il.Create(OpCodes.Call, debugLog));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stsfld, markerField));
        il.Append(skipLogIns);

        //   r = (float)(double)member.GetElement<PipboyPrimitiveValue<double>>(0)
        //   g = ... (1)
        //   b = ... (2)
        // Left on the eval stack as three floats, then consumed by Color..ctor.
        for (int idx = 0; idx < 3; idx++)
        {
            il.Append(il.Create(OpCodes.Ldloc, memberLocal));
            il.Append(OpCodeForIntConstant(il, idx));
            il.Append(il.Create(OpCodes.Callvirt, getElement));
            il.Append(il.Create(OpCodes.Call, primValueToDouble));
            il.Append(il.Create(OpCodes.Conv_R4));
        }

        //   AppSettings.PipboyEffectColor = new Color(r, g, b);
        // The setter persists to PlayerPrefs and fires PipboyEffectColorChanged,
        // which is what the menu subscribers listen on to update the shader.
        il.Append(il.Create(OpCodes.Newobj, colorCtor));
        il.Append(il.Create(OpCodes.Call, appSettingsSetColor));

        il.Append(retIns);

        // Peak stack: 4 (e.g. [r, g, member, idx] just before the 3rd Callvirt).
        if (body.MaxStackSize < 5) body.MaxStackSize = 5;

        var overloadNote = useTolerantOverload ? "tolerant" : "strict";
        return new(true,
            $"PipboyStatusManager::UpdatePipboyEffectColor rewritten "
          + $"(drop IsDirty gate, {overloadNote} GetMember, one-shot Debug.Log)");
    }

    static Instruction OpCodeForIntConstant(ILProcessor il, int v) => v switch
    {
        0 => il.Create(OpCodes.Ldc_I4_0),
        1 => il.Create(OpCodes.Ldc_I4_1),
        2 => il.Create(OpCodes.Ldc_I4_2),
        3 => il.Create(OpCodes.Ldc_I4_3),
        _ => il.Create(OpCodes.Ldc_I4, v),
    };
}

// Experiment A1 — minimal IL probe. Tees the per-frame _Brightness
// value into a static field PipboyPostEffect::_stripboyLastFB but
// does nothing else. Purpose: determine whether IL injection into
// PipboyPostEffect.Update is itself poisonous (vs. specifically the
// JNI / boxing path of earlier attempts).
//
// NOT registered in the patches[] array. To enable, insert
//   ("FlickerProbeA1", FlickerProbeA1.Apply),
// into the patches array above and rebuild Strip-Boy.
//
// If shader survives this probe -> the per-frame Update body tolerates
// IL injection; the prior breakage was Material.GetColor / JNI / GC.
// Proceed to Experiment A2 (full LED write via cached jvalue[]).
//
// If shader breaks anyway -> in-process per-frame work is not viable;
// fall back to Experiment C (flickerd sidekick APK).
static class FlickerProbeA1
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");

        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("PipboyPostEffect::Update() not found");

        const string fieldName = "_stripboyLastFB";
        if (type.Fields.Any(f => f.Name == fieldName))
            return new(false, "FlickerProbeA1 already installed");

        // Find Material.SetFloat("_Brightness", ...) call site by walking
        // forward from each `ldstr "_Brightness"` for the nearest matching
        // Callvirt/Call to a SetFloat method on Material.
        Instruction? brightnessSetFloat = null;
        foreach (var i in update.Body.Instructions)
        {
            if (i.OpCode != OpCodes.Ldstr || (i.Operand as string) != "_Brightness") continue;
            var c = i.Next;
            while (c != null)
            {
                if ((c.OpCode == OpCodes.Callvirt || c.OpCode == OpCodes.Call)
                    && c.Operand is MethodReference mr
                    && mr.Name == "SetFloat"
                    && mr.DeclaringType.Name == "Material")
                {
                    brightnessSetFloat = c;
                    break;
                }
                c = c.Next;
            }
            if (brightnessSetFloat != null) break;
        }
        if (brightnessSetFloat is null)
            throw new Exception("Material::SetFloat(\"_Brightness\", ...) call not found in Update");

        var fbField = new FieldDefinition(fieldName,
            FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Single);
        type.Fields.Add(fbField);

        // At the SetFloat callvirt, stack is [...][material][string][float fB].
        // dup leaves [...][material][string][float fB][float fB]; stsfld pops
        // the top into our field, leaving the original 3 args intact for the
        // SetFloat call to consume. Effectively a non-destructive tee.
        var il = update.Body.GetILProcessor();
        var dup = il.Create(OpCodes.Dup);
        var stfb = il.Create(OpCodes.Stsfld, fbField);
        il.InsertBefore(brightnessSetFloat, dup);
        il.InsertAfter(dup, stfb);

        update.Body.MaxStackSize += 1;

        return new(true,
            $"PipboyPostEffect::Update _Brightness SetFloat tee'd into static "
          + $"{fieldName} at IL_{brightnessSetFloat.Offset:X4} (no JNI, no work)");
    }
}

// Experiment A2 — full per-frame LED brightness write with ZERO heap
// allocations. Subsumes Experiment A1 (FlickerProbeA1): also tees the
// per-frame _Brightness into _stripboyLastFB AND pushes it to Java via
// raw AndroidJNI using a cached classPtr + methodID + pre-allocated
// jvalue[]. No object[], no boxing, no AndroidJavaClass.
//
// Per-frame work added to PipboyPostEffect::Update at the
// Material.SetFloat("_Brightness", ...) call site:
//   - dup; stsfld _stripboyLastFB           (A1 tee — only if A1 isn't
//                                            already there)
//   - 1× brtrue (cache hit check on the methodID handle)
//   - On hit (every frame after first): 4× ldsfld, ldelema, stfld,
//     3× ldsfld, 1× call
//   - On miss (first call only): ldstr×3, call FindClass, stsfld,
//     ldsfld, ldstr×2, call GetStaticMethodID, stsfld
// Steady state: ~9 instructions, 0 alloc.
//
// Static cctor on PipboyPostEffect is extended to do
//   _stripboyJvalueArr = new jvalue[1]
// once at type init, so the array exists by first Update call.
//
// REQUIRES io.pipboy.thor.LEDBridge::applyBrightness(F)V to exist on
// the Java side (added to patcher/smali/io/pipboy/thor/LEDBridge.smali
// in the same commit as this class).
//
// NOT registered in the patches[] array. To enable, insert
//   ("FlickerSyncA2", FlickerSyncA2.Apply),
// after FlickerProbeA1 in the patches list and rebuild Strip-Boy. If
// A1 is also enabled, A2 detects it (via the fb-field presence) and
// skips its own redundant tee.
static class FlickerSyncA2
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");
        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("PipboyPostEffect::Update() not found");

        // Idempotence — keyed off the JNI-handle field that ONLY A2 adds.
        const string classPtrFieldName = "_stripboyLEDBridgeClassPtr";
        if (type.Fields.Any(f => f.Name == classPtrFieldName))
            return new(false, "FlickerSyncA2 already installed");

        // ---- Type references ----------------------------------------------
        var unityRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "UnityEngine")
            ?? throw new Exception("UnityEngine assembly reference not present");
        var intPtrType = new TypeReference("System", "IntPtr",
            module, module.TypeSystem.CoreLibrary, valueType: true);
        var jvalueType = new TypeReference("UnityEngine", "jvalue",
            module, unityRef, valueType: true);
        var jvalueArrType = new ArrayType(jvalueType);
        var androidJniType = new TypeReference("UnityEngine", "AndroidJNI",
            module, unityRef, valueType: false);

        // ---- AndroidJNI static method refs --------------------------------
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

        // ---- Fields (reuse A1's fb-field if present) ----------------------
        const string fbFieldName = "_stripboyLastFB";
        bool needTee = !type.Fields.Any(f => f.Name == fbFieldName);
        var fbField = type.Fields.FirstOrDefault(f => f.Name == fbFieldName);
        if (fbField is null)
        {
            fbField = new FieldDefinition(fbFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                module.TypeSystem.Single);
            type.Fields.Add(fbField);
        }
        var classPtrField = new FieldDefinition(classPtrFieldName,
            FieldAttributes.Private | FieldAttributes.Static, intPtrType);
        var methodIdField = new FieldDefinition("_stripboyApplyBrightnessMethodId",
            FieldAttributes.Private | FieldAttributes.Static, intPtrType);
        var jvalueArrField = new FieldDefinition("_stripboyJvalueArr",
            FieldAttributes.Private | FieldAttributes.Static, jvalueArrType);
        type.Fields.Add(classPtrField);
        type.Fields.Add(methodIdField);
        type.Fields.Add(jvalueArrField);

        // ---- jvalueArr init in <cctor> ------------------------------------
        var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
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

        // ---- Find the Material.SetFloat("_Brightness", ...) call site ----
        Instruction? brightnessSetFloat = null;
        foreach (var i in update.Body.Instructions)
        {
            if (i.OpCode != OpCodes.Ldstr || (i.Operand as string) != "_Brightness") continue;
            var c = i.Next;
            while (c != null)
            {
                if ((c.OpCode == OpCodes.Callvirt || c.OpCode == OpCodes.Call)
                    && c.Operand is MethodReference mr
                    && mr.Name == "SetFloat"
                    && mr.DeclaringType.Name == "Material")
                {
                    brightnessSetFloat = c;
                    break;
                }
                c = c.Next;
            }
            if (brightnessSetFloat != null) break;
        }
        if (brightnessSetFloat is null)
            throw new Exception("Material::SetFloat(\"_Brightness\", ...) call not found in Update");

        // ---- Build IL sequence --------------------------------------------
        var il = update.Body.GetILProcessor();
        // Anchor: first instruction AFTER the cache-init branch.
        var afterCacheInit = il.Create(OpCodes.Ldsfld, jvalueArrField);

        var seq = new List<Instruction>();
        if (needTee)
        {
            seq.Add(il.Create(OpCodes.Dup));
            seq.Add(il.Create(OpCodes.Stsfld, fbField));
        }

        // if (methodIdField != IntPtr.Zero) goto afterCacheInit
        seq.Add(il.Create(OpCodes.Ldsfld, methodIdField));
        seq.Add(il.Create(OpCodes.Brtrue, afterCacheInit));

        // classPtrField = AndroidJNI.FindClass("io/pipboy/thor/LEDBridge");
        seq.Add(il.Create(OpCodes.Ldstr, "io/pipboy/thor/LEDBridge"));
        seq.Add(il.Create(OpCodes.Call, findClass));
        seq.Add(il.Create(OpCodes.Stsfld, classPtrField));

        // methodIdField = AndroidJNI.GetStaticMethodID(classPtrField, "applyBrightness", "(F)V");
        seq.Add(il.Create(OpCodes.Ldsfld, classPtrField));
        seq.Add(il.Create(OpCodes.Ldstr, "applyBrightness"));
        seq.Add(il.Create(OpCodes.Ldstr, "(F)V"));
        seq.Add(il.Create(OpCodes.Call, getStaticMethodID));
        seq.Add(il.Create(OpCodes.Stsfld, methodIdField));

        // afterCacheInit:  ldsfld jvalueArr  (this IS afterCacheInit; we
        // ldelema [0] from it next, then stfld jvalue::f)
        seq.Add(afterCacheInit);
        seq.Add(il.Create(OpCodes.Ldc_I4_0));
        seq.Add(il.Create(OpCodes.Ldelema, jvalueType));
        seq.Add(il.Create(OpCodes.Ldsfld, fbField));
        seq.Add(il.Create(OpCodes.Stfld, jvalueFloatField));

        // AndroidJNI.CallStaticVoidMethod(classPtrField, methodIdField, jvalueArr)
        seq.Add(il.Create(OpCodes.Ldsfld, classPtrField));
        seq.Add(il.Create(OpCodes.Ldsfld, methodIdField));
        seq.Add(il.Create(OpCodes.Ldsfld, jvalueArrField));
        seq.Add(il.Create(OpCodes.Call, callStaticVoid));

        foreach (var ins in seq)
            il.InsertBefore(brightnessSetFloat, ins);

        // Cache-init path stacks classPtr + 2 strings = 3; tee path adds 1.
        // SetFloat itself wants 3 args (this, string, float). 8 is safe.
        if (update.Body.MaxStackSize < 8) update.Body.MaxStackSize = 8;

        return new(true,
            $"PipboyPostEffect::Update _Brightness tee'd + JNI-pushed via "
          + $"cached jvalue[] (zero per-frame alloc) "
          + $"{(needTee ? "[+A1 tee]" : "[reused existing A1 tee]")} "
          + $"at IL_{brightnessSetFloat.Offset:X4}");
    }
}

// Experiment B (post-A2-failure) — like A2 but uses AndroidJavaClass
// instead of raw AndroidJNI (which doesn't exist in Unity 5.x). One
// new object[1] + one boxed float per frame; that's the minimum
// allocation footprint for an AJC.CallStatic call.
//
// Strips out Material.GetColor("_Color") (the prior failure's likely
// culprit), since fB is already on the stack at the SetFloat tee site.
//
// Hypothesis after A1 passed on-device 2026-06-21: IL injection in
// Update itself isn't the trigger; the prior failure was Material.GetColor
// dirtying material state, or per-frame allocations causing renderer-
// synchronized GC pauses. B isolates the latter from the former.
//
// NOT registered in patches[] by default. To enable, add:
//   ("FlickerSyncB", FlickerSyncB.Apply),
// after LEDStickBridge.
static class FlickerSyncB
{
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");
        var update = type.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 0)
            ?? throw new Exception("PipboyPostEffect::Update() not found");

        // Idempotence keyed off B's dedicated AJC cache field (distinct
        // from LEDStickBridge's so the two patches don't entangle).
        const string ajcFieldName = "_stripboyBAjcCls";
        if (type.Fields.Any(f => f.Name == ajcFieldName))
            return new(false, "FlickerSyncB already installed");

        var unityRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "UnityEngine")
            ?? throw new Exception("UnityEngine assembly reference not present");
        var ajcType = new TypeReference("UnityEngine", "AndroidJavaClass",
            module, unityRef, valueType: false);

        var ajcCtor = new MethodReference(".ctor", module.TypeSystem.Void, ajcType)
            { HasThis = true };
        ajcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var callStatic = new MethodReference("CallStatic", module.TypeSystem.Void, ajcType)
            { HasThis = true };
        callStatic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        callStatic.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));

        const string fbFieldName = "_stripboyLastFB";
        bool needTee = !type.Fields.Any(f => f.Name == fbFieldName);
        var fbField = type.Fields.FirstOrDefault(f => f.Name == fbFieldName);
        if (fbField is null)
        {
            fbField = new FieldDefinition(fbFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                module.TypeSystem.Single);
            type.Fields.Add(fbField);
        }
        var ajcField = new FieldDefinition(ajcFieldName,
            FieldAttributes.Private | FieldAttributes.Static, ajcType);
        type.Fields.Add(ajcField);

        Instruction? brightnessSetFloat = null;
        foreach (var i in update.Body.Instructions)
        {
            if (i.OpCode != OpCodes.Ldstr || (i.Operand as string) != "_Brightness") continue;
            var c = i.Next;
            while (c != null)
            {
                if ((c.OpCode == OpCodes.Callvirt || c.OpCode == OpCodes.Call)
                    && c.Operand is MethodReference mr
                    && mr.Name == "SetFloat"
                    && mr.DeclaringType.Name == "Material")
                { brightnessSetFloat = c; break; }
                c = c.Next;
            }
            if (brightnessSetFloat != null) break;
        }
        if (brightnessSetFloat is null)
            throw new Exception("Material::SetFloat(\"_Brightness\", ...) call not found in Update");

        var il = update.Body.GetILProcessor();
        // Anchor where the cache-init branch lands.
        var afterCacheInit = il.Create(OpCodes.Ldsfld, ajcField);

        var seq = new List<Instruction>();
        if (needTee)
        {
            seq.Add(il.Create(OpCodes.Dup));
            seq.Add(il.Create(OpCodes.Stsfld, fbField));
        }

        // if (_stripboyBAjcCls != null) goto afterCacheInit
        seq.Add(il.Create(OpCodes.Ldsfld, ajcField));
        seq.Add(il.Create(OpCodes.Brtrue, afterCacheInit));

        // _stripboyBAjcCls = new AndroidJavaClass("io.pipboy.thor.LEDBridge");
        seq.Add(il.Create(OpCodes.Ldstr, "io.pipboy.thor.LEDBridge"));
        seq.Add(il.Create(OpCodes.Newobj, ajcCtor));
        seq.Add(il.Create(OpCodes.Stsfld, ajcField));

        // afterCacheInit: ldsfld _stripboyBAjcCls  (1st arg implicit for callvirt)
        seq.Add(afterCacheInit);

        // "applyBrightness"
        seq.Add(il.Create(OpCodes.Ldstr, "applyBrightness"));

        // new object[1] { (object)fbField }
        seq.Add(il.Create(OpCodes.Ldc_I4_1));
        seq.Add(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
        seq.Add(il.Create(OpCodes.Dup));
        seq.Add(il.Create(OpCodes.Ldc_I4_0));
        seq.Add(il.Create(OpCodes.Ldsfld, fbField));
        seq.Add(il.Create(OpCodes.Box, module.TypeSystem.Single));
        seq.Add(il.Create(OpCodes.Stelem_Ref));

        // _stripboyBAjcCls.CallStatic("applyBrightness", arr)
        seq.Add(il.Create(OpCodes.Callvirt, callStatic));

        foreach (var ins in seq)
            il.InsertBefore(brightnessSetFloat, ins);

        // Cache-init path has only ldsfld + ldstr + newobj/stsfld → +2.
        // Steady state stacks AJC + string + obj[] + (during init: arr,
        // idx, boxed val) on top of SetFloat's 3 args = up to 9. Bump.
        if (update.Body.MaxStackSize < 9) update.Body.MaxStackSize = 9;

        return new(true,
            $"PipboyPostEffect::Update _Brightness tee'd + AJC.CallStatic("
          + $"\"applyBrightness\", new object[1]{{boxed fB}}) "
          + $"{(needTee ? "[+A1 tee]" : "[reused existing A1 tee]")} "
          + $"at IL_{brightnessSetFloat.Offset:X4}");
    }
}

static class LEDStickBridge
{
    const string BridgeClassFqn = "io.pipboy.thor.LEDBridge";

    // Hook PipboyPostEffect.SetColor only.  Tried PipboyPostEffect
    // .Update twice (synchronous and async sysfs); both times the
    // shader rendering broke — gradient banding in the middle of the
    // screen, scanlines missing — even when sysfs work was on a
    // background thread.  Best guess: Material.GetColor("_Color") or
    // the per-frame JNI / boxing has a Mono-JIT / Unity-renderer
    // side effect we can't see from outside.
    //
    // SetColor is event-driven: fires once when the screen tint
    // actually changes (F4 HUD-colour delta, in-app picker, menu
    // init).  Trade-off: no per-frame flicker sync on the LEDs.
    //
    // The smali apply(IIIF) signature stays — we just always pass
    // 1.0f as the 4th arg here since SetColor has no view of the
    // per-frame fBrightness.  The smali helper's async architecture
    // (HandlerThread + coalesced posts) is preserved; it's harmless
    // for per-event firing and means the C# call returns instantly.
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("PipboyPostEffect")
            ?? throw new Exception("PipboyPostEffect type not found");

        var method = type.Methods.FirstOrDefault(m =>
            m.Name == "SetColor"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "UnityEngine.Color")
            ?? throw new Exception("PipboyPostEffect::SetColor(Color) not found");

        // Idempotence: if the body already loads our bridge FQN, we've patched.
        foreach (var i in method.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Ldstr && (i.Operand as string) == BridgeClassFqn)
                return new(false, "PipboyPostEffect::SetColor already hooks LEDBridge");
        }

        // ---- UnityEngine assembly ref + AndroidJavaClass type/method refs
        var unityRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "UnityEngine")
            ?? throw new Exception("UnityEngine assembly reference not present");
        var ajcType = new TypeReference("UnityEngine", "AndroidJavaClass",
            module, unityRef, valueType: false);

        var ajcCtor = new MethodReference(".ctor", module.TypeSystem.Void, ajcType)
            { HasThis = true };
        ajcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var callStatic = new MethodReference("CallStatic", module.TypeSystem.Void, ajcType)
            { HasThis = true };
        callStatic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        callStatic.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));

        // ---- Harvest Color.r/g/b FieldRefs from anywhere in the module
        FieldReference? colorR = null, colorG = null, colorB = null;
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (m.Body == null) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.Operand is not FieldReference fr) continue;
                    if (fr.DeclaringType.FullName != "UnityEngine.Color") continue;
                    if (fr.Name == "r") colorR ??= fr;
                    if (fr.Name == "g") colorG ??= fr;
                    if (fr.Name == "b") colorB ??= fr;
                }
                if (colorR != null && colorG != null && colorB != null) break;
            }
            if (colorR != null && colorG != null && colorB != null) break;
        }
        if (colorR is null || colorG is null || colorB is null)
            throw new Exception("Color.r/g/b FieldRefs not harvested from module");

        // ---- Cached AndroidJavaClass static field on PipboyPostEffect
        const string cachedFieldName = "_stripboyLedBridgeCls";
        var cachedField = type.Fields.FirstOrDefault(f => f.Name == cachedFieldName);
        if (cachedField is null)
        {
            cachedField = new FieldDefinition(cachedFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                ajcType);
            type.Fields.Add(cachedField);
        }

        // Visible-instance binding. PipboyPostEffect.Update runs on EVERY live
        // instance (boot/loopback effects included), but SetColor fires on the
        // one the user sees. Record its id so the vscan roll hook (FlickerSeed,
        // in Update) fires only for the visible instance — otherwise a background
        // instance running ahead leads the LED bar (same class of bug the
        // this.fTime phase feed fixes for the flicker clock).
        const string visibleIdFieldName = "_stripboyVisibleInstanceId";
        var visibleIdField = type.Fields.FirstOrDefault(f => f.Name == visibleIdFieldName);
        if (visibleIdField is null)
        {
            visibleIdField = new FieldDefinition(visibleIdFieldName,
                FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
            type.Fields.Add(visibleIdField);
        }
        var objTypeVis = new TypeReference("UnityEngine", "Object", module, unityRef, valueType: false);
        var getInstIdVis = new MethodReference("GetInstanceID", module.TypeSystem.Int32, objTypeVis) { HasThis = true };

        var body = method.Body;
        var il = body.GetILProcessor();
        var finalRet = body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? throw new Exception("PipboyPostEffect::SetColor has no ret");

        // Helper: push (int)(color.<channel> * 255f).
        // color is arg 1 (instance method, ldarg.0 = this). ldarga.s
        // for the struct's address so ldfld can read it.
        Instruction[] PushChannel(FieldReference channel) => new[]
        {
            il.Create(OpCodes.Ldarga_S, method.Parameters[0]),
            il.Create(OpCodes.Ldfld, channel),
            il.Create(OpCodes.Ldc_R4, 255f),
            il.Create(OpCodes.Mul),
            il.Create(OpCodes.Conv_I4),
        };

        var cachedLoad = il.Create(OpCodes.Ldsfld, cachedField);

        var seq = new List<Instruction>
        {
            // if (_stripboyLedBridgeCls == null) {
            il.Create(OpCodes.Ldsfld, cachedField),
            il.Create(OpCodes.Brtrue, cachedLoad),
            //     _stripboyLedBridgeCls = new AndroidJavaClass(BridgeClassFqn);
            il.Create(OpCodes.Ldstr, BridgeClassFqn),
            il.Create(OpCodes.Newobj, ajcCtor),
            il.Create(OpCodes.Stsfld, cachedField),
            // }
            // _stripboyLedBridgeCls.CallStatic("apply",
            //     new object[]{ r, g, b, 1.0f });
            cachedLoad,
            il.Create(OpCodes.Ldstr, "apply"),
            il.Create(OpCodes.Ldc_I4_4),
            il.Create(OpCodes.Newarr, module.TypeSystem.Object),
        };

        var channels = new[] { colorR, colorG, colorB };
        for (int idx = 0; idx < 3; idx++)
        {
            seq.Add(il.Create(OpCodes.Dup));
            seq.Add(il.Create(OpCodes.Ldc_I4, idx));
            seq.AddRange(PushChannel(channels[idx]));
            seq.Add(il.Create(OpCodes.Box, module.TypeSystem.Int32));
            seq.Add(il.Create(OpCodes.Stelem_Ref));
        }

        // arr[3] = this.fTime — the elapsed flicker clock of the SAME instance
        // whose colour just changed (the visible Pip-Boy). The shared static
        // _stripboyFTime was clobbered every frame by ANY live PipboyPostEffect's
        // Update tee, so a boot/loopback effect running ahead of the on-screen one
        // poisoned the phase feed (measured: a constant ~15s lead → flicker never
        // lined up). Reading this.fTime binds the feed to the instance the user
        // actually sees. LEDBridge forwards it to Bifrost as "phaseSeconds" so the
        // plugin fast-forwards its seeded flicker sim to the screen's exact state.
        var fTimeRef = new FieldReference("fTime", module.TypeSystem.Single, type);
        seq.Add(il.Create(OpCodes.Dup));
        seq.Add(il.Create(OpCodes.Ldc_I4_3));
        seq.Add(il.Create(OpCodes.Ldarg_0));
        seq.Add(il.Create(OpCodes.Ldfld, fTimeRef));
        seq.Add(il.Create(OpCodes.Box, module.TypeSystem.Single));
        seq.Add(il.Create(OpCodes.Stelem_Ref));

        seq.Add(il.Create(OpCodes.Callvirt, callStatic));

        // Record this (visible) instance's id before the apply() relay, so the
        // vscan roll hook can gate on it.
        seq.InsertRange(0, new[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, getInstIdVis),
            il.Create(OpCodes.Stsfld, visibleIdField),
        });

        foreach (var i in seq)
            il.InsertBefore(finalRet, i);

        if (body.MaxStackSize < 8) body.MaxStackSize = 8;

        return new(true,
            $"PipboyPostEffect::SetColor now relays (r,g,b,1.0f) to "
          + $"{BridgeClassFqn}.apply (event-driven; no per-frame flicker sync)");
    }
}

