package android.os;

import java.io.File;

public final class Environment {
    public static final String DIRECTORY_DOWNLOADS = "Downloads";
    private Environment() { }
    public static File getExternalStorageDirectory() { File dir = new File(System.getProperty("user.home"), "AppData/Local/TV/spider"); dir.mkdirs(); return dir; }
    public static File getExternalStoragePublicDirectory(String type) { return getExternalStorageDirectory(); }
}
