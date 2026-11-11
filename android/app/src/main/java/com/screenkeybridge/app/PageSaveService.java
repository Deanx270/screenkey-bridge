package com.screenkeybridge.app;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Only the authorized ADB shell can request a background HTML save. */
public final class PageSaveService extends Service {
    private static final String CHANNEL = "page_saves";
    private static final int NOTIFICATION = 1452;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    @Override public void onCreate() { super.onCreate(); getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel(CHANNEL, "Page saves", NotificationManager.IMPORTANCE_LOW)); }
    private Notification notice(String text, boolean ongoing) {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_menu_save).setContentTitle(getApplicationInfo().loadLabel(getPackageManager())).setContentText(text).setContentIntent(open).setOngoing(ongoing).setAutoCancel(!ongoing).build();
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(NOTIFICATION, notice("Saving Edge page…", true));
        String id = intent == null ? null : intent.getStringExtra("request_id");
        if (id == null || !id.matches("[A-F0-9]{32}")) { stopSelfResult(startId); return START_NOT_STICKY; }
        worker.execute(() -> {
            String secret = getSharedPreferences("MainActivity", MODE_PRIVATE).getString("key", "");
            boolean saved = false; String message;
            try {
                if (secret == null || !secret.matches("[A-F0-9]{64}")) throw new Exception("Phone is not paired.");
                PageSaver.html(this, PageSaver.fetch(secret, id)); saved = true; message = "HTML saved to Downloads/ScreenKey Bridge.";
            } catch (Exception e) { message = "Page save failed: " + e.getMessage(); }
            if (secret != null && secret.matches("[A-F0-9]{64}")) PageSaver.acknowledge(secret, id, saved);
            final String result = message;
            getSharedPreferences("capture_status", MODE_PRIVATE).edit().putString("message", result).putLong("time", System.currentTimeMillis()).apply();
            if (stopSelfResult(startId)) { stopForeground(STOP_FOREGROUND_REMOVE); try { getSystemService(NotificationManager.class).notify(NOTIFICATION, notice(result, false)); } catch (SecurityException ignored) { } }
        });
        return START_NOT_STICKY;
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public void onDestroy() { worker.shutdown(); super.onDestroy(); }
}
