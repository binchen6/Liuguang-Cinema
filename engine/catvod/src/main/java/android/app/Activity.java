package android.app;

import android.content.ComponentName;

public class Activity extends Application {
    public int checkSelfPermission(String permission) { return 0; }
    public void requestPermissions(String[] permissions, int requestCode) { }
    public android.view.Window getWindow() { return new android.view.Window(); }
    public ComponentName getComponentName() { return new ComponentName("tv.desktop", "DesktopActivity"); }
}
