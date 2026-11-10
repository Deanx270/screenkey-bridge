package com.screenkeybridge.app;

import android.inputmethodservice.InputMethodService;
import android.os.Handler;
import android.os.Looper;
import android.view.KeyEvent;
import android.view.KeyCharacterMap;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;
import android.content.Context;
import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.net.Uri;
import android.view.inputmethod.InputContentInfo;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONObject;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.BreakIterator;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class BridgeInputMethod extends InputMethodService {
    private final ExecutorService network = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile int generation;
    private volatile boolean ready;
    private TextView status;
    private long noticeUntil;
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
                        if (status != null && android.os.SystemClock.uptimeMillis() >= noticeUntil) status.setText("Keyboard: " + state + "  •  Ctrl+Alt+F12 returns to PC");
                        InputConnection connection = getCurrentInputConnection();
                        if (connection == null) { ready = false; generation++; return; }
                        for (int i = 0; i < events.length(); i++) {
                            String event = events.optString(i);
                            if ("<unsupported>".equals(event)) { showNotice("That key or shortcut is not supported yet."); continue; }
                            boolean delivered = "<ctrl:v>".equals(event) ? paste(BridgeInputMethod.this, connection, getCurrentInputEditorInfo()) : deliver(connection, event);
                            if (!delivered) {
                                if (event.startsWith("<ctrl:") || event.startsWith("<shift:")) {
                                    showNotice("This phone app did not accept that shortcut.");
                                    continue;
                                }
                                ready = false; generation++; break;
                            }
                        }
                    });
                } catch (Exception e) {
                    main.post(() -> { if (current == generation && status != null) status.setText("PC disconnected; typing returns to PC."); });
                }
                main.postDelayed(() -> { if (ready && current == generation) main.post(poll); }, 350);
            });
        }
    };

    private void showNotice(String text) { noticeUntil = android.os.SystemClock.uptimeMillis() + 2500; if (status != null) status.setText(text); }

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
            case "<ctrl:a>": return c.performContextMenuAction(android.R.id.selectAll);
            case "<ctrl:c>": return c.performContextMenuAction(android.R.id.copy);
            case "<ctrl:x>": return c.performContextMenuAction(android.R.id.cut);
            case "<ctrl:v>": return c.performContextMenuAction(android.R.id.paste);
            case "<ctrl:backspace>": return deletePreviousWord(c);
            case "<ctrl:z>": return modified(c, KeyEvent.KEYCODE_Z, KeyEvent.META_CTRL_ON);
            case "<ctrl:shift:z>": return modified(c, KeyEvent.KEYCODE_Z, KeyEvent.META_CTRL_ON | KeyEvent.META_SHIFT_ON);
            case "<ctrl:y>": return modified(c, KeyEvent.KEYCODE_Y, KeyEvent.META_CTRL_ON);
            case "<ctrl:delete>": return modified(c, KeyEvent.KEYCODE_FORWARD_DEL, KeyEvent.META_CTRL_ON);
            case "<backspace>": return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL));
            case "<enter>": return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER));
            case "<delete>": return arrow(c, KeyEvent.KEYCODE_FORWARD_DEL);
            case "<tab>": return arrow(c, KeyEvent.KEYCODE_TAB);
            case "<home>": return arrow(c, KeyEvent.KEYCODE_MOVE_HOME);
            case "<end>": return arrow(c, KeyEvent.KEYCODE_MOVE_END);
            case "<left>": return arrow(c, KeyEvent.KEYCODE_DPAD_LEFT);
            case "<right>": return arrow(c, KeyEvent.KEYCODE_DPAD_RIGHT);
            case "<up>": return arrow(c, KeyEvent.KEYCODE_DPAD_UP);
            case "<down>": return arrow(c, KeyEvent.KEYCODE_DPAD_DOWN);
            default:
                if (text.startsWith("<shift:") || text.startsWith("<ctrl:")) return deliverModifiedNavigation(c, text);
                return text.length() == 1 && c.commitText(text, 1);
        }
    }
    static boolean paste(Context context, InputConnection connection, EditorInfo editor) {
        try {
            ClipboardManager clipboard = (ClipboardManager)context.getSystemService(Context.CLIPBOARD_SERVICE);
            ClipData clip = clipboard == null ? null : clipboard.getPrimaryClip();
            if (clip != null && clip.getItemCount() > 0 && clip.getDescription().hasMimeType("image/png") && editor != null && editor.contentMimeTypes != null) {
                Uri uri = clip.getItemAt(0).getUri();
                String last = context.getSharedPreferences("capture_status", MODE_PRIVATE).getString("last_image_uri", "");
                boolean supportsImage = false;
                for (String mime : editor.contentMimeTypes) if (mime != null && ClipDescription.compareMimeTypes("image/png", mime)) supportsImage = true;
                if (uri != null && uri.toString().equals(last) && supportsImage) {
                    InputContentInfo content = new InputContentInfo(uri, new ClipDescription("PC screenshot", new String[]{"image/png"}), null);
                    connection.finishComposingText();
                    if (connection.commitContent(content, InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION, null)) return true;
                }
            }
        } catch (RuntimeException ignored) { }
        return connection.performContextMenuAction(android.R.id.paste);
    }
    private static boolean deletePreviousWord(InputConnection c) {
        CharSequence selection = c.getSelectedText(0);
        if (selection != null && selection.length() > 0) return c.commitText("", 1);
        CharSequence before = c.getTextBeforeCursor(4096, 0);
        if (before == null) return modified(c, KeyEvent.KEYCODE_DEL, KeyEvent.META_CTRL_ON);
        String text = before.toString();
        int end = text.length();
        while (end > 0) {
            int cp = text.codePointBefore(end);
            if (!Character.isWhitespace(cp)) break;
            end -= Character.charCount(cp);
        }
        if (end == 0) return text.isEmpty() || c.deleteSurroundingText(text.length(), 0);
        BreakIterator words = BreakIterator.getWordInstance(Locale.getDefault());
        words.setText(text);
        int start = words.preceding(end);
        if (start == BreakIterator.DONE) start = 0;
        return c.deleteSurroundingText(text.length() - start, 0);
    }
    private static boolean deliverModifiedNavigation(InputConnection c, String event) {
        boolean ctrl = event.startsWith("<ctrl:");
        boolean shift = event.startsWith("<shift:") || event.startsWith("<ctrl:shift:");
        int start = event.lastIndexOf(':') + 1;
        if (!event.endsWith(">") || start < 1) return true;
        String name = event.substring(start, event.length() - 1);
        int key;
        switch (name) {
            case "left": key = KeyEvent.KEYCODE_DPAD_LEFT; break;
            case "right": key = KeyEvent.KEYCODE_DPAD_RIGHT; break;
            case "up": key = KeyEvent.KEYCODE_DPAD_UP; break;
            case "down": key = KeyEvent.KEYCODE_DPAD_DOWN; break;
            case "home": key = KeyEvent.KEYCODE_MOVE_HOME; break;
            case "end": key = KeyEvent.KEYCODE_MOVE_END; break;
            case "tab": key = KeyEvent.KEYCODE_TAB; break;
            default: return true;
        }
        return modified(c, key, (ctrl ? KeyEvent.META_CTRL_ON : 0) | (shift ? KeyEvent.META_SHIFT_ON : 0));
    }
    private static boolean modified(InputConnection c, int key, int meta) {
        long now = android.os.SystemClock.uptimeMillis();
        KeyEvent down = new KeyEvent(now, now, KeyEvent.ACTION_DOWN, key, 0, meta, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, KeyEvent.FLAG_SOFT_KEYBOARD);
        KeyEvent up = new KeyEvent(now, now, KeyEvent.ACTION_UP, key, 0, meta, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, KeyEvent.FLAG_SOFT_KEYBOARD);
        return c.sendKeyEvent(down) && c.sendKeyEvent(up);
    }
    private static boolean arrow(InputConnection c, int key) { return c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, key)) && c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, key)); }
}
