package android.os;

public final class Build {
    public static final String BOARD = "generic";
    public static final String BRAND = "Android";
    public static final String DEVICE = "desktop";
    public static final String FINGERPRINT = "Android/desktop/desktop:14/UP1A.231005.007/1:user/release-keys";
    public static final String HARDWARE = "desktop";
    public static final String MANUFACTURER = "Google";
    public static final String MODEL = "Pixel 8";
    public static final String PRODUCT = "desktop";
    public static final String[] SUPPORTED_ABIS = new String[]{"arm64-v8a", "armeabi-v7a", "x86_64"};

    private Build() { }

    public static final class VERSION {
        public static final int SDK_INT = 34;
        public static final String RELEASE = "14";
        public static final String CODENAME = "REL";
    }

    public static final class VERSION_CODES {
        public static final int KITKAT = 19;
        public static final int LOLLIPOP = 21;
        public static final int M = 23;
        public static final int N = 24;
        public static final int O = 26;
        public static final int P = 28;
        public static final int Q = 29;
        public static final int R = 30;
        public static final int S = 31;
        public static final int TIRAMISU = 33;
        public static final int UPSIDE_DOWN_CAKE = 34;
    }
}
