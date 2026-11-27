package android.graphics;

public class RectF {
    public float left, top, right, bottom;
    public RectF() {}
    public RectF(float left, float top, float right, float bottom) {
        this.left = left; this.top = top; this.right = right; this.bottom = bottom;
    }
    public float width() { return right - left; }
    public float height() { return bottom - top; }
}
