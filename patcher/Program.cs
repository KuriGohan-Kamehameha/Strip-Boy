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
//        Hooks both the SETTER and GETTER of
//        AppSettings.PipboyEffectColor — setter covers F4-protocol
//        and in-app-picker driven changes, getter covers the
//        startup path where the first CompanionFlashMenu.Init reads
//        the saved PlayerPrefs colour. Each call reaches
//        io.pipboy.thor.LEDBridge.apply(r, g, b) via Unity's
//        AndroidJavaClass bridge (the AndroidJavaClass ref is
//        cached on a new static field so we don't pay ~10 ms of
//        JNI lookups per 30 Hz tick).
//
//        The smali helper drives the AYN Thor's SN3112L (left) and
//        SN3112R (right) LED controllers DIRECTLY via
//        /sys/class/sn3112{l,r}/led/brightness — world-writable
//        nodes on stock firmware, the same path Moonbench's Bifrost
//        LED utility uses. Wire format: "1-R:G:B:A" / "2-R:G:B:A"
//        where R/G/B are 0..255 colour channels and A is the
//        intensity, scaled from dual_screen_brightness_level
//        (0..100, the bottom screen) with a 70 % cap and 5 % floor.
//
//        Why not Settings.System.joystick_led_light_picker_color:
//        AYN's vendor SettingsProvider rejects writes to those keys
//        from non-privileged UIDs across every API path
//        (Settings.System.putString, .putStringForUser, .insert via
//        ContentResolver, with or without WRITE_SETTINGS and
//        WRITE_SECURE_SETTINGS pre-granted). The direct-sysfs path
//        bypasses the check entirely. No permissions needed.
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

        // Idempotence: if the body already starts with `ldstr <NewMessage>` somewhere
        // before any callvirt, we're done.
        foreach (var ins in instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && (ins.Operand as string) == NewMessage)
                return new(false, "FontConfigManager::GetText already rewritten");
            if (ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) break;
        }

        // String.op_Equality(string, string) -> bool, static.
        // (See note in AutoPickLoopback for why we don't .Resolve().)
        var stringEquals = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String);
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var firstOriginal = instructions[0];

        var il = body.GetILProcessor();

        var loadKey      = il.Create(OpCodes.Ldarg_1);
        var loadCmpKey   = il.Create(OpCodes.Ldstr, "$Companion_NoConsoleFoundDesc");
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

static class LEDStickBridge
{
    const string BridgeClassFqn = "io.pipboy.thor.LEDBridge";

    // Inject a call to io.pipboy.thor.LEDBridge.apply(int r, int g, int b)
    // at the tail of AppSettings.set_PipboyEffectColor — right before the
    // method's final ret, after the existing PipboyEffectColorChanged
    // event has fired.
    //
    // The C# equivalent of what we're inserting:
    //
    //   if (AppSettings._stripboyLedBridgeCls == null)
    //       AppSettings._stripboyLedBridgeCls =
    //           new AndroidJavaClass("io.pipboy.thor.LEDBridge");
    //   AppSettings._stripboyLedBridgeCls.CallStatic("apply",
    //       (int)(val.r * 255f), (int)(val.g * 255f), (int)(val.b * 255f));
    //
    // ...where `val` is local 0 (the post-luma-boost Color). The
    // AndroidJavaClass JNI ref is cached on a new static field of
    // AppSettings — constructing one costs ~10 ms of JNI lookups, which
    // would be wasted ~30 times per second on every protocol tick. The
    // class lives for the app's lifetime; we deliberately never Dispose,
    // so there's no try/finally needed and the call sequence stays IL-
    // compact. (The smali helper's own canWrite + dedupe gates make
    // CallStatic itself non-throwing in the steady state.)
    //
    // Same .Resolve()-free discipline as AutoPickLoopback: all method
    // references are constructed manually using module.TypeSystem.* and
    // the existing UnityEngine assembly reference; the Color.r/g/b
    // FieldReferences are harvested from the existing IL of this method.
    public static PatchResult Apply(ModuleDefinition module)
    {
        var type = module.GetType("AppSettings")
            ?? throw new Exception("AppSettings type not found");
        var setter = type.Methods.FirstOrDefault(m =>
            m.Name == "set_PipboyEffectColor" && m.Parameters.Count == 1)
            ?? throw new Exception("AppSettings::set_PipboyEffectColor(Color) not found");
        var getter = type.Methods.FirstOrDefault(m =>
            m.Name == "get_PipboyEffectColor" && m.Parameters.Count == 0)
            ?? throw new Exception("AppSettings::get_PipboyEffectColor not found");

        bool AlreadyHooks(MethodDefinition m) => m.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldstr && (i.Operand as string) == BridgeClassFqn);
        var setterHooked = AlreadyHooks(setter);
        var getterHooked = AlreadyHooks(getter);
        if (setterHooked && getterHooked)
            return new(false, "AppSettings setter+getter already hook LEDBridge");

        // ---- Harvest existing FieldReferences for Color.r/g/b --------
        FieldReference? colorR = null, colorG = null, colorB = null;
        foreach (var i in setter.Body.Instructions)
        {
            if (i.Operand is not FieldReference fr) continue;
            if (fr.DeclaringType.FullName != "UnityEngine.Color") continue;
            switch (fr.Name)
            {
                case "r": colorR ??= fr; break;
                case "g": colorG ??= fr; break;
                case "b": colorB ??= fr; break;
            }
        }
        if (colorR is null || colorG is null || colorB is null)
            throw new Exception("Color.r/g/b FieldRefs not harvested");

        // ---- UnityEngine assembly reference + AndroidJavaClass refs ----
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

