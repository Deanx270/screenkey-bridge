package com.screenkeybridge.app;

import android.content.Intent;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;
import android.widget.Toast;

public final class CaptureTile extends TileService {
    @Override public void onStartListening() {
        super.onStartListening();
        Tile tile = getQsTile();
        if (tile != null) {
            long time = getSharedPreferences("capture_status", MODE_PRIVATE).getLong("time", 0);
            String message = System.currentTimeMillis() - time < 15000 ? getSharedPreferences("capture_status", MODE_PRIVATE).getString("tile", "") : "";
            tile.setSubtitle(message); tile.setState(Tile.STATE_INACTIVE); tile.updateTile();
        }
    }
    @Override public void onClick() {
        super.onClick();
        getSharedPreferences("capture_status", MODE_PRIVATE).edit().putString("tile", "Capturing…").putLong("time", System.currentTimeMillis()).apply();
        Tile tile = getQsTile(); if (tile != null) { tile.setSubtitle("Capturing…"); tile.updateTile(); }
        try { startForegroundService(new Intent(this, CaptureService.class)); }
        catch (RuntimeException e) {
            getSharedPreferences("capture_status", MODE_PRIVATE).edit().putString("tile", "Could not start capture").putLong("time", System.currentTimeMillis()).apply();
            if (tile != null) { tile.setSubtitle("Could not start capture"); tile.updateTile(); }
            Toast.makeText(this, "Could not start capture. Open ScreenKey Bridge and try again.", Toast.LENGTH_LONG).show();
        }
    }
}
