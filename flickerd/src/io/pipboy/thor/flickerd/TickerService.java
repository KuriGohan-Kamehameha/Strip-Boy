package io.pipboy.thor.flickerd;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Log;

import java.io.FileOutputStream;

/**
 * Owns the LED tick loop. Spins up a dedicated HandlerThread at ~30 Hz that
 * reads the latest broadcast colour, generates a pulse + occasional flicker
 * dip, and writes the result to /sys/class/sn3112{l,r}/led/brightness.
 *
 * Brightness model (mirrors the in-Strip-Boy LEDBridge):
 *   target_max = bottom_screen_brightness * 0.1275      // 5% × 255/100
 *   mult       = pulse(t) * flicker(rand)
 *   write RGB scaled so max channel = target_max * mult
 *
 * Auto-stops itself after IDLE_MS without a new colour broadcast — so the
 * service goes away when the Pip-Boy companion is backgrounded / closed.
 */
public class TickerService extends Service {

    private static final String TAG = "strip-boy-flickerd";
    private static final long IDLE_MS = 30_000;   // auto-shutdown after 30s silence
    private static final long TICK_MS = 33;       // ~30 Hz
    private static final String CHANNEL_ID = "flickerd";
    private static final int NOTIF_ID = 1;

    private static final String LEFT_PATH  = "/sys/class/sn3112l/led/brightness";
    private static final String RIGHT_PATH = "/sys/class/sn3112r/led/brightness";

    /** Latest colour broadcast in from ColorReceiver. */
    private static volatile int colorR, colorG, colorB;
    private static volatile boolean haveColor;
    private static volatile long lastColorMs;

    private HandlerThread thread;
    private Handler handler;
    private long startMillis;

    public static void updateColor(int r, int g, int b) {
        colorR = r;
        colorG = g;
        colorB = b;
        haveColor = true;
        lastColorMs = SystemClock.elapsedRealtime();
    }

    @Override
    public void onCreate() {
        super.onCreate();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            ensureChannel();
            startForeground(NOTIF_ID, buildNotification());
        }
        thread = new HandlerThread("strip-boy-flickerd");
        thread.start();
        handler = new Handler(thread.getLooper());
        startMillis = SystemClock.elapsedRealtime();
        handler.post(tick);
        Log.i(TAG, "TickerService started");
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        // Receiver already set the colour via updateColor before kicking us
        // off; nothing extra to do here.
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }

    @Override
    public void onDestroy() {
        super.onDestroy();
        if (thread != null) thread.quitSafely();
        haveColor = false;
        Log.i(TAG, "TickerService stopped");
    }

    /** Tick loop — runs on the HandlerThread, never the main thread. */
    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            try {
                if (!haveColor) {
                    // Wait for first colour broadcast.
                    handler.postDelayed(this, TICK_MS);
                    return;
                }
                long sinceColor = SystemClock.elapsedRealtime() - lastColorMs;
                if (sinceColor > IDLE_MS) {
                    Log.i(TAG, "idle for " + sinceColor + "ms — stopping");
                    stopSelf();
                    return;
                }
                writeOneTick();
            } catch (Throwable t) {
                Log.w(TAG, "tick threw", t);
            }
            handler.postDelayed(this, TICK_MS);
        }
    };

    /** Compute one tick's RGB + scale + sysfs write. */
    private void writeOneTick() throws Exception {
        // -- pulse (slow sin oscillation) ----------------------------------
        long t = SystemClock.elapsedRealtime() - startMillis;
        double pulsePhase = (t % 4000L) / 4000.0 * 2 * Math.PI;
        double pulse = 0.85 + 0.15 * Math.sin(pulsePhase);

        // -- flicker (occasional brief dip) --------------------------------
        // ~4% chance per tick of a dip event lasting 1-3 ticks. We model
        // this stateful-ly via a static countdown so a single dip persists
        // across multiple ticks (more visible than a single-frame flash).
        if (flickerHoldTicks > 0) {
            flickerHoldTicks--;
        } else if (Math.random() < 0.04) {
            flickerHoldTicks = 1 + (int)(Math.random() * 3);
        }
        double flicker = (flickerHoldTicks > 0)
            ? 0.25 + Math.random() * 0.35   // 0.25..0.60 during dip
            : 1.0;

        double mult = Math.min(1.0, pulse * flicker);

        // -- scale RGB to target_max ----------------------------------------
        int bottom;
        try {
            bottom = Settings.System.getInt(getContentResolver(),
                "dual_screen_brightness_level", 50);
        } catch (Settings.SettingNotFoundException e) {
            bottom = 50;
        }
        // target_max = bottom * 0.1275 (5% PWM × screen brightness)
        double targetMax = bottom * 0.1275 * mult;

        // Saturated channel scaling: caller has already given us the
        // pure-hue colour (Strip-Boy's LEDBridge does the subtract-min
        // / rescale dance before broadcasting). We just scale by
        // targetMax / 255.
        double scale = targetMax / 255.0;
        int r = clamp((int)(colorR * scale));
        int g = clamp((int)(colorG * scale));
        int b = clamp((int)(colorB * scale));

        String payload = "1-" + r + ":" + g + ":" + b + ":255\n";
        byte[] bytes = payload.getBytes();
        writeFile(LEFT_PATH, bytes);
        writeFile(RIGHT_PATH, bytes);
    }

    private static int flickerHoldTicks = 0;

    private static int clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

    private static void writeFile(String path, byte[] bytes) throws Exception {
        try (FileOutputStream out = new FileOutputStream(path)) {
            out.write(bytes);
        }
    }

    // ---- foreground service notification (API 26+) ----------------------

    private void ensureChannel() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm.getNotificationChannel(CHANNEL_ID) == null) {
            NotificationChannel ch = new NotificationChannel(
                CHANNEL_ID, "Strip-Boy LED tick",
                NotificationManager.IMPORTANCE_MIN);
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
    }

    private Notification buildNotification() {
        return new Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Strip-Boy LED")
            .setContentText("Stick LED tick running")
            .setSmallIcon(android.R.drawable.sym_def_app_icon)
            .setOngoing(true)
            .build();
    }
}
