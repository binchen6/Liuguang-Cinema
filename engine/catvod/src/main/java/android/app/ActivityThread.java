package android.app;

public final class ActivityThread {
    private static volatile Application currentApp;

    public static void setCurrentApplication(Application app) {
        currentApp = app;
    }

    public static Application currentApplication() {
        return currentApp;
    }

    public static String currentPackageName() {
        Application app = currentApp;
        return app != null ? app.getPackageName() : "tv.desktop";
    }
}
