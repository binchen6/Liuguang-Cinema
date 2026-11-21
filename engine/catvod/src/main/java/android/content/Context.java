package android.content;

import java.io.File;

public class Context {
    public static final String CLIPBOARD_SERVICE = "clipboard";
    public static final int MODE_PRIVATE = 0;
    public Context getApplicationContext() { return this; }
    public String getPackageName() { return "tv.desktop"; }
    public File getFilesDir() { return new File(System.getProperty("user.home")); }
    public File getCacheDir() { return getFilesDir(); }
    public File getExternalFilesDir(String type) { return getFilesDir(); }
    public SharedPreferences getSharedPreferences(String name, int mode) { return null; }
    public Object getSystemService(String name) { return null; }
    public void startActivity(Intent intent) { }
    public android.content.res.Resources getResources() { return new android.content.res.Resources(); }
}
