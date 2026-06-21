# LEDBridge — AYN Thor analog-stick RGB driver.
#
# Async architecture: PipboyPostEffect.Update calls apply(r, g, b,
# fBrightness) on the Unity main thread; apply() just stores those
# four into static "pending" fields and posts a Runnable to a
# dedicated background HandlerThread. The Runnable's run() — on the
# background thread, never the main thread — reads the pending
# values, does the saturation + alpha math, and writes
# /sys/class/sn3112{l,r}/led/brightness.
#
# Why async: the LAST time we hooked Update synchronously, the per-
# frame FileOutputStream sysfs writes (5-10 ms each × 2 sticks) ate
# Unity's frame budget and the shader's other uniform writes
# (_VScanAmount, _Flicker, _ScanlineFrequency, _TexelSize) drifted —
# visible as a gradient band in the middle of the Pip-Boy screen
# and missing scanlines. Moving sysfs to a background thread keeps
# the main-thread work under ~200 μs per frame.
#
# Coalescing: Handler.removeCallbacks + post on every apply() so
# the writer queue is always at most ONE entry — the latest. If
# 5 Update frames fire before the writer drains, the writer reads
# the LATEST pending values once and skips the intermediate.
#
# Wire format: "1-R:G:B:A\n" to both sn3112{l,r} (the kernel driver
# accepts only prefix '1-'; '2-/3-/4-' are silent no-ops). PATH
# selects side.
#
# Brightness:
#   fB_clamped = min(fBrightness, 1.0)         # bursts can't brighten
#   A = clamp(bottom_screen * fB_clamped * 0.8925, 0, 255)
#   (0.8925 = 0.35 × 2.55  =  35 % LED-PWM-max × 255/100)
#
# So at bottom=100 + no flicker: A = 89 (≈35 % PWM).
# At bottom=44 + no flicker: A = 39 (≈15 % PWM).
# Flicker (fBrightness drops to 0.3-0.7) pulls A proportionally lower.
# Bursts (fBrightness spikes ≥ 1.0) capped — no brightness pop.
#
# Saturation: subtract min channel, rescale max=255. Strips the
# white component so the LED reads pure-hue, not washed out.
# Dedupe on (sat_r, sat_g, sat_b, alpha) skips redundant sysfs writes.

.class public Lio/pipboy/thor/LEDBridge;
.super Ljava/lang/Object;
.implements Ljava/lang/Runnable;


# Background-thread handler + the singleton Runnable instance.
.field private static handler:Landroid/os/Handler;
.field private static writer:Ljava/lang/Runnable;

# Pending values posted by apply(); read by run() on background thread.
.field private static pendingR:I
.field private static pendingG:I
.field private static pendingB:I
.field private static pendingFB:F

# Dedupe — last (saturated r, g, b, alpha) successfully written.
.field private static lastR:I
.field private static lastG:I
.field private static lastB:I
.field private static lastAlpha:I

# One-shot diagnostic flag — log first successful write only.
.field private static loggedOnce:Z


.method static constructor <clinit>()V
    .registers 5
    const/4 v0, -0x1
    sput v0, Lio/pipboy/thor/LEDBridge;->lastR:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastG:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastB:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastAlpha:I

    # Spin up a dedicated HandlerThread for sysfs writes.
    new-instance v0, Landroid/os/HandlerThread;
    const-string v1, "strip-boy-led"
    invoke-direct {v0, v1}, Landroid/os/HandlerThread;-><init>(Ljava/lang/String;)V
    invoke-virtual {v0}, Landroid/os/HandlerThread;->start()V

    new-instance v1, Landroid/os/Handler;
    invoke-virtual {v0}, Landroid/os/HandlerThread;->getLooper()Landroid/os/Looper;
    move-result-object v2
    invoke-direct {v1, v2}, Landroid/os/Handler;-><init>(Landroid/os/Looper;)V
    sput-object v1, Lio/pipboy/thor/LEDBridge;->handler:Landroid/os/Handler;

    # Singleton instance — its run() is the background writer.
    new-instance v2, Lio/pipboy/thor/LEDBridge;
    invoke-direct {v2}, Lio/pipboy/thor/LEDBridge;-><init>()V
    sput-object v2, Lio/pipboy/thor/LEDBridge;->writer:Ljava/lang/Runnable;

    return-void
.end method


.method public constructor <init>()V
    .registers 1
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method


