package com.roiril.mawarimi.tablet;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.res.AssetFileDescriptor;
import android.graphics.Color;
import android.media.AudioManager;
import android.net.Uri;
import android.os.Bundle;
import android.os.SystemClock;
import android.util.Log;
import android.view.View;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.PermissionRequest;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

public final class MainActivity extends Activity {
    private static final String TAG = "DoctorTablet";
    private static final String LOCAL_HOST = "appassets.androidplatform.net";
    private static final String LOCAL_URL = "https://" + LOCAL_HOST + "/index.html";
    private static final String PREFS = "doctor_tablet";
    private static final String QUEST_KEY = "quest";
    private static final String CSP = "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; "
        + "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; "
        + "connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    private final ThreadPoolExecutor requestExecutor = new ThreadPoolExecutor(
        2, 2, 30, TimeUnit.SECONDS, new ArrayBlockingQueue<>(16));
    private final ConcurrentHashMap<String, PendingRequest> pendingRequests = new ConcurrentHashMap<>();
    private WebView webView;
    private SharedPreferences preferences;
    private long startedAt;
    private int pageSequence;
    private int lifecycleSequence;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        startedAt = SystemClock.elapsedRealtime();
        preferences = getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        applyInitialQuest();
        logAssetInventory();
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN
            | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        enterFullscreen();

        webView = new WebView(this);
        webView.setBackgroundColor(Color.BLACK);
        configureWebView(webView);
        setContentView(webView);
        webView.loadUrl(LOCAL_URL);
    }

    private void applyInitialQuest() {
        if (preferences.contains(QUEST_KEY)) return;
        String initialQuest = getIntent().getStringExtra(QUEST_KEY);
        if (RequestPolicy.validQuest(initialQuest)) {
            preferences.edit().putString(QUEST_KEY, initialQuest).apply();
            Log.i(TAG, "initialQuest=" + initialQuest);
        }
    }

    private void logAssetInventory() {
        try {
            Log.i(TAG, "assetList root=" + Arrays.toString(getAssets().list("")));
            Log.i(TAG, "assetList web=" + Arrays.toString(getAssets().list("web")));
            Log.i(TAG, "assetList web/asset=" + Arrays.toString(getAssets().list("web/asset")));
        } catch (IOException error) {
            Log.e(TAG, "assetList failed", error);
        }
    }

    private void configureWebView(WebView view) {
        boolean debuggable = (getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0;
        WebView.setWebContentsDebuggingEnabled(debuggable);
        WebSettings settings = view.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setAllowFileAccessFromFileURLs(false);
        settings.setAllowUniversalAccessFromFileURLs(false);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        settings.setSupportMultipleWindows(false);
        settings.setGeolocationEnabled(false);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSafeBrowsingEnabled(true);
        view.addJavascriptInterface(new TabletBridge(), "TabletHost");
        view.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onPermissionRequest(PermissionRequest request) {
                request.deny();
            }
        });
        view.setWebViewClient(new LocalClient());
    }

    private void enterFullscreen() {
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) enterFullscreen();
    }

    @Override
    protected void onPause() {
        if (webView != null) {
            final WebView pausingView = webView;
            final int sequence = ++lifecycleSequence;
            webView.evaluateJavascript("(function(){if(window.__tabletLifecycle)window.__tabletLifecycle(true);"
                + "document.querySelectorAll('audio,video').forEach(function(m){m.pause();});return true;})()",
                value -> {
                    if (pausingView == webView && sequence == lifecycleSequence) pausingView.pauseTimers();
                    Log.i(TAG, "lifecycle paused=true acknowledged=" + value);
                });
            webView.onPause();
            webView.postDelayed(() -> {
                if (pausingView == webView && sequence == lifecycleSequence) {
                    pausingView.pauseTimers();
                    Log.i(TAG, "lifecycle paused=true fallbackTimerStop");
                }
            }, 250);
        }
        Log.i(TAG, "onPause elapsedMs=" + (SystemClock.elapsedRealtime() - startedAt));
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        lifecycleSequence++;
        if (webView != null) {
            webView.resumeTimers();
            webView.onResume();
            webView.evaluateJavascript("window.__tabletLifecycle&&window.__tabletLifecycle(false)",
                value -> Log.i(TAG, "lifecycle paused=false acknowledged=" + value));
        }
        enterFullscreen();
        Log.i(TAG, "onResume elapsedMs=" + (SystemClock.elapsedRealtime() - startedAt));
    }

    @Override
    protected void onDestroy() {
        for (PendingRequest request : pendingRequests.values()) request.cancel();
        pendingRequests.clear();
        requestExecutor.shutdownNow();
        if (webView != null) {
            webView.removeJavascriptInterface("TabletHost");
            webView.destroy();
            webView = null;
        }
        super.onDestroy();
    }

    private final class LocalClient extends WebViewClient {
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            return !request.isForMainFrame() || !isLocalIndex(request.getUrl());
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            Uri uri = request.getUrl();
            if (!isLocalOrigin(uri) || !("GET".equals(request.getMethod()) || "HEAD".equals(request.getMethod()))) {
                return blocked(403, "Forbidden");
            }
            String path = uri.getEncodedPath();
            if (path == null || path.indexOf('%') >= 0 || path.indexOf('\\') >= 0 || path.contains("//") || path.contains("..")) {
                return blocked(404, "Not Found");
            }
            if ("/".equals(path)) path = "/index.html";
            if (!path.equals("/index.html") && !path.startsWith("/asset/") && !path.equals("/tablet-transport.js")) {
                return blocked(404, "Not Found");
            }
            if (path.equals("/index.html") && !request.isForMainFrame()) return blocked(403, "Forbidden");
            try {
                return assetResponse("web" + path, rangeHeader(request.getRequestHeaders()), "HEAD".equals(request.getMethod()));
            } catch (IOException error) {
                Log.w(TAG, "asset failure path=" + path, error);
                return blocked(404, "Not Found");
            }
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            pageSequence++;
            final int sequence = pageSequence;
            Log.i(TAG, "onPageFinished seq=" + sequence + " elapsedMs="
                + (SystemClock.elapsedRealtime() - startedAt) + " url=" + url);
            probeDom(view, sequence);
            view.postDelayed(() -> probeDom(view, sequence), 1500);
        }
    }

    private void probeDom(WebView view, int sequence) {
        String currentUrl = view.getUrl();
        if (view != webView || currentUrl == null || !isLocalIndex(Uri.parse(currentUrl))) return;
        String script = "(function(){var m=[].slice.call(document.querySelectorAll('audio,video'));"
            + "var r=m.filter(function(x){return x.readyState>=2;}).length;"
            + "var q=(globalThis.TabletTransport&&TabletTransport.getQuest())||'';"
            + "return document.readyState+'|assetReady='+r+'/'+m.length+'|target='+q"
            + "+'|viewport='+innerWidth+'x'+innerHeight;})()";
        view.evaluateJavascript(script, value -> Log.i(TAG, "domState seq=" + sequence
            + " elapsedMs=" + (SystemClock.elapsedRealtime() - startedAt) + " value=" + value));
    }

    private static String rangeHeader(Map<String, String> headers) {
        for (Map.Entry<String, String> header : headers.entrySet()) {
            if ("Range".equalsIgnoreCase(header.getKey())) return header.getValue();
        }
        return null;
    }

    private boolean isLocalOrigin(Uri uri) {
        return "https".equals(uri.getScheme()) && LOCAL_HOST.equals(uri.getHost()) && uri.getPort() == -1;
    }

    private boolean isLocalIndex(Uri uri) {
        return isLocalOrigin(uri) && ("/".equals(uri.getPath()) || "/index.html".equals(uri.getPath()));
    }

    private WebResourceResponse assetResponse(String assetPath, String rangeHeader, boolean head) throws IOException {
        AssetSource source = openAsset(assetPath);
        long totalLength = source.length;
        Map<String, String> headers = baseHeaders();
        headers.put("Accept-Ranges", "bytes");
        headers.put("Content-Security-Policy", CSP);
        if (rangeHeader != null) {
            ByteRange range = ByteRange.parse(rangeHeader, totalLength);
            if (range == null) {
                source.close();
                headers.put("Content-Range", "bytes */" + totalLength);
                headers.put("Content-Length", "0");
                return response(mimeType(assetPath), 416, "Range Not Satisfiable", headers,
                    new ByteArrayInputStream(new byte[0]));
            }
            headers.put("Content-Range", "bytes " + range.start + "-" + range.end + "/" + totalLength);
            headers.put("Content-Length", Long.toString(range.length()));
            Log.i(TAG, "asset206Range path=" + assetPath + " start=" + range.start
                + " end=" + range.end + " total=" + totalLength);
            // WebView's AndroidStreamReader applies the request Range to the
            // intercepted InputStream itself. Supply the complete asset at zero;
            // pre-skipping here doubles the offset and corrupts media packets.
            InputStream body = head ? emptyAndClose(source) : new LimitedInputStream(source.stream, range.end + 1);
            return response(mimeType(assetPath), 206, "Partial Content", headers, body);
        }
        headers.put("Content-Length", Long.toString(totalLength));
        InputStream body = head ? emptyAndClose(source) : source.stream;
        return response(mimeType(assetPath), 200, "OK", headers, body);
    }

    private AssetSource openAsset(String path) throws IOException {
        try {
            AssetFileDescriptor descriptor = getAssets().openFd(path);
            return new AssetSource(descriptor.createInputStream(), descriptor.getLength());
        } catch (IOException compressed) {
            InputStream input = getAssets().open(path);
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.read(buffer)) != -1) output.write(buffer, 0, count);
            input.close();
            byte[] bytes = output.toByteArray();
            return new AssetSource(new ByteArrayInputStream(bytes), bytes.length);
        }
    }

    private static InputStream emptyAndClose(AssetSource source) throws IOException {
        source.close();
        return new ByteArrayInputStream(new byte[0]);
    }

    private static Map<String, String> baseHeaders() {
        Map<String, String> headers = new HashMap<>();
        headers.put("Cache-Control", "no-store");
        headers.put("X-Content-Type-Options", "nosniff");
        return headers;
    }

    private static WebResourceResponse blocked(int status, String reason) {
        return response("text/plain", status, reason, Collections.singletonMap("Cache-Control", "no-store"),
            new ByteArrayInputStream(new byte[0]));
    }

    private static WebResourceResponse response(String mime, int status, String reason,
                                                Map<String, String> headers, InputStream body) {
        return new WebResourceResponse(mime, "UTF-8", status, reason, headers, body);
    }

    private static String mimeType(String path) {
        String lower = path.toLowerCase();
        if (lower.endsWith(".html")) return "text/html";
        if (lower.endsWith(".js")) return "application/javascript";
        if (lower.endsWith(".json")) return "application/json";
        if (lower.endsWith(".png")) return "image/png";
        if (lower.endsWith(".jpg") || lower.endsWith(".jpeg")) return "image/jpeg";
        if (lower.endsWith(".webp")) return "image/webp";
        if (lower.endsWith(".mp3")) return "audio/mpeg";
        if (lower.endsWith(".mp4")) return "video/mp4";
        return "application/octet-stream";
    }

    private final class TabletBridge {
        @JavascriptInterface
        public String getQuest() {
            String quest = preferences.getString(QUEST_KEY, "");
            if (!RequestPolicy.validQuest(quest)) quest = "";
            Log.i(TAG, "native getQuest quest=" + quest);
            return quest;
        }

        @JavascriptInterface
        public void setQuest(String quest) {
            if (!RequestPolicy.validQuest(quest)) {
                Log.w(TAG, "native setQuest rejected");
                return;
            }
            preferences.edit().putString(QUEST_KEY, quest).apply();
            Log.i(TAG, "native setQuest quest=" + quest);
        }

        @JavascriptInterface
        public String getMediaVolume() {
            try {
                AudioManager audioManager = (AudioManager) getSystemService(Context.AUDIO_SERVICE);
                if (audioManager == null) return "null";
                int current = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC);
                int max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
                boolean muted = audioManager.isStreamMute(AudioManager.STREAM_MUSIC);
                return "{\"current\":" + current + ",\"max\":" + max + ",\"muted\":" + muted + "}";
            } catch (RuntimeException error) {
                return "null";
            }
        }

        @JavascriptInterface
        public void request(String requestId, String quest, String method, String path, String body) {
            Log.i(TAG, "native request quest=" + quest + " method=" + method + " path=" + path
                + requestValueSummary(path, body));
            if (!RequestPolicy.validRequest(requestId, quest, method, path, body)) {
                callbackFailure(requestId, "request rejected");
                return;
            }
            PendingRequest pending = new PendingRequest(requestId);
            if (pendingRequests.putIfAbsent(requestId, pending) != null) {
                callbackFailure(requestId, "request id already active");
                return;
            }
            try {
                requestExecutor.execute(() -> executeRequest(pending, quest, method, path, body == null ? "" : body));
            } catch (RuntimeException error) {
                pendingRequests.remove(requestId, pending);
                callbackFailure(requestId, "request queue full");
            }
        }

        @JavascriptInterface
        public void cancel(String requestId) {
            if (!RequestPolicy.validRequestId(requestId)) return;
            PendingRequest request = pendingRequests.remove(requestId);
            if (request != null) request.cancel();
            Log.i(TAG, "native cancel id=" + requestId);
        }
    }

    private void executeRequest(PendingRequest pending, String quest, String method, String path, String body) {
        HttpURLConnection connection = null;
        try {
            if (pending.cancelled) return;
            connection = (HttpURLConnection) new URL(RequestPolicy.endpoint(quest, path)).openConnection();
            pending.connection = connection;
            connection.setInstanceFollowRedirects(false);
            connection.setUseCaches(false);
            connection.setConnectTimeout(5000);
            connection.setReadTimeout(8000);
            connection.setRequestMethod(method);
            connection.setRequestProperty("Accept", "application/json");
            if ("POST".equals(method)) {
                byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
                connection.setDoOutput(true);
                connection.setFixedLengthStreamingMode(bytes.length);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                try (java.io.OutputStream output = connection.getOutputStream()) {
                    output.write(bytes);
                }
            }
            int status = connection.getResponseCode();
            if (status >= 300 && status < 400) throw new IOException("redirect rejected");
            InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
            String responseBody = stream == null ? "" : readResponse(stream);
            Log.i(TAG, "native response id=" + pending.requestId + " status=" + status
                + " bytes=" + responseBody.getBytes(StandardCharsets.UTF_8).length);
            if (!pending.cancelled) callbackSuccess(pending.requestId, status, responseBody);
        } catch (Exception error) {
            if (!pending.cancelled) callbackFailure(pending.requestId, safeError(error));
        } finally {
            pendingRequests.remove(pending.requestId, pending);
            if (connection != null) connection.disconnect();
        }
    }

    private static String readResponse(InputStream input) throws IOException {
        try (InputStream stream = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int total = 0;
            int count;
            while ((count = stream.read(buffer)) != -1) {
                total += count;
                if (total > RequestPolicy.MAX_RESPONSE_BYTES) throw new IOException("response too large");
                output.write(buffer, 0, count);
            }
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        }
    }

    private void callbackSuccess(String requestId, int status, String body) {
        callback("window.__tabletNativeResponse(" + jsonString(requestId) + ",{status:" + status
            + ",body:" + jsonString(body) + "});");
    }

    private void callbackFailure(String requestId, String error) {
        if (!RequestPolicy.validRequestId(requestId)) return;
        callback("window.__tabletNativeResponse(" + jsonString(requestId)
            + ",{status:0,body:\"\",error:" + jsonString(error) + "});");
    }

    private void callback(String script) {
        runOnUiThread(() -> {
            if (webView != null) webView.evaluateJavascript(script, null);
        });
    }

    private static String safeError(Exception error) {
        String message = error.getMessage();
        if (message == null || message.isEmpty()) return error.getClass().getSimpleName();
        return message.length() > 160 ? message.substring(0, 160) : message;
    }

    private static String requestValueSummary(String path, String body) {
        if (!"/set".equals(path) || body == null) return "";
        String lang = body.contains("\"lang\":\"ja\"") ? "ja"
            : body.contains("\"lang\":\"en\"") ? "en"
            : body.contains("\"lang\":\"fr\"") ? "fr" : "?";
        String relief = body.contains("\"relief\":true") ? "true"
            : body.contains("\"relief\":false") ? "false" : "?";
        return " lang=" + lang + " relief=" + relief;
    }

    private static String jsonString(String value) {
        if (value == null) return "null";
        StringBuilder output = new StringBuilder(value.length() + 16).append('"');
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '"': output.append("\\\""); break;
                case '\\': output.append("\\\\"); break;
                case '\b': output.append("\\b"); break;
                case '\f': output.append("\\f"); break;
                case '\n': output.append("\\n"); break;
                case '\r': output.append("\\r"); break;
                case '\t': output.append("\\t"); break;
                default:
                    if (character < 0x20 || character == 0x2028 || character == 0x2029
                        || Character.isSurrogate(character)) {
                        output.append(String.format("\\u%04x", (int) character));
                    } else {
                        output.append(character);
                    }
            }
        }
        return output.append('"').toString();
    }

    private static final class PendingRequest {
        final String requestId;
        volatile HttpURLConnection connection;
        volatile boolean cancelled;

        PendingRequest(String requestId) {
            this.requestId = requestId;
        }

        void cancel() {
            cancelled = true;
            HttpURLConnection active = connection;
            if (active != null) active.disconnect();
        }
    }

    private static final class AssetSource {
        final InputStream stream;
        final long length;

        AssetSource(InputStream stream, long length) {
            this.stream = stream;
            this.length = length;
        }

        void close() throws IOException {
            stream.close();
        }
    }

    private static final class LimitedInputStream extends FilterInputStream {
        private long remaining;

        LimitedInputStream(InputStream input, long remaining) {
            super(input);
            this.remaining = remaining;
        }

        @Override
        public int read() throws IOException {
            if (remaining == 0) return -1;
            int value = super.read();
            if (value >= 0) remaining--;
            return value;
        }

        @Override
        public int read(byte[] bytes, int offset, int length) throws IOException {
            if (length == 0) return 0;
            if (remaining == 0) return -1;
            int count = super.read(bytes, offset, (int) Math.min(length, remaining));
            if (count > 0) remaining -= count;
            return count;
        }

        @Override
        public long skip(long count) throws IOException {
            if (count <= 0 || remaining == 0) return 0;
            long skipped = in.skip(Math.min(count, remaining));
            if (skipped > 0) remaining -= skipped;
            return skipped;
        }

        @Override
        public int available() throws IOException {
            return (int) Math.min(in.available(), remaining);
        }
    }
}
