package com.screenkeybridge.app;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputContentInfo;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;

/** Device-side checks that don't read or change the user's clipboard or text fields. */
public final class ShortcutInstrumentation extends Instrumentation {
    private boolean checkClipboard;
    @Override public void onCreate(Bundle arguments) { super.onCreate(arguments); checkClipboard = "true".equals(arguments.getString("check_clipboard")); start(); }
    @Override public void onStart() {
        Bundle result = new Bundle();
        try {
            Method deliver = BridgeInputMethod.class.getDeclaredMethod("deliver", InputConnection.class, String.class);
            deliver.setAccessible(true);
            final int[] action = {0}, deleted = {-1}, contentFlags = {0};
            final InputContentInfo[] inserted = {null};
            final String[] before = {"hello world"}, selection = {null}, committed = {null};
            List<KeyEvent> keys = new ArrayList<>();
            InputConnection connection = (InputConnection)Proxy.newProxyInstance(InputConnection.class.getClassLoader(), new Class<?>[]{InputConnection.class}, (proxy, method, args) -> {
                switch (method.getName()) {
                    case "performContextMenuAction": action[0] = (Integer)args[0]; return true;
                    case "getSelectedText": return selection[0];
                    case "getTextBeforeCursor": return before[0];
                    case "deleteSurroundingText": deleted[0] = (Integer)args[0]; return true;
                    case "commitText": committed[0] = args[0].toString(); return true;
                    case "sendKeyEvent": keys.add((KeyEvent)args[0]); return true;
                    case "commitContent": inserted[0] = (InputContentInfo)args[0]; contentFlags[0] = (Integer)args[1]; return true;
                    default: return method.getReturnType() == boolean.class ? true : null;
                }
            });
            require((Boolean)deliver.invoke(null, connection, "<ctrl:a>") && action[0] == android.R.id.selectAll, "Ctrl+A");
            require((Boolean)deliver.invoke(null, connection, "<ctrl:v>") && action[0] == android.R.id.paste, "Ctrl+V uses Android paste");
            require((Boolean)deliver.invoke(null, connection, "<ctrl:backspace>") && deleted[0] == 5, "Ctrl+Backspace deletes preceding word");
            before[0] = "hello world  "; deleted[0] = -1;
            require((Boolean)deliver.invoke(null, connection, "<ctrl:backspace>") && deleted[0] == 7, "Ctrl+Backspace deletes trailing spaces and word");
            selection[0] = "selected";
            require((Boolean)deliver.invoke(null, connection, "<ctrl:backspace>") && "".equals(committed[0]), "Ctrl+Backspace deletes selection");
            require((Boolean)deliver.invoke(null, connection, "<ctrl:z>") && keys.size() == 2 && keys.get(0).getKeyCode() == KeyEvent.KEYCODE_Z && keys.get(0).isCtrlPressed(), "Ctrl+Z forwards control modifier");
            if (checkClipboard) {
                Context context = getTargetContext();
                ClipboardManager manager = (ClipboardManager)context.getSystemService(Context.CLIPBOARD_SERVICE);
                ClipData clip = manager.getPrimaryClip();
                String last = context.getSharedPreferences("capture_status", Context.MODE_PRIVATE).getString("last_image_uri", "");
                require(clip != null && clip.getItemCount() > 0 && clip.getDescription().hasMimeType("image/png"), "Screenshot is an image on the clipboard");
                Uri uri = clip.getItemAt(0).getUri();
                require(uri != null && uri.toString().equals(last), "Clipboard matches the latest saved screenshot");
                try (java.io.InputStream in = context.getContentResolver().openInputStream(uri)) {
                    require(in != null && in.read() == 0x89 && in.read() == 0x50 && in.read() == 0x4e && in.read() == 0x47, "Clipboard image can be read as PNG");
                }
                EditorInfo editor = new EditorInfo(); editor.contentMimeTypes = new String[]{"image/*"};
                require(BridgeInputMethod.paste(context, connection, editor) && inserted[0] != null && uri.equals(inserted[0].getContentUri()) && (contentFlags[0] & InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION) != 0, "Image paste uses rich content and grants read access");
                editor.contentMimeTypes = new String[]{"text/plain"}; action[0] = 0;
                require(BridgeInputMethod.paste(context, connection, editor) && action[0] == android.R.id.paste, "Unsupported editors receive normal paste");
            }
            result.putString("result", "Shortcut delivery checks passed");
            finish(Activity.RESULT_OK, result);
        } catch (Throwable e) {
            result.putString("error", e.toString());
            finish(Activity.RESULT_CANCELED, result);
        }
    }
    private static void require(boolean condition, String label) { if (!condition) throw new AssertionError(label); }
}
