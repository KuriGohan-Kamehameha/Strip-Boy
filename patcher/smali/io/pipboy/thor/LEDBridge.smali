# LEDBridge — AYN Thor analog-stick RGB driver.
#
# Single static method `apply(int r, int g, int b)`, invoked from the
# tail of PipboyPostEffect.SetColor — i.e. literally once per screen
# colour change, no more, no less.
#
# Writes directly to the SN3112L (left) and SN3112R (right) LED
# controllers via /sys/class/sn3112{l,r}/led/brightness — world-
# writable (-rw-rw-rw-) on stock AYN Thor firmware. Same path
# Moonbench's Bifrost LED utility uses; no permissions required.
#
# Wire format (Bifrost-compatible):
#
#   /sys/class/sn3112l/led/brightness  ← "1-R:G:B:A\n"
#   /sys/class/sn3112r/led/brightness  ← "2-R:G:B:A\n"
#
# Trailing newline matters — the kernel driver returns EINVAL on
# writes without it.
#
# Brightness (A) matches the bottom-screen brightness slider with a
# 70 % ceiling:
#
#   A = min(dual_screen_brightness_level, 70) * 255 / 100
#
# Wrapped in try/catch (Throwable) so any failure — kernel EINVAL,
# SELinux denial, missing class — is swallowed silently rather than
# unwinding through PipboyPostEffect.SetColor and breaking the
# screen-colour chain.

.class public Lio/pipboy/thor/LEDBridge;
.super Ljava/lang/Object;


.method public static apply(III)V
    .registers 13
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"

    :try_start
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    invoke-virtual {v0}, Landroid/content/Context;->getContentResolver()Landroid/content/ContentResolver;
    move-result-object v1

    # int bottom = Settings.System.getInt(cr, "dual_screen_brightness_level", 50);
    const-string v2, "dual_screen_brightness_level"
    const/16 v3, 0x32
    invoke-static {v1, v2, v3}, Landroid/provider/Settings$System;->getInt(Landroid/content/ContentResolver;Ljava/lang/String;I)I
    move-result v4

    # alpha = min(bottom, 50) * 255 / 100  (50 % ceiling)
    const/16 v2, 0x32
    invoke-static {v4, v2}, Ljava/lang/Math;->min(II)I
    move-result v5
    mul-int/lit16 v5, v5, 0xff
    div-int/lit8 v5, v5, 0x64

    # Write left stick:  idx=1 → "1-R:G:B:A\n" → /sys/class/sn3112l/led/brightness
    const/4 v2, 0x1
    invoke-static {v2, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    # Write right stick: idx=2 → "2-R:G:B:A\n" → /sys/class/sn3112r/led/brightness
    const/4 v2, 0x2
    invoke-static {v2, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->writeStick(IIIII)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    return-void

    :catch
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
    # so we can freely use v5 as the path string (would have clobbered
    # p0=v5 under .registers 10).

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
    # Note: the leading "1-" is fixed, NOT the stick index. Each
    # SN3112 chip exposes a single addressable LED group; writing
    # prefix 2-/3-/4- is a silent no-op on either chip. Verified
    # empirically by writing 1-RED, 2-GREEN, 3-BLUE, 4-YELLOW in
    # sequence to one chip — only the 1- (red) took effect.
    # PATH (sn3112l vs sn3112r) is what selects left vs right;
    # the `idx` arg is used only by writeStick's path picker.
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
