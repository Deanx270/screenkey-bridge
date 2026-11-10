package com.screenkeybridge.app;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.ComponentName;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.service.quicksettings.TileService;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Started by our tile or by authorized ADB shell; never captures without a paired PC. */
public final class CaptureService extends Service {
    private static final String CHANNEL = "capture_results";
    private static final int NOTIFICATION = 1451;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());

    @Override public void onCreate() {
        super.onCreate();
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL, "PC captures", NotificationManager.IMPORTANCE_LOW));
    }
    private Notification notification(String text, boolean ongoing) {
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent intent = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("ScreenKey Bridge").setContentText(text).setContentIntent(intent)
            .setOnlyAlertOnce(true).setOngoing(ongoing).setAutoCancel(!ongoing).build();
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(NOTIFICATION, notification("Capturing PC screen…", true));
        String requestId = intent == null ? null : intent.getStringExtra("request_id");
        if (requestId != null && !requestId.matches("[A-F0-9]{32}")) { stopSelfResult(startId); return START_NOT_STICKY; }
        worker.execute(() -> {
            String secret = getSharedPreferences("MainActivity", MODE_PRIVATE).getString("key", "");
            String display = getSharedPreferences("MainActivity", MODE_PRIVATE).getString("display", "");
            boolean success = false;
            CaptureStorage.ClipboardResult clipboard = CaptureStorage.ClipboardResult.FAILED;
            String result;
            try {
                if (secret == null || !secret.matches("[0-9A-F]{64}")) throw new Exception("Set the pairing key in the app first");
                clipboard = CaptureStorage.capture(this, secret, display == null ? "" : display, requestId);
                success = true; result = CaptureStorage.message(clipboard);
            } catch (Exception e) { result = "Capture failed: " + (e.getMessage() == null ? "check your connection" : e.getMessage()); }
            if (requestId != null && secret != null && secret.matches("[0-9A-F]{64}")) acknowledge(secret, requestId, success, clipboard);
            final String message = result;
            getSharedPreferences("capture_status", MODE_PRIVATE).edit().putString("tile", success ? (clipboard == CaptureStorage.ClipboardResult.COPIED ? "Saved and copied" : "Saved; check clipboard") : "Capture failed")
                .putString("message", result).putLong("time", System.currentTimeMillis()).apply();
            try { TileService.requestListeningState(this, new ComponentName(this, CaptureTile.class)); } catch (RuntimeException ignored) { }
            main.post(() -> {
                if (stopSelfResult(startId)) {
                    stopForeground(STOP_FOREGROUND_REMOVE);
                    if (Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
                        try { getSystemService(NotificationManager.class).notify(NOTIFICATION, notification(message, false)); } catch (SecurityException ignored) { }
                    }
                }
            });
        });
        return START_NOT_STICKY;
    }
    private void acknowledge(String secret, String id, boolean success, CaptureStorage.ClipboardResult clipboard) {
        try {
            String url = "http://127.0.0.1:38473/capture/ack?id=" + URLEncoder.encode(id, "UTF-8") + "&ok=" + (success ? "1" : "0") + "&clipboard=" + clipboard.name().toLowerCase(java.util.Locale.ROOT);
            HttpURLConnection c = (HttpURLConnection)new URL(url).openConnection();
            try { c.setRequestMethod("POST"); c.setConnectTimeout(1000); c.setReadTimeout(1000); c.setRequestProperty("X-ScreenKey-Token", secret); c.getResponseCode(); }
            finally { c.disconnect(); }
        } catch (Exception ignored) { }
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public void onDestroy() { worker.shutdown(); super.onDestroy(); }
}
