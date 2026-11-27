package android.graphics;

import java.io.InputStream;

public class BitmapFactory {
    public static class Options {
        public boolean inJustDecodeBounds;
        public int inSampleSize = 1;
        public int outWidth;
        public int outHeight;
        public String outMimeType;
    }
    public static Bitmap decodeByteArray(byte[] data, int offset, int length) { return Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888); }
    public static Bitmap decodeByteArray(byte[] data, int offset, int length, Options opts) { return Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888); }
    public static Bitmap decodeStream(InputStream is) { return Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888); }
    public static Bitmap decodeFile(String pathName) { return Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888); }
}
