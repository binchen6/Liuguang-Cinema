package android.graphics;

public class Canvas {
    public Canvas() {}
    public Canvas(Bitmap bitmap) {}
    public void drawColor(int color) {}
    public void drawBitmap(Bitmap bitmap, float left, float top, Paint paint) {}
    public void drawRect(float left, float top, float right, float bottom, Paint paint) {}
    public void drawRoundRect(RectF rect, float rx, float ry, Paint paint) {}
    public void drawText(String text, float x, float y, Paint paint) {}
}
