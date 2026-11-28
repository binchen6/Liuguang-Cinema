package android.os;

import java.io.File;

public final class Environment {
    public static final String DIRECTORY_DOWNLOADS = "Downloads";
    public static final String DIRECTORY_MOVIES = "Movies";
    public static final String MEDIA_MOUNTED = "mounted";

    private Environment() { }

    public static File getExternalStorageDirectory() {
        String configured = System.getProperty("tv.spider.dataDir");
        if (configured == null || configured.isBlank()) configured = System.getenv("TV_SPIDER_DATA_DIR");
        File base = (configured != null && !configured.isBlank())
            ? new File(configured)
            : new File(System.getProperty("user.home"), "AppData/Local/TV/spider");
        File dir = new File(base, "sdcard");
        dir.mkdirs();
        return dir;
    }

    public static File getExternalStoragePublicDirectory(String type) {
        File dir = (type == null || type.isBlank())
            ? getExternalStorageDirectory()
            : new File(getExternalStorageDirectory(), type);
        dir.mkdirs();
        return dir;
    }

    public static String getExternalStorageState() {
        return MEDIA_MOUNTED;
    }
}
