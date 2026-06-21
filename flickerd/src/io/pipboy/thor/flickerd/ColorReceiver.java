package io.pipboy.thor.flickerd;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.util.Log;

/**
 * Receives io.pipboy.thor.SET_COLOR broadcasts from the patched Pip-Boy
 * companion app. Stores the latest colour as a class-static and ensures
 * TickerService is alive so the HandlerThread can pick the colour up on
 * its next tick.
 *
 * The receiver is the cheap-fast path — it doesn't touch sysfs itself,
 * it just nudges the service. All LED work happens in TickerService.run.
 */
public class ColorReceiver extends BroadcastReceiver {

    private static final String TAG = "strip-boy-flickerd";

    @Override
    public void onReceive(Context ctx, Intent intent) {
        String action = intent.getAction();
        if (action == null) return;

        switch (action) {
            case "io.pipboy.thor.SET_COLOR": {
                int r = intent.getIntExtra("r", 0);
                int g = intent.getIntExtra("g", 0);
                int b = intent.getIntExtra("b", 0);
                TickerService.updateColor(r, g, b);

                Intent svc = new Intent(ctx, TickerService.class);
                try {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                        ctx.startForegroundService(svc);
                    } else {
                        ctx.startService(svc);
                    }
                } catch (Throwable t) {
                    // The ticker can be killed by Android in the background;
                    // if so just log and move on — next broadcast will retry.
                    Log.w(TAG, "startService failed: " + t.getMessage());
                }
                break;
            }
            case "io.pipboy.thor.STOP": {
                ctx.stopService(new Intent(ctx, TickerService.class));
                break;
            }
            default:
                /* unknown action — ignore */
        }
    }
}
