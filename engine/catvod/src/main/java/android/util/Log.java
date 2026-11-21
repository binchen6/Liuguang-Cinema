package android.util;

public final class Log {
    private Log() { }
    public static int v(String tag, String text) { return print("V", tag, text, null); }
    public static int d(String tag, String text) { return print("D", tag, text, null); }
    public static int i(String tag, String text) { return print("I", tag, text, null); }
    public static int w(String tag, String text) { return print("W", tag, text, null); }
    public static int e(String tag, String text) { return print("E", tag, text, null); }
    public static int e(String tag, String text, Throwable error) { return print("E", tag, text, error); }
    public static int w(String tag, String text, Throwable error) { return print("W", tag, text, error); }
    public static int d(String tag, String text, Throwable error) { return print("D", tag, text, error); }
    private static int print(String level, String tag, String text, Throwable error) {
        System.err.println(level + "/" + tag + ": " + text);
        if (error != null) error.printStackTrace();
        return text == null ? 0 : text.length();
    }
}
