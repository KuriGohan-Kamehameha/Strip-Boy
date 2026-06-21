# LEDBridge — AYN Thor analog-stick RGB driver.
#
# Single static method `apply(int r, int g, int b, float fBrightness)`,
# invoked from PipboyPostEffect.Update — every frame, with the same
# (r,g,b) the shader's _Color uniform is using AND the per-frame
# fBrightness multiplier the shader's _Brightness uniform uses. So the
# stick LEDs follow the screen's colour AND its flicker/pulse/scanline
# dimming.
#
# Writes directly to the SN3112L/R LED controllers via
# /sys/class/sn3112{l,r}/led/brightness — world-writable (-rw-rw-rw-)
# on stock AYN Thor firmware. No permissions required. The wire format:
#
#   /sys/class/sn3112l/led/brightness  ← "1-R:G:B:A\n"
#   /sys/class/sn3112r/led/brightness  ← "1-R:G:B:A\n"
#
# Note: prefix '1-' for BOTH paths. Verified empirically — only the
# 1-prefix actually drives the LED; 2-/3-/4- are kernel-driver no-ops.
# PATH selects side; prefix is fixed.
#
# Brightness: A = clamp(bottom_screen * fBrightness * 0.5, 0, 255).
# At bottom=100, fBrightness=1.0 → A = 50  (≈ 20 % PWM ≈ "50 % perceived"
# after LED gamma). Flicker (fBrightness drops to 0.3-0.7 briefly)
# carries the screen pulse through to the LEDs.
#
# Trailing '\n' on the payload matters — kernel driver returns EINVAL
# without it. try/catch on Throwable so any failure is silent rather
# than unwinding through PipboyPostEffect.Update.

.class public Lio/pipboy/thor/LEDBridge;
.super Ljava/lang/Object;

# One-shot diagnostic flag — set true after the first successful
# apply() entry; we log only that first call so launches stay
# debuggable without spewing 30 Hz log lines under per-frame drive.
.field private static loggedOnce:Z


.method public static apply(IIIF)V
    .registers 14
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"
    .param p3, "fBrightness"

    :try_start
    # One-shot diagnostic: first successful apply(), log (r,g,b,fB).
    sget-boolean v6, Lio/pipboy/thor/LEDBridge;->loggedOnce:Z
    if-nez v6, :no_log
    const/4 v6, 0x1
    sput-boolean v6, Lio/pipboy/thor/LEDBridge;->loggedOnce:Z
    const-string v6, "strip-boy"
    new-instance v7, Ljava/lang/StringBuilder;
    invoke-direct {v7}, Ljava/lang/StringBuilder;-><init>()V
    const-string v8, "LEDBridge.apply live r="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p0}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " g="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p1}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " b="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p2}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " fB="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p3}, Ljava/lang/StringBuilder;->append(F)Ljava/lang/StringBuilder;
    invoke-virtual {v7}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v7
    invoke-static {v6, v7}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    :no_log

    # Activity activity = UnityPlayer.currentActivity;  bail if null
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    # ContentResolver cr = activity.getContentResolver();
    invoke-virtual {v0}, Landroid/content/Context;->getContentResolver()Landroid/content/ContentResolver;
    move-result-object v1

    # int bottom = Settings.System.getInt(cr, "dual_screen_brightness_level", 50);
    const-string v2, "dual_screen_brightness_level"
    const/16 v3, 0x32
    invoke-static {v1, v2, v3}, Landroid/provider/Settings$System;->getInt(Landroid/content/ContentResolver;Ljava/lang/String;I)I
    move-result v4

    # alpha = clamp((float)bottom * fBrightness * 0.5f, 0, 255)
    int-to-float v5, v4
    mul-float v5, v5, p3
    const v2, 0x3f000000              # 0.5f
    mul-float v5, v5, v2
    # upper clamp at 255
    const v2, 0x437f0000              # 255.0f
    invoke-static {v5, v2}, Ljava/lang/Math;->min(FF)F
    move-result v5
    # lower clamp at 0
    const/4 v2, 0x0
    int-to-float v2, v2
    invoke-static {v5, v2}, Ljava/lang/Math;->max(FF)F
    move-result v5
    float-to-int v5, v5

    # writeStick(idx=1, r, g, b, alpha) — left chip
    const/4 v2, 0x1
    invoke-static {v2, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    # writeStick(idx=2, r, g, b, alpha) — right chip
    const/4 v2, 0x2
    invoke-static {v2, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "apply threw"
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

    # With .registers 11 the 5 params take v6..v10; v0..v5 are locals,
    # so we can freely use v5 as the path string.

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
    # Note: leading "1-" is fixed; only 1-prefix takes effect (verified
    # empirically that 2-/3-/4- are kernel-driver no-ops). PATH selects
    # side; the idx arg is only used by writeStick's path picker above.
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
