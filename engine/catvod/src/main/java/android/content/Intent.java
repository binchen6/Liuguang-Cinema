package android.content;

import android.net.Uri;

public class Intent {
    public static final String ACTION_VIEW = "android.intent.action.VIEW";
    public static final int FLAG_ACTIVITY_NEW_TASK = 0x10000000;
    public static final int FLAG_GRANT_READ_URI_PERMISSION = 1;
    public Intent() { }
    public Intent(String action) { }
    public Intent setClassName(String packageName, String className) { return this; }
    public Intent setDataAndType(Uri uri, String type) { return this; }
    public Intent setData(Uri uri) { return this; }
    public Intent addFlags(int flags) { return this; }
    public Intent putExtra(String name, String value) { return this; }
}
