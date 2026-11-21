package android.content;

import java.io.File;

public class ContextWrapper extends Context {
    private final Context base;
    public ContextWrapper() { this(null); }
    public ContextWrapper(Context base) { this.base = base; }
    @Override public Context getApplicationContext() { return base == null ? this : base.getApplicationContext(); }
    @Override public String getPackageName() { return base == null ? super.getPackageName() : base.getPackageName(); }
    @Override public File getFilesDir() { return base == null ? super.getFilesDir() : base.getFilesDir(); }
    @Override public File getCacheDir() { return base == null ? super.getCacheDir() : base.getCacheDir(); }
    @Override public File getExternalFilesDir(String type) { return base == null ? super.getExternalFilesDir(type) : base.getExternalFilesDir(type); }
    @Override public SharedPreferences getSharedPreferences(String name, int mode) { return base == null ? null : base.getSharedPreferences(name, mode); }
    @Override public Object getSystemService(String name) { return base == null ? null : base.getSystemService(name); }
}
