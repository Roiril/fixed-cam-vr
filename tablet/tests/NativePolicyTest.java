package com.roiril.mawarimi.tablet;

public final class NativePolicyTest {
    public static void main(String[] args) {
        expectRange("bytes=0-99", 1000, 0, 99);
        expectRange("bytes=900-", 1000, 900, 999);
        expectRange("bytes=-100", 1000, 900, 999);
        expectRange("bytes=900-2000", 1000, 900, 999);
        rejectRange("bytes=1000-", 1000);
        rejectRange("bytes=0-1,3-4", 1000);
        rejectRange("items=0-1", 1000);

        check(RequestPolicy.validRequest("abc-123", "alpha", "GET", "/status", ""), "alpha status");
        check(RequestPolicy.validRequest("x", "beta", "POST", "/set", "{}"), "beta set");
        check(RequestPolicy.validRequest("x", "alpha", "POST", "/clear", "{}"), "clear");
        check(RequestPolicy.validRequest("x", "beta", "POST", "/tablet/pulse", "{}"), "pulse");
        check(!RequestPolicy.validRequest("x", "gamma", "GET", "/status", ""), "quest allowlist");
        check(!RequestPolicy.validRequest("x", "alpha", "POST", "/status", ""), "method allowlist");
        check(!RequestPolicy.validRequest("x", "alpha", "GET", "/set", ""), "method path pair");
        check(!RequestPolicy.validRequest("x", "alpha", "GET", "http://evil.test/", ""), "path allowlist");
        check(!RequestPolicy.validRequest("../x", "alpha", "GET", "/status", ""), "request id");
        check(!RequestPolicy.validRequest("x", "alpha", "POST", "/set", repeat('x', 16385)), "body limit");
        check(RequestPolicy.validRequest("x", "cart", "GET", "/ending", ""), "cart ending get");
        check(RequestPolicy.validRequest("x", "cart", "POST", "/ending", "{}"), "cart ending save");
        check(RequestPolicy.validRequest("x", "cart", "POST", "/ending/go", "{}"), "cart ending go");
        check(RequestPolicy.validRequest("x", "cart", "GET", "/stop", ""), "cart stop");
        check(!RequestPolicy.validRequest("x", "cart", "GET", "/run", ""), "cart run is not allowed");
        check(!RequestPolicy.validRequest("x", "cart", "POST", "/set", "{}"), "cart has no Quest paths");
        check(!RequestPolicy.validRequest("x", "alpha", "GET", "/ending", ""), "Quest has no cart paths");
        check(!RequestPolicy.validQuest("cart"), "cart is not a Quest");
        check("http://192.168.10.51/ending".equals(RequestPolicy.endpoint("cart", "/ending")), "cart endpoint");
        check("http://192.168.10.31:8090/status".equals(RequestPolicy.endpoint("alpha", "/status")), "alpha endpoint");
        check("http://192.168.10.32:8090/status".equals(RequestPolicy.endpoint("beta", "/status")), "beta endpoint");
        check(RequestPolicy.servableWebPath("/index.html"), "index");
        check(RequestPolicy.servableWebPath("/asset/briefing-v1.json"), "asset");
        check(!RequestPolicy.servableWebPath("/other.js"), "unknown script");
        check(!RequestPolicy.servableWebPath("/asset"), "asset dir");
        checkPageScripts(args.length > 0 ? args[0] : "tablet/app/src/main/assets/web/index.html");
        System.out.println("NativePolicyTest PASS");
    }

    // index.html が読む js が全部配れること（2026-10-16、signal-cut.js と title-write.js が 404 で黙って落ちていた）
    private static void checkPageScripts(String indexPath) {
        String html;
        try {
            html = new String(java.nio.file.Files.readAllBytes(java.nio.file.Paths.get(indexPath)), "UTF-8");
        } catch (java.io.IOException error) {
            throw new AssertionError("read " + indexPath + ": " + error);
        }
        java.util.regex.Matcher scripts = java.util.regex.Pattern.compile("<script\\s+[^>]*src=\"\\./([^\"]+)\"").matcher(html);
        int count = 0;
        while (scripts.find()) {
            count++;
            check(RequestPolicy.servableWebPath("/" + scripts.group(1)), "page script not servable: " + scripts.group(1));
        }
        check(count >= 4, "page scripts found: " + count);
    }

    private static void expectRange(String header, long total, long start, long end) {
        ByteRange range = ByteRange.parse(header, total);
        check(range != null && range.start == start && range.end == end, header);
    }

    private static void rejectRange(String header, long total) {
        check(ByteRange.parse(header, total) == null, header);
    }

    private static String repeat(char value, int count) {
        StringBuilder result = new StringBuilder(count);
        for (int index = 0; index < count; index++) result.append(value);
        return result.toString();
    }

    private static void check(boolean condition, String name) {
        if (!condition) throw new AssertionError(name);
    }
}
