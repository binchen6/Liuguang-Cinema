package tv.engine;

import android.app.ActivityThread;
import android.app.Application;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.SharedPreferences;

import java.io.File;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

public final class DesktopApplication extends Application {
    private final ClipboardManager clipboard = new ClipboardManager();
    private final Map<String, MemoryPreferences> preferences = new ConcurrentHashMap<>();
    private final File data;

    public DesktopApplication() {
        String configured = System.getProperty("tv.spider.dataDir");
        this.data = (configured != null && !configured.isBlank())
            ? new File(configured)
            : new File(System.getProperty("user.home"), "AppData/Local/TV/spider");
        this.data.mkdirs();
        ActivityThread.setCurrentApplication(this);
    }

    @Override public SharedPreferences getSharedPreferences(String name, int mode) {
        return preferences.computeIfAbsent(name == null ? "default" : name, key -> new MemoryPreferences(key, new File(data, "prefs")));
    }
    @Override public String getPackageName() { return "tv.desktop"; }
    @Override public File getFilesDir() { data.mkdirs(); return data; }
    @Override public File getCacheDir() { File dir = new File(data, "cache"); dir.mkdirs(); return dir; }
    @Override public File getExternalFilesDir(String type) { return super.getExternalFilesDir(type); }
    @Override public Context getApplicationContext() { return this; }
    @Override public Object getSystemService(String name) { return CLIPBOARD_SERVICE.equals(name) ? clipboard : null; }
}