        // ---- Shared cached AndroidJavaClass field ----------------------
        const string cachedFieldName = "_stripboyLedBridgeCls";
        var cachedField = type.Fields.FirstOrDefault(f => f.Name == cachedFieldName);
        if (cachedField is null)
        {
            cachedField = new FieldDefinition(cachedFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                ajcType);
            type.Fields.Add(cachedField);
        }

        // ---- Hook the SETTER (writes from F4 protocol + in-app picker)
        // Channel pusher reads (int)(val.<channel> * 255f) where val is
        // local 0 of the setter — the post-luma-boost Color.
        if (!setterHooked)
        {
            Instruction[] PushSetterChannel(int idx)
            {
                var fr = idx == 0 ? colorR! : idx == 1 ? colorG! : colorB!;
                var il = setter.Body.GetILProcessor();
                return new[]
                {
                    il.Create(OpCodes.Ldloca_S, setter.Body.Variables[0]),
                    il.Create(OpCodes.Ldfld, fr),
                    il.Create(OpCodes.Ldc_R4, 255f),
                    il.Create(OpCodes.Mul),
                    il.Create(OpCodes.Conv_I4),
                };
            }
            InjectLEDBridgeCall(setter, cachedField, ajcCtor, callStatic, module, PushSetterChannel);
        }

        // ---- Hook the GETTER (covers app-startup LED push) -----------
        // Reads locals 0/1/2 = the float r/g/b loaded from PlayerPrefs.
        // The first CompanionFlashMenu.Init after Unity comes up calls
        // SetPipboyEffectColor(AppSettings.PipboyEffectColor) — that
        // invokes this getter, so the LEDs reflect the saved PlayerPrefs
        // colour on every launch even before F4 connects or the user
        // touches the in-app picker. The smali helper's dedupe makes
        // subsequent getter calls free.
        if (!getterHooked)
        {
            Instruction[] PushGetterChannel(int idx)
            {
                var il = getter.Body.GetILProcessor();
                return new[]
                {
                    il.Create(OpCodes.Ldloc, getter.Body.Variables[idx]),
                    il.Create(OpCodes.Ldc_R4, 255f),
                    il.Create(OpCodes.Mul),
                    il.Create(OpCodes.Conv_I4),
                };
            }
            InjectLEDBridgeCall(getter, cachedField, ajcCtor, callStatic, module, PushGetterChannel);
        }

        var msg = (setterHooked, getterHooked) switch
        {
            (false, false) => "setter+getter",
            (true,  false) => "getter (setter already hooked)",
            (false, true)  => "setter (getter already hooked)",
            (true,  true)  => "(no change)",
        };
        return new(true,
            $"AppSettings {msg} → {BridgeClassFqn}.apply(r,g,b)"
          + $" (cached AJC ref in {cachedFieldName})");
    }

    // Insert an AndroidJavaClass-cached call to LEDBridge.apply(int,int,int)
    // immediately before the method's final `ret`. `pushChannel(idx)`
    // returns the IL that puts an int32 for channel idx (0=r, 1=g, 2=b)
    // on the eval stack — boxing is applied here, so pushChannel must
    // leave a raw int32 on top.
    static void InjectLEDBridgeCall(
        MethodDefinition method,
        FieldDefinition cachedField,
        MethodReference ajcCtor,
        MethodReference callStatic,
        ModuleDefinition module,
        Func<int, Instruction[]> pushChannel)
    {
        var body = method.Body;
        var il = body.GetILProcessor();

        var finalRet = body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? throw new Exception($"{method.FullName} has no ret");

        // Branch target for the cached path. After construct-and-store,
        // execution falls through to this same ldsfld.
        var cachedLoad = il.Create(OpCodes.Ldsfld, cachedField);

        var seq = new System.Collections.Generic.List<Instruction>
        {
            // if (_stripboyLedBridgeCls == null) {
            il.Create(OpCodes.Ldsfld, cachedField),
            il.Create(OpCodes.Brtrue, cachedLoad),
            //     _stripboyLedBridgeCls = new AndroidJavaClass(BridgeClassFqn);
            il.Create(OpCodes.Ldstr, BridgeClassFqn),
            il.Create(OpCodes.Newobj, ajcCtor),
            il.Create(OpCodes.Stsfld, cachedField),
            // }
            // _stripboyLedBridgeCls.CallStatic("apply", new object[]{ r, g, b });
            cachedLoad,
            il.Create(OpCodes.Ldstr, "apply"),
            il.Create(OpCodes.Ldc_I4_3),
            il.Create(OpCodes.Newarr, module.TypeSystem.Object),
        };

        for (int idx = 0; idx < 3; idx++)
        {
            seq.Add(il.Create(OpCodes.Dup));
            seq.Add(il.Create(OpCodes.Ldc_I4, idx));
            seq.AddRange(pushChannel(idx));
            seq.Add(il.Create(OpCodes.Box, module.TypeSystem.Int32));
            seq.Add(il.Create(OpCodes.Stelem_Ref));
        }

        seq.Add(il.Create(OpCodes.Callvirt, callStatic));
        // No Dispose — we keep the JNI ref for the lifetime of the
        // process. AndroidJavaClass holds one global ref; cost is
        // negligible vs. reconstructing on every call.

        foreach (var i in seq)
            il.InsertBefore(finalRet, i);

        // Peak stack: [cls, "apply", arr, arr, idx, <channel value>, 255f]
        // = 7 deep momentarily during pushChannel (between Ldc_R4 and Mul).
        if (body.MaxStackSize < 8) body.MaxStackSize = 8;
    }
}

