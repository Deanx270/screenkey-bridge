package com.screenkeybridge.app;

import android.inputmethodservice.InputMethodService;
import android.os.Handler;
import android.os.Looper;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;
import android.content.Context;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONObject;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class BridgeInputMethod extends InputMethodService {
    private final ExecutorService network = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile int generation;
    private volatile boolean ready;
    private TextView status;
    private final Runnable poll = new Runnable() {
        @Override public void run() {
            final int current = generation;
            if (!ready) return;
            network.execute(() -> {
                try {
                    JSONObject reply = request("poll");
                    JSONArray events = reply.getJSONArray("events");
                    String state = reply.getString("state");
                    main.post(() -> {
                        if (!ready || current != generation) return;
                        if (status != null) status.setText("Keyboard: " + state + "  •  Ctrl+Alt+F12 returns to PC");
                        InputConnection connection = getCurrentInputConnection();
                        if (connection == null) { ready = false; generation++; return; }
                        for (int i = 0; i < events.length(); i++) {
                            String event = events.optString(i);
                            if (!deliver(connection, event)) { ready = false; generation++; break; }
                        }
                    });
                } catch (Exception e) {
                    main.post(() -> { if (current == generation && status != null) status.setText("PC disconnected; typing returns to PC."); });
                }
                main.postDelayed(() -> { if (ready && current == generation) main.post(poll); }, 350);
            });
        }
    };

    @Override public View onCreateInputView() {
        LinearLayout layout = new LinearLayout(this); layout.setOrientation(LinearLayout.VERTICAL);
        int pad = (int)(12 * getResources().getDisplayMetrics().density); layout.setPadding(pad, pad, pad, pad);
        status = new TextView(this); status.setText("ScreenKey Bridge keyboard"); layout.addView(status);
        Button toggle = new Button(this); toggle.setText("Toggle PC / phone keyboard");
        toggle.setOnClickListener(v -> network.execute(() -> {
            try { request("toggle"); } catch (Exception ignored) { main.post(() -> status.setText("Could not reach the PC.")); }
        })); layout.addView(toggle);
        Button change = new Button(this); change.setText("Choose another keyboard");
        change.setOnClickListener(v -> ((InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE)).showInputMethodPicker());
        layout.addView(change); return layout;
    }

    @Override public void onStartInput(EditorInfo info, boolean restarting) {
        super.onStartInput(info, restarting);
        generation++; ready = getCurrentInputConnection() != null && !getPackageName().equals(info.packageName);
        if (ready) main.post(poll);
    }
    @Override public void onFinishInput() {
        ready = false; generation++;
        network.execute(() -> { try { request("unready"); } catch (Exception ignored) {} });
        super.onFinishInput();
    }
    @Override public void onDestroy() { ready = false; generation++; network.shutdown(); super.onDestroy(); }

    static JSONObject request(Context context, String route) throws Exception {
        String token = context.getSharedPreferences("MainActivity", MODE_PRIVATE).getString("key", "");
        if (token == null || !token.matches("[0-9A-F]{64}")) throw new Exception("Set the pairing key in ScreenKey Bridge first.");
        HttpURLConnection c = (HttpURLConnection)new URL("http://127.0.0.1:38473/keyboard/" + route).openConnection();
        c.setRequestMethod("POST"); c.setConnectTimeout(1000); c.setReadTimeout(1000);
        c.setRequestProperty("X-ScreenKey-Token", token);
        try {
            int code = c.getResponseCode();
            if (code != 200) throw new Exception(code == 401 ? "Pairing key rejected by PC" : "PC returned HTTP " + code);
            try (java.io.InputStream in = c.getInputStream(); java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream()) {
                byte[] b = new byte[1024]; int n; while ((n = in.read(b)) != -1) { if (out.size() + n > 32768) throw new Exception("Response too large"); out.write(b, 0, n); }
                return new JSONObject(new String(out.toByteArray(), StandardCharsets.UTF_8));
            }
        } finally { c.disconnect(); }
    }
    private JSONObject request(String route) throws Exception { return request(this, route); }

    private static boolean deliver(InputConnection c, String text) {
        switch (text) {
            case "<backspace>": return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL));
            case "<enter>": return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER));
            case "<left>": return arrow(c, KeyEvent.KEYCODE_DPAD_LEFT);
            case "<right>": return arrow(c, KeyEvent.KEYCODE_DPAD_RIGHT);
            case "<up>": return arrow(c, KeyEvent.KEYCODE_DPAD_UP);
            case "<down>": return arrow(c, KeyEvent.KEYCODE_DPAD_DOWN);
            default: return text.length() == 1 && c.commitText(text, 1);
        }
    }
    private static boolean arrow(InputConnection c, int key) { return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, key)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, key)); }
}
