package tv.engine;

import android.app.Application;
import android.content.Context;
import android.content.ClipboardManager;
import android.content.SharedPreferences;
import android.content.res.AssetManager;

import java.io.File;
import java.util.HashMap;
import java.util.Map;

public final class DesktopApplication extends Application {
    private final ClipboardManager clipboard = new ClipboardManager();
    private final Map<String, MemoryPreferences> preferences = new HashMap<>();
    private final File data = new File(System.getProperty("user.home"), "AppData/Local/TV/spider");

    @Override public SharedPreferences getSharedPreferences(String name, int mode) { return preferences.computeIfAbsent(name, MemoryPreferences::new); }
    @Override public String getPackageName() { return "tv.desktop"; }
    @Override public File getFilesDir() { data.mkdirs(); return data; }
    @Override public File getCacheDir() { File dir = new File(data, "cache"); dir.mkdirs(); return dir; }
    @Override public File getExternalFilesDir(String type) { return getFilesDir(); }
    @Override public Context getApplicationContext() { return this; }
    @Override public Object getSystemService(String name) { return CLIPBOARD_SERVICE.equals(name) ? clipboard : null; }
}
