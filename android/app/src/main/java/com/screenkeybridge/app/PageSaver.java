package com.screenkeybridge.app;

import android.app.Activity;
import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.print.PrintAttributes;
import android.print.PrintDocumentAdapter;
import android.print.PrintManager;
import android.print.PageRange;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.MediaStore;
import android.view.ViewGroup;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.function.Consumer;

final class PageSaver {
    private PageSaver() {}
    static String fetch(String secret) throws Exception {
        return fetch(secret, null);
    }
    static String fetch(String secret, String requestId) throws Exception {
        if (requestId != null && !requestId.matches("[A-F0-9]{32}")) throw new Exception("Invalid page request.");
        String route = requestId == null ? "/page/save" : "/page/pending?id=" + URLEncoder.encode(requestId, "UTF-8");
        HttpURLConnection c = (HttpURLConnection)new URL("http://127.0.0.1:38473" + route).openConnection();
        c.setRequestMethod("POST"); c.setConnectTimeout(5000); c.setReadTimeout(45000); c.setRequestProperty("X-ScreenKey-Token", secret);
        try {
            int code = c.getResponseCode();
            if (code == 401) throw new Exception("Pairing key rejected. Pair the phone with this PC again.");
            try (InputStream in = code == 200 ? c.getInputStream() : c.getErrorStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                if (in == null) throw new Exception("The PC did not return a page.");
                byte[] b = new byte[32768]; int n; while ((n = in.read(b)) != -1) { if (out.size() + n > 16 * 1024 * 1024) throw new Exception("The page is too large."); out.write(b, 0, n); }
                JSONObject data = new JSONObject(new String(out.toByteArray(), StandardCharsets.UTF_8));
                if (code != 200) throw new Exception(data.optString("error", "Could not save the page."));
                String html = data.getString("html"); if (html.isEmpty()) throw new Exception("The page was empty."); return html;
            }
        } finally { c.disconnect(); }
    }
    static void acknowledge(String secret, String requestId, boolean success) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection)new URL("http://127.0.0.1:38473/page/ack?id=" + URLEncoder.encode(requestId, "UTF-8") + "&ok=" + (success ? "1" : "0")).openConnection();
            c.setRequestMethod("POST"); c.setConnectTimeout(3000); c.setReadTimeout(3000); c.setRequestProperty("X-ScreenKey-Token", secret); c.getResponseCode();
        } catch (Exception ignored) { } finally { if (c != null) c.disconnect(); }
    }
    private static Uri create(Context context, String extension, String mime) throws Exception {
        ContentValues v = new ContentValues();
        v.put(MediaStore.Downloads.DISPLAY_NAME, "ScreenKey_Page_" + new SimpleDateFormat("yyyyMMdd_HHmmss_SSS", Locale.US).format(new Date()) + extension);
        v.put(MediaStore.Downloads.MIME_TYPE, mime); v.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/ScreenKey Bridge"); v.put(MediaStore.Downloads.IS_PENDING, 1);
        Uri uri = context.getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v);
        if (uri == null) throw new Exception("Could not create the file in Downloads."); return uri;
    }
    private static void publish(Context context, Uri uri) throws Exception {
        ContentValues v = new ContentValues(); v.put(MediaStore.Downloads.IS_PENDING, 0);
        if (context.getContentResolver().update(uri, v, null, null) != 1) throw new Exception("Could not publish the file in Downloads.");
    }
    static void html(Context context, String html) throws Exception {
        Uri uri = create(context, ".html", "text/html"); boolean saved = false;
        try (OutputStream out = context.getContentResolver().openOutputStream(uri)) {
            if (out == null) throw new Exception("Could not write the file.");
            out.write(html.getBytes(StandardCharsets.UTF_8)); out.flush(); publish(context, uri); saved = true;
        } finally { if (!saved) context.getContentResolver().delete(uri, null, null); }
    }
    // Render the sanitized snapshot on the phone, not in the PC's browser.
    static void pdf(Activity activity, String html, Consumer<String> finished) {
        WebView view = new WebView(activity); view.getSettings().setJavaScriptEnabled(false);
        view.getSettings().setAllowFileAccess(false); view.getSettings().setAllowContentAccess(false);
        ViewGroup parent = activity.findViewById(android.R.id.content);
        // WebView's print adapter needs a laid-out, renderable view. Keep it
        // behind the existing phone UI until the system print flow completes.
        parent.addView(view, 0, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        view.setFocusable(false); view.setClickable(false);
        Handler handler = new Handler(Looper.getMainLooper());
        final boolean[] done = {false}, started = {false};
        Consumer<String> finish = message -> {
            if (done[0]) return; done[0] = true;
            parent.removeView(view); view.destroy(); finished.accept(message);
        };
        handler.postDelayed(() -> { if (!started[0]) finish.accept("PDF preparation timed out."); }, 60000);
        view.setWebViewClient(new WebViewClient() {
            @Override public void onPageFinished(WebView ignored, String url) {
                if (started[0] || done[0]) return; started[0] = true;
                try {
                    PrintDocumentAdapter delegate = view.createPrintDocumentAdapter("ScreenKey page");
                    PrintAttributes attrs = new PrintAttributes.Builder().setMediaSize(PrintAttributes.MediaSize.ISO_A4).setResolution(new PrintAttributes.Resolution("screenkey", "PDF", 300, 300)).setMinMargins(PrintAttributes.Margins.NO_MARGINS).build();
                    PrintDocumentAdapter wrapper = new PrintDocumentAdapter() {
                        @Override public void onStart() { delegate.onStart(); }
                        @Override public void onLayout(PrintAttributes oldAttrs, PrintAttributes newAttrs, CancellationSignal cancel, LayoutResultCallback callback, Bundle extras) { delegate.onLayout(oldAttrs, newAttrs, cancel, callback, extras); }
                        @Override public void onWrite(PageRange[] pages, ParcelFileDescriptor destination, CancellationSignal cancel, WriteResultCallback callback) { delegate.onWrite(pages, destination, cancel, callback); }
                        @Override public void onFinish() { delegate.onFinish(); handler.post(() -> finish.accept("PDF print screen closed.")); }
                    };
                    PrintManager manager = (PrintManager)activity.getSystemService(Context.PRINT_SERVICE);
                    if (manager == null) throw new Exception("Android printing is unavailable.");
                    manager.print("ScreenKey page", wrapper, attrs);
                    finished.accept("Choose Save as PDF in the phone's print screen.");
                } catch (Exception e) { finish.accept("PDF save failed: " + e.getMessage()); }
            }
        });
        view.loadDataWithBaseURL(null, html, "text/html", "UTF-8", null);
    }
}
