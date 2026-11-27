package android.content;

import java.io.File;

public class Context {
    public static final String CLIPBOARD_SERVICE = "clipboard";
    public static final String WIFI_SERVICE = "wifi";
    public static final String CONNECTIVITY_SERVICE = "connectivity";
    public static final int MODE_PRIVATE = 0;

    public Context getApplicationContext() { return this; }
    public String getPackageName() { return "tv.desktop"; }
    public File getFilesDir() {
        String configured = System.getProperty("tv.spider.dataDir");
        File dir = (configured != null && !configured.isBlank())
            ? new File(configured)
            : new File(System.getProperty("user.home"), "AppData/Local/TV/spider");
        dir.mkdirs();
        return dir;
    }
    public File getCacheDir() { File dir = new File(getFilesDir(), "cache"); dir.mkdirs(); return dir; }
    public File getExternalCacheDir() { return getCacheDir(); }
    public File getExternalFilesDir(String type) {
        File dir = (type == null || type.isBlank()) ? getFilesDir() : new File(getFilesDir(), type);
        dir.mkdirs();
        return dir;
    }
    public File getDir(String name, int mode) {
        File dir = new File(getFilesDir(), "app_" + name);
        dir.mkdirs();
        return dir;
    }
    public File getDatabasePath(String name) {
        File dir = new File(getFilesDir(), "databases");
        dir.mkdirs();
        return new File(dir, name);
    }
    public SharedPreferences getSharedPreferences(String name, int mode) { return null; }
    public Object getSystemService(String name) { return null; }
    public void startActivity(Intent intent) { }
    public android.content.res.Resources getResources() { return new android.content.res.Resources(); }
    public android.content.res.AssetManager getAssets() { return new android.content.res.AssetManager(); }
    public android.content.pm.PackageManager getPackageManager() { return new android.content.pm.PackageManager(); }
    public ContentResolver getContentResolver() { return new ContentResolver(); }
    public int checkSelfPermission(String permission) { return android.content.pm.PackageManager.PERMISSION_GRANTED; }
}
