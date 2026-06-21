.class public Lio/pipboy/thor/LEDClear;
.super Ljava/lang/Object;

# Proactive deactivation signal for Bifrost.
#
# When the Fallout 4 companion app backgrounds, the Pip-Boy is no longer
# on screen, so its stick-LED effect should stop. Without a signal Bifrost
# only notices when the ACTION_DISPLAY lease goes stale (a heartbeat or two
# later). This sends an ACTION_CLEAR the instant the app is paused so Bifrost
# reverts immediately — no lease wait.
#
# Hooked from FlowManager.OnApplicationPause(true) via AndroidJavaClass.
# Mirrors LEDBridge.sendPulse's broadcast plumbing; no extras — Bifrost
# resolves the caller package from our UID, so an empty ACTION_CLEAR to the
# override owner is sufficient. Self-guarded: any failure is swallowed.

.method public constructor <init>()V
    .registers 1
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method

.method public static onDeactivate()V
    .registers 5

    :try_start
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    new-instance v1, Landroid/content/Intent;
    const-string v2, "com.moonbench.bifrost.api.ACTION_CLEAR"
    invoke-direct {v1, v2}, Landroid/content/Intent;-><init>(Ljava/lang/String;)V

    new-instance v2, Landroid/content/ComponentName;
    const-string v3, "com.moonbench.bifrost"
    const-string v4, "com.moonbench.bifrost.external.ExternalApiReceiver"
    invoke-direct {v2, v3, v4}, Landroid/content/ComponentName;-><init>(Ljava/lang/String;Ljava/lang/String;)V
    invoke-virtual {v1, v2}, Landroid/content/Intent;->setComponent(Landroid/content/ComponentName;)Landroid/content/Intent;

    invoke-virtual {v0, v1}, Landroid/app/Activity;->sendBroadcast(Landroid/content/Intent;)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch

    return-void

    :catch
    move-exception v0
    return-void
.end method
