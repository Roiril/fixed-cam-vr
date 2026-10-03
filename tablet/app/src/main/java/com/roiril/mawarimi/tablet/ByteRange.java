package com.roiril.mawarimi.tablet;

public final class ByteRange {
    public final long start;
    public final long end;

    private ByteRange(long start, long end) {
        this.start = start;
        this.end = end;
    }

    public long length() {
        return end - start + 1;
    }

    public static ByteRange parse(String header, long totalLength) {
        if (header == null || !header.startsWith("bytes=") || totalLength <= 0) return null;
        String value = header.substring(6).trim();
        if (value.isEmpty() || value.indexOf(',') >= 0) return null;
        int separator = value.indexOf('-');
        if (separator < 0 || value.indexOf('-', separator + 1) >= 0) return null;
        String first = value.substring(0, separator).trim();
        String second = value.substring(separator + 1).trim();
        try {
            long start;
            long end;
            if (first.isEmpty()) {
                long suffixLength = Long.parseLong(second);
                if (suffixLength <= 0) return null;
                start = Math.max(0, totalLength - suffixLength);
                end = totalLength - 1;
            } else {
                start = Long.parseLong(first);
                if (start < 0 || start >= totalLength) return null;
                end = second.isEmpty() ? totalLength - 1 : Long.parseLong(second);
                if (end < start) return null;
                end = Math.min(end, totalLength - 1);
            }
            return new ByteRange(start, end);
        } catch (NumberFormatException error) {
            return null;
        }
    }
}
