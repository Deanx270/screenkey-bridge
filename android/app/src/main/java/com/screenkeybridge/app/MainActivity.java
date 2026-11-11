package com.screenkeybridge.app;

import android.app.Activity;
import android.app.StatusBarManager;
import android.Manifest;
import android.content.ComponentName;
import android.content.Intent;
import android.content.Context;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.graphics.drawable.Icon;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;
import android.view.inputmethod.InputMethodManager;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private EditText key;
    private TextView status;
    private Button capture;
    private TextView keyboardStatus;
    private Spinner displayPicker;
    private boolean updatingDisplays;
    private boolean displaySyncPending = true;
    private boolean displaySyncInFlight;
    private final Handler main = new Handler(Looper.getMainLooper());
    private boolean visible;
    private final SharedPreferences.OnSharedPreferenceChangeListener pairingListener = (prefs, name) -> {
        if (!"key".equals(name) || key == null) return;
        String value = prefs.getString("key", "");
        if (value != null && !value.equals(key.getText().toString())) {
            key.setText(value); displaySyncPending = true;
            if (visible) { loadDisplays(); syncDisplay(getPreferences(MODE_PRIVATE).getString("display", "")); }
        }
    };

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        int pad = (int)(24 * getResources().getDisplayMetrics().density);
        layout.setPadding(pad, pad, pad, pad);
        layout.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = new TextView(this);
        title.setText(getPackageName().endsWith(".preview") ? "ScreenKey Bridge Preview" : "ScreenKey Bridge"); title.setTextSize(26);
        layout.addView(title);
        TextView instructions = new TextView(this);
        instructions.setText("On Windows, select Connect and pair a phone from the tray menu, then choose this phone. Keep ADB connected. The key is transferred automatically.");
        layout.addView(instructions);
        key = new EditText(this);
        key.setSingleLine(true); key.setHint("Windows pairing key");
        key.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        key.setText(getPreferences(MODE_PRIVATE).getString("key", ""));
        getPreferences(MODE_PRIVATE).registerOnSharedPreferenceChangeListener(pairingListener);
        layout.addView(key);
        TextView displayLabel = new TextView(this); displayLabel.setText("Screenshot display"); layout.addView(displayLabel);
        displayPicker = new Spinner(this); layout.addView(displayPicker);
        List<DisplayChoice> initial = new ArrayList<>();
        initial.add(new DisplayChoice("", "All displays"));
        String savedDisplay = getPreferences(MODE_PRIVATE).getString("display", "");
        if (savedDisplay != null && !savedDisplay.isEmpty()) initial.add(new DisplayChoice(savedDisplay, "Saved display (checking availability)"));
        setDisplays(initial);
        displayPicker.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (!updatingDisplays) {
                    String idValue = ((DisplayChoice)parent.getItemAtPosition(position)).id;
                    getPreferences(MODE_PRIVATE).edit().putString("display", idValue).apply();
                    displaySyncPending = true;
                    syncDisplay(idValue);
                }
            }
            @Override public void onNothingSelected(AdapterView<?> parent) {}
        });
        Button refreshDisplays = new Button(this); refreshDisplays.setText("Refresh displays"); refreshDisplays.setOnClickListener(v -> loadDisplays()); layout.addView(refreshDisplays);
        Button addTile = new Button(this); addTile.setText("Add Quick Settings capture tile");
        addTile.setOnClickListener(v -> {
            if (Build.VERSION.SDK_INT >= 33) {
                StatusBarManager bar = getSystemService(StatusBarManager.class);
                String tileName = getPackageName().endsWith(".preview") ? "Capture PC (Preview)" : "Capture PC screen";
                bar.requestAddTileService(new ComponentName(this, CaptureTile.class), tileName, Icon.createWithResource(this, R.drawable.ic_capture), getMainExecutor(),
                    result -> Toast.makeText(this, result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ADDED || result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED ? "Capture tile is available in Quick Settings." : "You can add Capture PC screen from Quick Settings edit.", Toast.LENGTH_LONG).show());
            } else Toast.makeText(this, "Open Quick Settings, tap Edit, and add Capture PC screen.", Toast.LENGTH_LONG).show();
        }); layout.addView(addTile);
        Button notifications = new Button(this); notifications.setText("Enable capture notifications");
        notifications.setOnClickListener(v -> {
            if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 19);
            else { Intent settings = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS); settings.putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName()); startActivity(settings); }
        }); layout.addView(notifications);
        capture = new Button(this); capture.setText("Capture PC screen");
        layout.addView(capture);
        status = new TextView(this); status.setText("Ready"); layout.addView(status);
        TextView pageLabel = new TextView(this); pageLabel.setText("Save the current Edge page"); layout.addView(pageLabel);
        Spinner pageFormat = new Spinner(this);
        ArrayAdapter<String> pageFormats = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, new String[]{"HTML", "PDF"});
        pageFormats.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item); pageFormat.setAdapter(pageFormats);
        pageFormat.setSelection(getPreferences(MODE_PRIVATE).getInt("page_format", 0) == 1 ? 1 : 0); layout.addView(pageFormat);
        Button savePage = new Button(this); savePage.setText("Save current Edge page to phone"); layout.addView(savePage);
        savePage.setOnClickListener(v -> {
            String secret = key.getText().toString().trim();
            if (!secret.matches("[0-9A-F]{64}")) { status.setText("Connect and pair this phone from the Windows tray menu first."); return; }
            boolean pdf = pageFormat.getSelectedItemPosition() == 1;
            getPreferences(MODE_PRIVATE).edit().putInt("page_format", pdf ? 1 : 0).apply();
            savePage.setEnabled(false); status.setText("Saving page from Edge…");
            worker.execute(() -> {
                try {
                    String html = PageSaver.fetch(secret);
                    if (!pdf) { PageSaver.html(this, html); runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) { status.setText("Saved HTML to Downloads/ScreenKey Bridge."); savePage.setEnabled(true); } }); }
                    else runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) PageSaver.pdf(this, html, message -> { if (!isFinishing() && !isDestroyed()) { status.setText(message); savePage.setEnabled(true); } }); });
                } catch (Exception e) { runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) { status.setText("Page save failed: " + e.getMessage()); savePage.setEnabled(true); } }); }
            });
        });
        Button enable = new Button(this); enable.setText("Enable ScreenKey keyboard");
        enable.setOnClickListener(v -> startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))); layout.addView(enable);
        Button choose = new Button(this); choose.setText("Choose ScreenKey keyboard");
        choose.setOnClickListener(v -> ((InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE)).showInputMethodPicker()); layout.addView(choose);
        Button toggle = new Button(this); toggle.setText("Toggle PC / phone keyboard");
        toggle.setOnClickListener(v -> {
            String secret = key.getText().toString().trim();
            if (!secret.matches("[0-9A-F]{64}")) { status.setText("Connect and pair this phone from the Windows tray menu first."); return; }
            getPreferences(MODE_PRIVATE).edit().putString("key", secret).apply();
            worker.execute(() -> { try { String s = BridgeInputMethod.request(this, "toggle").getString("state"); runOnUiThread(() -> keyboardStatus.setText(modeLabel(s))); }
                catch (Exception e) { runOnUiThread(() -> keyboardStatus.setText(connectionError(e))); } });
        }); layout.addView(toggle);
        keyboardStatus = new TextView(this); keyboardStatus.setText("Keyboard: checking"); layout.addView(keyboardStatus);
        capture.setOnClickListener(v -> {
            String secret = key.getText().toString().trim();
            if (!secret.matches("[0-9A-F]{64}")) { status.setText("Connect and pair this phone from the Windows tray menu first."); return; }
            getPreferences(MODE_PRIVATE).edit().putString("key", secret).apply();
            DisplayChoice selected = (DisplayChoice)displayPicker.getSelectedItem();
            String display = selected == null ? "" : selected.id;
            capture.setEnabled(false); status.setText("Capturing…");
            worker.execute(() -> {
                String result;
                try { result = CaptureStorage.message(CaptureStorage.capture(this, secret, display, null)); }
                catch (Exception e) { result = "Capture failed: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
                String message = result;
                runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) { status.setText(message); capture.setEnabled(true); } });
            });
        });
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); scroll.addView(layout); setContentView(scroll);
        handlePcPdf(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); handlePcPdf(intent); }
    private void handlePcPdf(Intent intent) {
        if (intent == null || !"com.screenkeybridge.action.PRINT_PAGE".equals(intent.getAction())) return;
        String id = intent.getStringExtra("request_id"); intent.setAction(null); intent.removeExtra("request_id");
        if (id == null || !id.matches("[A-F0-9]{32}")) { status.setText("Invalid page request."); return; }
        String secret = getPreferences(MODE_PRIVATE).getString("key", "");
        if (secret == null || !secret.matches("[A-F0-9]{64}")) { status.setText("Pair the phone with the PC first."); return; }
        status.setText("Preparing PDF from the PC…");
        worker.execute(() -> {
            try {
                String html = PageSaver.fetch(secret, id);
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    java.util.concurrent.atomic.AtomicBoolean reported = new java.util.concurrent.atomic.AtomicBoolean();
                    PageSaver.pdf(this, html, message -> {
                        if (isFinishing() || isDestroyed()) return;
                        status.setText(message);
                        if (reported.compareAndSet(false, true)) { try { worker.execute(() -> PageSaver.acknowledge(secret, id, message.startsWith("Choose Save as PDF"))); } catch (java.util.concurrent.RejectedExecutionException ignored) { } }
                    });
                });
            } catch (Exception e) {
                PageSaver.acknowledge(secret, id, false);
                runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) status.setText("Page save failed: " + e.getMessage()); });
            }
        });
    }

    private static final class DisplayChoice {
        final String id, label;
        DisplayChoice(String id, String label) { this.id = id; this.label = label; }
        @Override public String toString() { return label; }
    }
    private void setDisplays(List<DisplayChoice> choices) {
        String saved = getPreferences(MODE_PRIVATE).getString("display", "");
        int selected = 0;
        for (int i = 0; i < choices.size(); i++) if (choices.get(i).id.equals(saved)) selected = i;
        updatingDisplays = true;
        ArrayAdapter<DisplayChoice> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, choices);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        displayPicker.setAdapter(adapter); displayPicker.setSelection(selected);
        updatingDisplays = false;
    }
    private void loadDisplays() {
        String secret = key.getText().toString().trim();
        if (!secret.matches("[0-9A-F]{64}")) return;
        worker.execute(() -> {
            try {
                JSONObject response = requestDisplays(secret);
                JSONArray entries = response.getJSONArray("displays");
                List<DisplayChoice> choices = new ArrayList<>(); choices.add(new DisplayChoice("", "All displays"));
                for (int i = 0; i < entries.length(); i++) {
                    JSONObject entry = entries.getJSONObject(i);
                    choices.add(new DisplayChoice(entry.getString("id"), entry.getString("label")));
                }
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    String saved = getPreferences(MODE_PRIVATE).getString("display", "");
                    boolean found = false; for (DisplayChoice choice : choices) if (choice.id.equals(saved)) found = true;
                    if (!found && saved != null && !saved.isEmpty()) choices.add(new DisplayChoice(saved, "Previously selected display (unavailable)"));
                    setDisplays(choices);
                });
            } catch (Exception e) { runOnUiThread(() -> { if (!isFinishing() && !isDestroyed()) status.setText("Display list unavailable. Check the PC connection or refresh displays."); }); }
        });
    }
    private void syncDisplay(String display) {
        String secret = key.getText().toString().trim();
        if (!displaySyncPending || displaySyncInFlight || !secret.matches("[0-9A-F]{64}")) return;
        displaySyncInFlight = true;
        worker.execute(() -> {
            HttpURLConnection c = null;
            boolean success = false;
            try {
                String url = "http://127.0.0.1:38473/capture/preference?display=" + URLEncoder.encode(display, "UTF-8");
                c = (HttpURLConnection)new URL(url).openConnection(); c.setRequestMethod("POST"); c.setConnectTimeout(3000); c.setReadTimeout(3000);
                c.setRequestProperty("X-ScreenKey-Token", secret);
                if (c.getResponseCode() != 200) throw new Exception("Display selection not saved on PC");
                success = true;
            } catch (Exception ignored) { }
            finally { if (c != null) c.disconnect(); }
            boolean synced = success;
            runOnUiThread(() -> {
                displaySyncInFlight = false;
                if (isFinishing() || isDestroyed()) return;
                String current = getPreferences(MODE_PRIVATE).getString("display", "");
                displaySyncPending = !synced || !display.equals(current);
                if (!synced) status.setText("Display selection will sync when the PC reconnects.");
                else if (displaySyncPending) syncDisplay(current);
            });
        });
    }
    private JSONObject requestDisplays(String secret) throws Exception {
        HttpURLConnection c = (HttpURLConnection)new URL("http://127.0.0.1:38473/displays").openConnection();
        c.setRequestMethod("POST"); c.setConnectTimeout(3000); c.setReadTimeout(3000); c.setRequestProperty("X-ScreenKey-Token", secret);
        try {
            if (c.getResponseCode() != 200) throw new Exception("Could not retrieve displays");
            try (InputStream in = c.getInputStream(); java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream()) {
                byte[] b = new byte[1024]; int n; while ((n = in.read(b)) != -1) { if (out.size() + n > 32768) throw new Exception("Display list too large"); out.write(b, 0, n); }
                return new JSONObject(new String(out.toByteArray(), StandardCharsets.UTF_8));
            }
        } finally { c.disconnect(); }
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
                try { String s = BridgeInputMethod.request(MainActivity.this, "state").getString("state"); runOnUiThread(() -> { if (visible) { keyboardStatus.setText(modeLabel(s)); if (displaySyncPending) syncDisplay(getPreferences(MODE_PRIVATE).getString("display", "")); } }); }
                catch (Exception e) { runOnUiThread(() -> { if (visible) keyboardStatus.setText(connectionError(e)); }); }
                main.postDelayed(this, 1500);
            });
        }
    };
    @Override protected void onResume() {
        super.onResume(); visible = true;
        String savedKey = getPreferences(MODE_PRIVATE).getString("key", "");
        if (savedKey != null && !savedKey.equals(key.getText().toString())) key.setText(savedKey);
        android.content.SharedPreferences captureState = getSharedPreferences("capture_status", MODE_PRIVATE);
        if (System.currentTimeMillis() - captureState.getLong("time", 0) < 60000) status.setText(captureState.getString("message", "Ready"));
        main.post(refresh); loadDisplays(); syncDisplay(getPreferences(MODE_PRIVATE).getString("display", ""));
    }
    @Override protected void onPause() { visible = false; main.removeCallbacks(refresh); super.onPause(); }

    @Override protected void onDestroy() { visible = false; getPreferences(MODE_PRIVATE).unregisterOnSharedPreferenceChangeListener(pairingListener); main.removeCallbacks(refresh); worker.shutdown(); super.onDestroy(); }
}
