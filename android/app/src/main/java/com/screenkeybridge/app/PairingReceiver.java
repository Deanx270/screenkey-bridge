package com.screenkeybridge.app;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Only the authorized ADB shell/system can send this explicit pairing request. */
public final class PairingReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null || !"com.screenkeybridge.action.PAIR".equals(intent.getAction())) { setResultCode(0); return; }
        String key = intent.getStringExtra("key");
        if (key == null || !key.matches("[0-9A-F]{64}")) { setResultCode(0); return; }
        boolean saved = context.getSharedPreferences("MainActivity", Context.MODE_PRIVATE).edit().putString("key", key).commit();
        setResultCode(saved ? 1 : 0);
        setResultData(saved ? "paired" : "not saved");
    }
}
