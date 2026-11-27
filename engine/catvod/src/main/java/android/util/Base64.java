package android.util;

import java.nio.charset.StandardCharsets;

public final class Base64 {
    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int CRLF = 4;
    public static final int URL_SAFE = 8;
    public static final int NO_CLOSE = 16;

    private Base64() { }

    public static byte[] decode(String value, int flags) {
        if (value == null) return new byte[0];
        String cleaned = value.replaceAll("\\s+", "");
        if ((flags & URL_SAFE) != 0 || cleaned.indexOf('-') >= 0 || cleaned.indexOf('_') >= 0) {
            cleaned = cleaned.replace('-', '+').replace('_', '/');
        }
        int mod = cleaned.length() % 4;
        if (mod == 2) cleaned += "==";
        else if (mod == 3) cleaned += "=";
        return java.util.Base64.getDecoder().decode(cleaned);
    }

    public static byte[] decode(byte[] value, int flags) {
        if (value == null) return new byte[0];
        return decode(new String(value, StandardCharsets.US_ASCII), flags);
    }

    public static byte[] encode(byte[] value, int flags) {
        return encodeToString(value, flags).getBytes(StandardCharsets.US_ASCII);
    }

    public static String encodeToString(byte[] value, int flags) {
        if (value == null) return "";
        java.util.Base64.Encoder encoder = (flags & URL_SAFE) != 0
            ? java.util.Base64.getUrlEncoder()
            : java.util.Base64.getEncoder();
        if ((flags & NO_PADDING) != 0) encoder = encoder.withoutPadding();
        return encoder.encodeToString(value);
    }
}
