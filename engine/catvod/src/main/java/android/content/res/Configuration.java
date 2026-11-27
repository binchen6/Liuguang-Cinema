package android.content.res;

public class Configuration {
    public static final int UI_MODE_NIGHT_MASK = 0x30;
    public static final int UI_MODE_NIGHT_YES = 0x20;
    public static final int UI_MODE_NIGHT_NO = 0x10;
    public static final int ORIENTATION_LANDSCAPE = 2;
    public static final int ORIENTATION_PORTRAIT = 1;
    public int uiMode = UI_MODE_NIGHT_YES;
    public int orientation = ORIENTATION_LANDSCAPE;
    public float fontScale = 1.0f;
}
