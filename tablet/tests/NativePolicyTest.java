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
        check("http://192.168.10.31:8090/status".equals(RequestPolicy.endpoint("alpha", "/status")), "alpha endpoint");
        check("http://192.168.10.32:8090/status".equals(RequestPolicy.endpoint("beta", "/status")), "beta endpoint");
        System.out.println("NativePolicyTest PASS");
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