# Called from Cecil-patched PipboyPostEffect.Update on the Unity main
# thread, every frame. Stores the pending values and posts to the
# background writer. Coalesces queued posts so the writer always
# operates on the latest values.
.method public static apply(IIIF)V
    .registers 5
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"
    .param p3, "fBrightness"

    sput p0, Lio/pipboy/thor/LEDBridge;->pendingR:I
    sput p1, Lio/pipboy/thor/LEDBridge;->pendingG:I
    sput p2, Lio/pipboy/thor/LEDBridge;->pendingB:I
    sput p3, Lio/pipboy/thor/LEDBridge;->pendingFB:F

    sget-object v0, Lio/pipboy/thor/LEDBridge;->handler:Landroid/os/Handler;
    sget-object v1, Lio/pipboy/thor/LEDBridge;->writer:Ljava/lang/Runnable;
    invoke-virtual {v0, v1}, Landroid/os/Handler;->removeCallbacks(Ljava/lang/Runnable;)V
    invoke-virtual {v0, v1}, Landroid/os/Handler;->post(Ljava/lang/Runnable;)Z

    return-void
.end method


# Background-thread writer. Saturates, computes alpha, dedupes,
# writes both sticks. Wraps everything in try/catch so a failure
# never propagates to the main thread (the handler thread keeps
# running and the next apply() will get fresh work).
.method public run()V
    .registers 16

    :try_start
    # Read pending snapshot.
    sget v0, Lio/pipboy/thor/LEDBridge;->pendingR:I
    sget v1, Lio/pipboy/thor/LEDBridge;->pendingG:I
    sget v2, Lio/pipboy/thor/LEDBridge;->pendingB:I
    sget v3, Lio/pipboy/thor/LEDBridge;->pendingFB:F

    # Saturation: subtract min channel. (Don't rescale to max=255 yet —
    # we now scale to `target_max` below, since the kernel driver
    # IGNORES the 4th alpha field and brightness is entirely a
    # function of RGB magnitudes. Verified empirically: writing
    # (0,255,0):5 and (0,255,0):255 produce identical brightness;
    # writing (0,15,0):255 is properly dim.)
    invoke-static {v0, v1}, Ljava/lang/Math;->min(II)I
    move-result v4
    invoke-static {v4, v2}, Ljava/lang/Math;->min(II)I
    move-result v4
    sub-int v5, v0, v4
    sub-int v6, v1, v4
    sub-int v7, v2, v4
    invoke-static {v5, v6}, Ljava/lang/Math;->max(II)I
    move-result v4
    invoke-static {v4, v7}, Ljava/lang/Math;->max(II)I
    move-result v4
    # v5, v6, v7 = sat channels (max=v4); v4 = sat_max

    # Need ContentResolver for bottom-screen brightness lookup.
    sget-object v8, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v8, :done
    invoke-virtual {v8}, Landroid/content/Context;->getContentResolver()Landroid/content/ContentResolver;
    move-result-object v8

    # bottom = Settings.System.getInt(cr, "dual_screen_brightness_level", 50);
    const-string v9, "dual_screen_brightness_level"
    const/16 v10, 0x32
    invoke-static {v8, v9, v10}, Landroid/provider/Settings$System;->getInt(Landroid/content/ContentResolver;Ljava/lang/String;I)I
    move-result v8

    # fB_clamped = min(fBrightness, 1.0)   ← bursts can't brighten
    const v9, 0x3f800000
    invoke-static {v3, v9}, Ljava/lang/Math;->min(FF)F
    move-result v3

    # target_max_f = (float)bottom * fB_clamped * 0.1275
    # 0.1275 = 0.05 × 2.55  (5% RGB ceiling × 255/100).
    # At bottom=100, fB=1.0 → target_max = 12  (max-channel value)
    # At bottom=44,  fB=1.0 → target_max = 5
    int-to-float v9, v8
    mul-float v9, v9, v3
    const v10, 0x3e028f5c              # 0.1275f
    mul-float v9, v9, v10

    # clamp upper at 255.0
    const v10, 0x437f0000
    invoke-static {v9, v10}, Ljava/lang/Math;->min(FF)F
    move-result v9
    # clamp lower at 0.0
    const/4 v10, 0x0
    int-to-float v10, v10
    invoke-static {v9, v10}, Ljava/lang/Math;->max(FF)F
    move-result v9
    float-to-int v9, v9
    # v9 = target_max (integer 0..255, typically small — 5..12)

    # Scale saturated channels TO target_max. After this, max(v5,v6,v7) = v9.
    # If sat_max == 0 (input was pure grey/white), output stays (0,0,0).
    if-eqz v4, :scale_done
    mul-int v5, v5, v9
    div-int v5, v5, v4
    mul-int v6, v6, v9
    div-int v6, v6, v4
    mul-int v7, v7, v9
    div-int v7, v7, v4
    :scale_done

    # Dedupe — skip the two sysfs writes if (r, g, b) matches the last
    # successful write. Alpha is constant (255, ignored by the kernel
    # driver) so it doesn't participate in the compare.
    sget v10, Lio/pipboy/thor/LEDBridge;->lastR:I
    if-ne v5, v10, :dirty
    sget v10, Lio/pipboy/thor/LEDBridge;->lastG:I
    if-ne v6, v10, :dirty
    sget v10, Lio/pipboy/thor/LEDBridge;->lastB:I
    if-eq v7, v10, :done

    :dirty
    sput v5, Lio/pipboy/thor/LEDBridge;->lastR:I
    sput v6, Lio/pipboy/thor/LEDBridge;->lastG:I
    sput v7, Lio/pipboy/thor/LEDBridge;->lastB:I
    # alpha is constant — write fixed 255 below; keep lastAlpha unset.

    # One-shot diagnostic — first real write only.
    sget-boolean v10, Lio/pipboy/thor/LEDBridge;->loggedOnce:Z
    if-nez v10, :no_log
    const/4 v10, 0x1
    sput-boolean v10, Lio/pipboy/thor/LEDBridge;->loggedOnce:Z
    const-string v10, "strip-boy"
    new-instance v11, Ljava/lang/StringBuilder;
    invoke-direct {v11}, Ljava/lang/StringBuilder;-><init>()V
    const-string v12, "first write scaled=("
    invoke-virtual {v11, v12}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v5}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v12, ","
    invoke-virtual {v11, v12}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v6}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v12}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v7}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v12, ") target_max="
    invoke-virtual {v11, v12}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v9}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v12, " bottom="
    invoke-virtual {v11, v12}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v11, v8}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v11}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v11
    invoke-static {v10, v11}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    :no_log

    # alpha = 255 (ignored by the kernel driver; brightness is in RGB
    # magnitudes after the target_max scaling above).
    const/16 v9, 0xff

    # writeStick(1, r, g, b, 255) — left chip
    const/4 v10, 0x1
    invoke-static {v10, v5, v6, v7, v9}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    # writeStick(2, r, g, b, 255) — right chip
    const/4 v10, 0x2
    invoke-static {v10, v5, v6, v7, v9}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "writer threw"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method


