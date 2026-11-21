package android.os;

public final class SystemClock {
    private SystemClock() { }
    public static long elapsedRealtime() { return System.nanoTime() / 1_000_000L; }
    public static long uptimeMillis() { return elapsedRealtime(); }
    public static void sleep(long milliseconds) { try { Thread.sleep(milliseconds); } catch (InterruptedException error) { Thread.currentThread().interrupt(); } }
}
