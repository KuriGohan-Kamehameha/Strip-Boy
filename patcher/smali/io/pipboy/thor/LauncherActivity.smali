.class public Lio/pipboy/thor/LauncherActivity;
.super Landroid/app/Activity;
.source "LauncherActivity.smali"


# Thin launcher that re-launches UnityPlayerNativeActivity on the first
# non-default Display. If there's no secondary display, it just launches
# normally. This is how we ship the AYN Thor "always launch on the bottom
# screen" behaviour without modifying any Unity code.


.method public constructor <init>()V
    .locals 0

    invoke-direct {p0}, Landroid/app/Activity;-><init>()V

    return-void
.end method


.method protected onCreate(Landroid/os/Bundle;)V
    .locals 8

    # super.onCreate(savedInstanceState)
    invoke-super {p0, p1}, Landroid/app/Activity;->onCreate(Landroid/os/Bundle;)V

    # --- default green LED state ---------------------------------------
    # Write a dim default-green payload to BOTH stick controllers
    # before Unity loads, so the sticks come on in a known state
    # instead of whatever leftover colour the kernel was holding (e.g.
    # bright white from a stale Bifrost session). Once Unity boots and
    # PipboyPostEffect.SetColor fires, LEDBridge.apply overwrites with
    # the actual saved/F4-delivered colour. /sys/class/sn3112{l,r}/led/
    # brightness is world-writable on stock AYN Thor firmware, so the
    # write succeeds from this activity's own UID — no UnityPlayer
    # required (it's not set yet at this point in the launch flow).
    :default_led_try
    new-instance v0, Ljava/io/FileOutputStream;
    const-string v1, "/sys/class/sn3112l/led/brightness"
    invoke-direct {v0, v1}, Ljava/io/FileOutputStream;-><init>(Ljava/lang/String;)V
    const-string v1, "1-0:5:0:255\n"
    invoke-virtual {v1}, Ljava/lang/String;->getBytes()[B
    move-result-object v1
    invoke-virtual {v0, v1}, Ljava/io/FileOutputStream;->write([B)V
    invoke-virtual {v0}, Ljava/io/FileOutputStream;->close()V

    new-instance v0, Ljava/io/FileOutputStream;
    const-string v1, "/sys/class/sn3112r/led/brightness"
    invoke-direct {v0, v1}, Ljava/io/FileOutputStream;-><init>(Ljava/lang/String;)V
    const-string v1, "1-0:5:0:255\n"
    invoke-virtual {v1}, Ljava/lang/String;->getBytes()[B
    move-result-object v1
    invoke-virtual {v0, v1}, Ljava/io/FileOutputStream;->write([B)V
    invoke-virtual {v0}, Ljava/io/FileOutputStream;->close()V
    :default_led_end
    .catch Ljava/lang/Throwable; {:default_led_try .. :default_led_end} :default_led_catch
    goto :default_led_done
    :default_led_catch
    move-exception v0
    :default_led_done
    # --- end default green LED state ----------------------------------

    # v0 = new Intent()
    new-instance v0, Landroid/content/Intent;

    invoke-direct {v0}, Landroid/content/Intent;-><init>()V

    # v1 = this.getPackageName()
    invoke-virtual {p0}, Landroid/app/Activity;->getPackageName()Ljava/lang/String;

    move-result-object v1

    # v2 = "com.unity3d.player.UnityPlayerNativeActivity"
    const-string v2, "com.unity3d.player.UnityPlayerNativeActivity"

    # v0.setClassName(v1, v2)
    invoke-virtual {v0, v1, v2}, Landroid/content/Intent;->setClassName(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;

    # v1 = Intent.FLAG_ACTIVITY_NEW_TASK (0x10000000) — required for setLaunchDisplayId
    const/high16 v1, 0x10000000

    # v0.setFlags(v1)
    invoke-virtual {v0, v1}, Landroid/content/Intent;->setFlags(I)Landroid/content/Intent;

    # v1 = (DisplayManager) this.getSystemService("display")
    const-string v1, "display"

    invoke-virtual {p0, v1}, Landroid/app/Activity;->getSystemService(Ljava/lang/String;)Ljava/lang/Object;

    move-result-object v1

    check-cast v1, Landroid/hardware/display/DisplayManager;

    # v2 = displayManager.getDisplays()
    invoke-virtual {v1}, Landroid/hardware/display/DisplayManager;->getDisplays()[Landroid/view/Display;

    move-result-object v2

    # v3 = -1            (targetDisplayId)
    const/4 v3, -0x1

    # v4 = 0             (loop index)
    const/4 v4, 0x0

    # v5 = displays.length
    array-length v5, v2

    :loop_start
    if-ge v4, v5, :loop_end

    aget-object v6, v2, v4

    invoke-virtual {v6}, Landroid/view/Display;->getDisplayId()I

    move-result v7

    # if (displayId == 0) continue   (skip the DEFAULT_DISPLAY)
    if-eqz v7, :loop_continue

    # targetId = displayId; break
    move v3, v7

    goto :loop_end

    :loop_continue
    add-int/lit8 v4, v4, 0x1

    goto :loop_start

    :loop_end

    # if (targetId < 0) goto no_options
    if-ltz v3, :no_options

    # v1 = ActivityOptions.makeBasic()
    invoke-static {}, Landroid/app/ActivityOptions;->makeBasic()Landroid/app/ActivityOptions;

    move-result-object v1

    # v1.setLaunchDisplayId(v3)
    invoke-virtual {v1, v3}, Landroid/app/ActivityOptions;->setLaunchDisplayId(I)Landroid/app/ActivityOptions;

    move-result-object v1

    # v1 = v1.toBundle()
    invoke-virtual {v1}, Landroid/app/ActivityOptions;->toBundle()Landroid/os/Bundle;

    move-result-object v1

    # this.startActivity(v0, v1)
    invoke-virtual {p0, v0, v1}, Landroid/app/Activity;->startActivity(Landroid/content/Intent;Landroid/os/Bundle;)V

    goto :done

    :no_options
    # this.startActivity(v0)
    invoke-virtual {p0, v0}, Landroid/app/Activity;->startActivity(Landroid/content/Intent;)V

    :done
    # this.finish()
    invoke-virtual {p0}, Landroid/app/Activity;->finish()V

    return-void
.end method