.method private static writeStick(IIIII)V
    .registers 11
    .param p0, "idx"
    .param p1, "r"
    .param p2, "g"
    .param p3, "b"
    .param p4, "a"

    # path = "/sys/class/sn3112" + (idx == 1 ? "l" : "r") + "/led/brightness"
    new-instance v0, Ljava/lang/StringBuilder;
    invoke-direct {v0}, Ljava/lang/StringBuilder;-><init>()V
    const-string v1, "/sys/class/sn3112"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    const/4 v1, 0x1
    if-ne p0, v1, :right
    const-string v1, "l"
    goto :side_done
    :right
    const-string v1, "r"
    :side_done
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    const-string v1, "/led/brightness"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v5

    # payload = "1-" + r + ":" + g + ":" + b + ":" + a + "\n"
    new-instance v0, Ljava/lang/StringBuilder;
    invoke-direct {v0}, Ljava/lang/StringBuilder;-><init>()V
    const-string v1, "1-"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p1}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v1, ":"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p2}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p3}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p4}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v1, "\n"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v2

    # FileOutputStream(path).write(payload.getBytes()); close()
    new-instance v3, Ljava/io/FileOutputStream;
    invoke-direct {v3, v5}, Ljava/io/FileOutputStream;-><init>(Ljava/lang/String;)V
    invoke-virtual {v2}, Ljava/lang/String;->getBytes()[B
    move-result-object v4
    invoke-virtual {v3, v4}, Ljava/io/FileOutputStream;->write([B)V
    invoke-virtual {v3}, Ljava/io/FileOutputStream;->close()V

    return-void
.end method
