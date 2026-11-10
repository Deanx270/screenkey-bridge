package com.screenkeybridge.app;

import android.app.Activity;
import android.content.ContentValues;
import android.content.Intent;
import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.provider.MediaStore;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.view.inputmethod.InputMethodManager;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private EditText key;
    private TextView status;
    private Button capture;
    private TextView keyboardStatus;
    private final Handler main = new Handler(Looper.getMainLooper());
    private boolean visible;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        int pad = (int)(24 * getResources().getDisplayMetrics().density);
        layout.setPadding(pad, pad, pad, pad);
        layout.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = new TextView(this);
        title.setText("ScreenKey Bridge"); title.setTextSize(26);
        layout.addView(title);
        TextView instructions = new TextView(this);
        instructions.setText("Enter the pairing key shown by the Windows tray app. Keep ADB connected.");
        layout.addView(instructions);
        key = new EditText(this);
        key.setSingleLine(true); key.setHint("Windows pairing key");
        key.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        key.setText(getPreferences(MODE_PRIVATE).getString("key", ""));
        layout.addView(key);
        capture = new Button(this); capture.setText("Capture PC screen");
        layout.addView(capture);
        status = new TextView(this); status.setText("Ready"); layout.addView(status);
        Button enable = new Button(this); enable.setText("Enable ScreenKey keyboard");
        enable.setOnClickListener(v -> startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))); layout.addView(enable);
        Button choose = new Button(this); choose.setText("Choose ScreenKey keyboard");
        choose.setOnClickListener(v -> ((InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE)).showInputMethodPicker()); layout.addView(choose);
        Button toggle = new Button(this); toggle.setText("Toggle PC / phone keyboard");
        toggle.setOnClickListener(v -> {
            String secret = key.getText().toString().trim();
            if (!secret.matches("[0-9A-F]{64}")) { status.setText("Enter the Windows pairing key first."); return; }
            getPreferences(MODE_PRIVATE).edit().putString("key", secret).apply();
            worker.execute(() -> { try { String s = BridgeInputMethod.request(this, "toggle").getString("state"); runOnUiThread(() -> keyboardStatus.setText(modeLabel(s))); }
                catch (Exception e) { runOnUiThread(() -> keyboardStatus.setText(connectionError(e))); } });
        }); layout.addView(toggle);
        keyboardStatus = new TextView(this); keyboardStatus.setText("Keyboard: checking"); layout.addView(keyboardStatus);
        capture.setOnClickListener(v -> {
            String secret = key.getText().toString().trim();
            if (!secret.matches("[0-9A-F]{64}")) { status.setText("Enter the 64-character pairing key from Windows."); return; }
            getPreferences(MODE_PRIVATE).edit().putString("key", secret).apply();
            capture.setEnabled(false); status.setText("Capturing…");
            worker.execute(() -> {
                String result;
                try { saveCapture(secret); result = "Saved to Pictures/ScreenKey Bridge."; }
                catch (Exception e) { result = "Capture failed: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
                String message = result;
                runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) { status.setText(message); capture.setEnabled(true); } });
            });
        });
        setContentView(layout);
    }

    private static String modeLabel(String state) {
        if ("waiting".equals(state)) return "Keyboard: waiting for a phone text field (30 seconds)";
        if ("phone".equals(state)) return "Keyboard: phone";
        return "Keyboard: PC";
    }
    private static String connectionError(Exception error) {
        String message = error.getMessage();
        if (message != null && message.contains("Pairing key rejected")) return "Connection: pairing key rejected; check the key.";
        if (message != null && message.contains("Set the pairing key")) return "Connection: enter the PC pairing key first.";
        return "Connection: PC unavailable. Check the companion and ADB connection.";
    }
    private final Runnable refresh = new Runnable() {
        @Override public void run() {
            if (!visible) return;
            worker.execute(() -> {
                try { String s = BridgeInputMethod.request(MainActivity.this, "state").getString("state"); runOnUiThread(() -> { if (visible) keyboardStatus.setText(modeLabel(s)); }); }
                catch (Exception e) { runOnUiThread(() -> { if (visible) keyboardStatus.setText(connectionError(e)); }); }
                main.postDelayed(this, 1500);
            });
        }
    };
    @Override protected void onResume() { super.onResume(); visible = true; main.post(refresh); }
    @Override protected void onPause() { visible = false; main.removeCallbacks(refresh); super.onPause(); }

    private void saveCapture(String secret) throws Exception {
        HttpURLConnection connection = (HttpURLConnection)new URL("http://127.0.0.1:38473/capture").openConnection();
        connection.setRequestMethod("POST"); connection.setConnectTimeout(5000); connection.setReadTimeout(30000);
        connection.setRequestProperty("X-ScreenKey-Token", secret);
        Uri item = null;
        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new Exception(code == 401 ? "pairing key rejected" : "PC returned HTTP " + code);
            if (!"image/png".equalsIgnoreCase(connection.getContentType())) throw new Exception("unexpected response from PC");
            ContentValues values = new ContentValues();
            values.put(MediaStore.Images.Media.DISPLAY_NAME, "ScreenKey_" + new SimpleDateFormat("yyyyMMdd_HHmmss_SSS", Locale.US).format(new Date()) + ".png");
            values.put(MediaStore.Images.Media.MIME_TYPE, "image/png");
            values.put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/ScreenKey Bridge");
            values.put(MediaStore.Images.Media.IS_PENDING, 1);
            item = getContentResolver().insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values);
            if (item == null) throw new Exception("could not create gallery image");
            try (InputStream in = connection.getInputStream(); OutputStream out = getContentResolver().openOutputStream(item)) {
                if (out == null) throw new Exception("could not write gallery image");
                byte[] buffer = new byte[32768]; int n; long total = 0;
                while ((n = in.read(buffer)) != -1) { total += n; if (total > 100L * 1024 * 1024) throw new Exception("image too large"); out.write(buffer, 0, n); }
                if (total == 0 || (connection.getContentLengthLong() >= 0 && total != connection.getContentLengthLong())) throw new Exception("incomplete image");
            }
            ContentValues publish = new ContentValues(); publish.put(MediaStore.Images.Media.IS_PENDING, 0);
            if (getContentResolver().update(item, publish, null, null) != 1) throw new Exception("could not publish gallery image");
            item = null;
        } finally {
            if (item != null) getContentResolver().delete(item, null, null);
            connection.disconnect();
        }
    }
    @Override protected void onDestroy() { visible = false; main.removeCallbacks(refresh); worker.shutdown(); super.onDestroy(); }
}
