package com.roiril.mawarimi.tablet;

import java.nio.charset.StandardCharsets;
import java.util.regex.Pattern;

public final class RequestPolicy {
    public static final int MAX_BODY_BYTES = 16 * 1024;
    public static final int MAX_RESPONSE_BYTES = 64 * 1024;
    private static final Pattern REQUEST_ID = Pattern.compile("[A-Za-z0-9._:-]{1,128}");

    private RequestPolicy() {}

    public static boolean validQuest(String quest) {
        return "alpha".equals(quest) || "beta".equals(quest);
    }

    public static boolean validRequestId(String requestId) {
        return requestId != null && REQUEST_ID.matcher(requestId).matches();
    }

    public static boolean validRequest(String requestId, String quest, String method, String path, String body) {
        if (!validRequestId(requestId) || !validQuest(quest) || method == null || path == null) return false;
        boolean allowed = "GET".equals(method) && "/status".equals(path)
            || "POST".equals(method) && ("/set".equals(path) || "/clear".equals(path) || "/tablet/pulse".equals(path));
        if (!allowed) return false;
        return body == null || body.getBytes(StandardCharsets.UTF_8).length <= MAX_BODY_BYTES;
    }

    public static String endpoint(String quest, String path) {
        String host = "alpha".equals(quest) ? "192.168.10.31" : "192.168.10.32";
        return "http://" + host + ":8090" + path;
    }
}
