package com.screenkeybridge.app;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.Locale;

/** A small local keyboard; touch input goes directly to the focused Android editor. */
final class TouchKeyboard extends LinearLayout {
    interface ConnectionProvider { InputConnection get(); }
    interface EditorProvider { EditorInfo get(); }
    private final ConnectionProvider connection;
    private final EditorProvider editor;
    private final Runnable paste;
    private boolean symbols;
    private boolean shifted;
    private final int keyHeight;

    TouchKeyboard(Context context, ConnectionProvider connection, EditorProvider editor, Runnable paste) {
        super(context);
        this.connection = connection; this.editor = editor; this.paste = paste;
        keyHeight = (int)(46 * getResources().getDisplayMetrics().density);
        setOrientation(VERTICAL);
        rebuild();
    }

    void setEditor(EditorInfo info) {
        int type = info == null ? 0 : info.inputType & InputType.TYPE_MASK_CLASS;
        symbols = type == InputType.TYPE_CLASS_NUMBER || type == InputType.TYPE_CLASS_PHONE || type == InputType.TYPE_CLASS_DATETIME;
        shifted = false; rebuild();
    }

    private void rebuild() {
        removeAllViews();
        addRow("1234567890", false);
        if (symbols) {
            addRow("@#$%&-+()", true);
            addRow("*/\"':;!?", true);
        } else {
            addRow("qwertyuiop", false);
            addRow("asdfghjkl", true);
        }
        LinearLayout third = row();
        if (symbols) addKey(third, "ABC", "Switch to letters", "mode", 1.5f, () -> { symbols = false; shifted = false; rebuild(); });
        else addKey(third, shifted ? "⇧●" : "⇧", "Shift", "shift", 1.5f, () -> { shifted = !shifted; rebuild(); });
        String chars = symbols ? "[]=<>_\\" : "zxcvbnm";
        for (int i = 0; i < chars.length(); i++) addCharacter(third, String.valueOf(chars.charAt(i)));
        addKey(third, "⌫", "Backspace", "backspace", 1.5f, this::backspace);
        addView(third);
        LinearLayout bottom = row();
        addKey(bottom, symbols ? "ABC" : "?123", symbols ? "Switch to letters" : "Switch to symbols", "mode_bottom", 1.4f, () -> { symbols = !symbols; shifted = false; rebuild(); });
        addCharacter(bottom, ",");
        addKey(bottom, "space", "Space", "space", 3.3f, () -> commit(" "));
        addCharacter(bottom, ".");
        addKey(bottom, "Paste", "Paste from phone clipboard", "paste", 1.2f, paste);
        addKey(bottom, "↵", "Enter", "enter", 1.4f, this::enter);
        addView(bottom);
    }

    private void addRow(String characters, boolean inset) {
        LinearLayout line = row();
        if (inset) spacer(line, .5f);
        for (int i = 0; i < characters.length(); i++) addCharacter(line, String.valueOf(characters.charAt(i)));
        if (inset) spacer(line, .5f);
        addView(line);
    }
    private LinearLayout row() { LinearLayout line = new LinearLayout(getContext()); line.setOrientation(HORIZONTAL); return line; }
    private void spacer(LinearLayout row, float weight) { row.addView(new View(getContext()), new LayoutParams(0, keyHeight, weight)); }
    private void addCharacter(LinearLayout row, String value) {
        String shown = shifted && !symbols ? value.toUpperCase(Locale.ROOT) : value;
        addKey(row, shown, shown, "char_" + value, 1, () -> {
            commit(shown);
            if (shifted && !symbols) { shifted = false; rebuild(); }
        });
    }
    private void addKey(LinearLayout row, String label, String description, String tag, float weight, Runnable action) {
        TextView key = new TextView(getContext()); key.setText(label); key.setTextColor(Color.rgb(28, 32, 38));
        key.setTextSize(label.length() > 3 ? 13 : 19); key.setTypeface(Typeface.DEFAULT, Typeface.NORMAL);
        key.setGravity(Gravity.CENTER); key.setContentDescription(description); key.setTag(tag); key.setFocusable(true); key.setClickable(true);
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.WHITE); bg.setCornerRadius(6 * getResources().getDisplayMetrics().density); key.setBackground(bg);
        LayoutParams params = new LayoutParams(0, keyHeight, weight);
        int gap = Math.max(1, (int)(2 * getResources().getDisplayMetrics().density)); params.setMargins(gap, gap, gap, gap);
        key.setOnClickListener(v -> action.run()); row.addView(key, params);
    }
    private void commit(String text) { InputConnection c = connection.get(); if (c != null) c.commitText(text, 1); }
    private void backspace() {
        InputConnection c = connection.get(); if (c == null) return;
        c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL)); c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL));
    }
    private void enter() {
        InputConnection c = connection.get(); if (c == null) return;
        EditorInfo info = editor.get();
        int action = info == null ? EditorInfo.IME_ACTION_NONE : info.imeOptions & EditorInfo.IME_MASK_ACTION;
        if (info != null && (info.imeOptions & EditorInfo.IME_FLAG_NO_ENTER_ACTION) == 0 && action != EditorInfo.IME_ACTION_NONE && action != EditorInfo.IME_ACTION_UNSPECIFIED) {
            c.performEditorAction(action);
        } else {
            c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER)); c.sendKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER));
        }
    }
}
