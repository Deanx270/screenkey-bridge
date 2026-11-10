package com.screenkeybridge.app;

import android.app.Activity;
import android.app.Instrumentation;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.inputmethod.InputConnection;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;

/** Device-side checks that don't read or change the user's clipboard or text fields. */
public final class ShortcutInstrumentation extends Instrumentation {
    @Override public void onCreate(Bundle arguments) { super.onCreate(arguments); start(); }
    @Override public void onStart() {
        Bundle result = new Bundle();
        try {
            Method deliver = BridgeInputMethod.class.getDeclaredMethod("deliver", InputConnection.class, String.class);
            deliver.setAccessible(true);
            final int[] action = {0}, deleted = {-1};
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
            result.putString("result", "Shortcut delivery checks passed");
            finish(Activity.RESULT_OK, result);
        } catch (Throwable e) {
            result.putString("error", e.toString());
            finish(Activity.RESULT_CANCELED, result);
        }
    }
    private static void require(boolean condition, String label) { if (!condition) throw new AssertionError(label); }
}
