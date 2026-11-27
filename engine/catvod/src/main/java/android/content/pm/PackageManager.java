package android.content.pm;

public class PackageManager {
    public static final int PERMISSION_GRANTED = 0;
    public static final int PERMISSION_DENIED = -1;
    public static final String FEATURE_LEANBACK = "android.software.leanback";
    public static final String FEATURE_TELEVISION = "android.hardware.type.television";
    public boolean hasSystemFeature(String name) { return false; }
}
