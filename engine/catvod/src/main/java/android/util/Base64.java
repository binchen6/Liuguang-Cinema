package android.util;

public final class Base64 {
    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int URL_SAFE = 8;
    private Base64() { }
    public static byte[] decode(String value, int flags) { return java.util.Base64.getDecoder().decode(value); }
    public static byte[] decode(byte[] value, int flags) { return java.util.Base64.getDecoder().decode(value); }
    public static byte[] encode(byte[] value, int flags) { return encodeToString(value, flags).getBytes(java.nio.charset.StandardCharsets.US_ASCII); }
    public static String encodeToString(byte[] value, int flags) {
        java.util.Base64.Encoder encoder = (flags & URL_SAFE) != 0 ? java.util.Base64.getUrlEncoder() : java.util.Base64.getEncoder();
        if ((flags & NO_PADDING) != 0) encoder = encoder.withoutPadding();
        return encoder.encodeToString(value);
    }
}
