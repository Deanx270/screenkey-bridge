package com.screenkeybridge.app;

import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

final class CaptureStorage {
    private CaptureStorage() {}

    static void capture(Context context, String secret, String display, String requestId) throws Exception {
        String url = requestId == null
            ? "http://127.0.0.1:38473/capture" + (display.isEmpty() ? "" : "?display=" + URLEncoder.encode(display, "UTF-8"))
            : "http://127.0.0.1:38473/capture/pending?id=" + URLEncoder.encode(requestId, "UTF-8");
        HttpURLConnection connection = (HttpURLConnection)new URL(url).openConnection();
        connection.setRequestMethod("POST"); connection.setConnectTimeout(5000); connection.setReadTimeout(30000);
        connection.setRequestProperty("X-ScreenKey-Token", secret);
        Uri item = null;
        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new Exception(code == 401 ? "pairing key rejected" : code == 400 ? "selected display unavailable; refresh displays" : "PC returned HTTP " + code);
            if (!"image/png".equalsIgnoreCase(connection.getContentType())) throw new Exception("unexpected response from PC");
            ContentValues values = new ContentValues();
            values.put(MediaStore.Images.Media.DISPLAY_NAME, "ScreenKey_" + new SimpleDateFormat("yyyyMMdd_HHmmss_SSS", Locale.US).format(new Date()) + ".png");
            values.put(MediaStore.Images.Media.MIME_TYPE, "image/png");
            values.put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/ScreenKey Bridge");
            values.put(MediaStore.Images.Media.IS_PENDING, 1);
            item = context.getContentResolver().insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values);
            if (item == null) throw new Exception("could not create gallery image");
            try (InputStream in = connection.getInputStream(); OutputStream out = context.getContentResolver().openOutputStream(item)) {
                if (out == null) throw new Exception("could not write gallery image");
                byte[] buffer = new byte[32768]; int n; long total = 0;
                while ((n = in.read(buffer)) != -1) { total += n; if (total > 100L * 1024 * 1024) throw new Exception("image too large"); out.write(buffer, 0, n); }
                if (total == 0 || (connection.getContentLengthLong() >= 0 && total != connection.getContentLengthLong())) throw new Exception("incomplete image");
            }
            ContentValues publish = new ContentValues(); publish.put(MediaStore.Images.Media.IS_PENDING, 0);
            if (context.getContentResolver().update(item, publish, null, null) != 1) throw new Exception("could not publish gallery image");
            item = null;
        } finally {
            if (item != null) context.getContentResolver().delete(item, null, null);
            connection.disconnect();
        }
    }
}
