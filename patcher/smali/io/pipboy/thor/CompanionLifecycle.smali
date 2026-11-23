.class public Lio/pipboy/thor/CompanionLifecycle;
.super Ljava/lang/Object;
.implements Landroid/app/Application$ActivityLifecycleCallbacks;
.source "CompanionLifecycle.smali"

# Follows the Unity activity's lifecycle so the companion stops working
# while the Pip-Boy is off screen.
#
#   resumed   -> LEDBridge.resumeKeepAlive(), GameWatch.onShown()
#   paused    -> LEDBridge.suspendKeepAlive(), GameWatch.onHidden()
#   destroyed -> same as paused
#
# Registered once per process, from LauncherActivity.onCreate and again
# (idempotently) from LEDBridge.apply, which also covers a start from
# Recents that re-creates the Unity activity without the launcher.
# Every other activity, including LauncherActivity, is ignored.

.field private static registered:Z


.method public constructor <init>()V
    .locals 0
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method


# Install the callbacks on the activity's Application. Safe to call from any
# thread, any number of times; a null activity is a no-op.
.method public static declared-synchronized register(Landroid/app/Activity;)V
    .locals 3
    .param p0, "activity"

    :try_start
    if-eqz p0, :done
    sget-boolean v0, Lio/pipboy/thor/CompanionLifecycle;->registered:Z
    if-nez v0, :done

    invoke-virtual {p0}, Landroid/app/Activity;->getApplication()Landroid/app/Application;
    move-result-object v0
    if-eqz v0, :done

    new-instance v1, Lio/pipboy/thor/CompanionLifecycle;
    invoke-direct {v1}, Lio/pipboy/thor/CompanionLifecycle;-><init>()V
    invoke-virtual {v0, v1}, Landroid/app/Application;->registerActivityLifecycleCallbacks(Landroid/app/Application$ActivityLifecycleCallbacks;)V

    const/4 v0, 0x1
    sput-boolean v0, Lio/pipboy/thor/CompanionLifecycle;->registered:Z

    const-string v1, "strip-boy"
    const-string v2, "lifecycle hooks registered"
    invoke-static {v1, v2}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch

    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "lifecycle hook registration failed"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method


.method private static isPipBoy(Landroid/app/Activity;)Z
    .locals 2
    .param p0, "activity"

    if-eqz p0, :no
    invoke-virtual {p0}, Ljava/lang/Object;->getClass()Ljava/lang/Class;
    move-result-object v0
    invoke-virtual {v0}, Ljava/lang/Class;->getName()Ljava/lang/String;
    move-result-object v0
    const-string v1, "com.unity3d.player.UnityPlayerNativeActivity"
    invoke-virtual {v1, v0}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v0
    return v0

    :no
    const/4 v0, 0x0
    return v0
.end method


.method private static onShown()V
    .locals 0
    invoke-static {}, Lio/pipboy/thor/LEDBridge;->resumeKeepAlive()V
    invoke-static {}, Lio/pipboy/thor/GameWatch;->onShown()V
    return-void
.end method


.method private static onHidden()V
    .locals 0
    invoke-static {}, Lio/pipboy/thor/LEDBridge;->suspendKeepAlive()V
    invoke-static {}, Lio/pipboy/thor/GameWatch;->onHidden()V
    return-void
.end method


.method public onActivityResumed(Landroid/app/Activity;)V
    .locals 1
    .param p1, "activity"

    invoke-static {p1}, Lio/pipboy/thor/CompanionLifecycle;->isPipBoy(Landroid/app/Activity;)Z
    move-result v0
    if-eqz v0, :done
    invoke-static {}, Lio/pipboy/thor/CompanionLifecycle;->onShown()V
    :done
    return-void
.end method


.method public onActivityPaused(Landroid/app/Activity;)V
    .locals 1
    .param p1, "activity"

    invoke-static {p1}, Lio/pipboy/thor/CompanionLifecycle;->isPipBoy(Landroid/app/Activity;)Z
    move-result v0
    if-eqz v0, :done
    invoke-static {}, Lio/pipboy/thor/CompanionLifecycle;->onHidden()V
    :done
    return-void
.end method


.method public onActivityDestroyed(Landroid/app/Activity;)V
    .locals 1
    .param p1, "activity"

    invoke-static {p1}, Lio/pipboy/thor/CompanionLifecycle;->isPipBoy(Landroid/app/Activity;)Z
    move-result v0
    if-eqz v0, :done
    invoke-static {}, Lio/pipboy/thor/CompanionLifecycle;->onHidden()V
    :done
    return-void
.end method


.method public onActivityCreated(Landroid/app/Activity;Landroid/os/Bundle;)V
    .locals 0
    return-void
.end method


.method public onActivityStarted(Landroid/app/Activity;)V
    .locals 0
    return-void
.end method


.method public onActivityStopped(Landroid/app/Activity;)V
    .locals 0
    return-void
.end method


.method public onActivitySaveInstanceState(Landroid/app/Activity;Landroid/os/Bundle;)V
    .locals 0
    return-void
.end method
