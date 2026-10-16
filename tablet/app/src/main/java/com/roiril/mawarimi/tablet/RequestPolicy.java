package com.roiril.mawarimi.tablet;

import java.nio.charset.StandardCharsets;
import java.util.regex.Pattern;

public final class RequestPolicy {
    public static final int MAX_BODY_BYTES = 16 * 1024;
    public static final int MAX_RESPONSE_BYTES = 64 * 1024;
    private static final Pattern REQUEST_ID = Pattern.compile("[A-Za-z0-9._:-]{1,128}");

    // index.html が <script src> で読む js。足したらここにも足す（無いと WebView には 404 で黙って届かない）。
    // NativePolicyTest が index.html の <script src> を全部ここと突き合わせる。
    private static final String[] WEB_SCRIPTS = {
        "/tablet-transport.js", "/briefing-presentation.js", "/signal-cut.js", "/title-write.js", "/tablet-sound.js",
        "/romi-panel.js",
    };

    private RequestPolicy() {}

    public static boolean servableWebPath(String path) {
        if (path == null) return false;
        if (path.equals("/index.html") || path.startsWith("/asset/")) return true;
        for (String script : WEB_SCRIPTS) if (script.equals(path)) return true;
        return false;
    }

    public static boolean validQuest(String quest) {
        return "alpha".equals(quest) || "beta".equals(quest);
    }

    public static boolean validRequestId(String requestId) {
        return requestId != null && REQUEST_ID.matcher(requestId).matches();
    }

    // 送り先。alpha / beta は Quest（:8090）、cart は終幕の台車 Romi（ESP32・:80・静的 IP .51）。
    // cart はスタッフ設定の「台車の自動走行」だけが使う。setQuest には通さない（validQuest は Quest だけ）。
    public static boolean validTarget(String target) {
        return validQuest(target) || "cart".equals(target);
    }

    public static boolean validRequest(String requestId, String quest, String method, String path, String body) {
        if (!validRequestId(requestId) || !validTarget(quest) || method == null || path == null) return false;
        boolean allowed = "cart".equals(quest)
            ? "GET".equals(method) && ("/ending".equals(path) || "/stop".equals(path) || "/info".equals(path))
                || "POST".equals(method) && ("/ending".equals(path) || "/ending/go".equals(path))
            : "GET".equals(method) && "/status".equals(path)
                || "POST".equals(method) && ("/set".equals(path) || "/clear".equals(path) || "/tablet/pulse".equals(path));
        if (!allowed) return false;
        return body == null || body.getBytes(StandardCharsets.UTF_8).length <= MAX_BODY_BYTES;
    }

    public static String endpoint(String quest, String path) {
        if ("cart".equals(quest)) return "http://192.168.10.51" + path;
        String host = "alpha".equals(quest) ? "192.168.10.31" : "192.168.10.32";
        return "http://" + host + ":8090" + path;
    }
}
