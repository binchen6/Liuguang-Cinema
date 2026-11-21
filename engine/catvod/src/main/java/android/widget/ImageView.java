package android.widget;

import android.content.Context;
import android.graphics.Bitmap;
import android.view.View;

public class ImageView extends View {
    public enum ScaleType { CENTER, CENTER_CROP, FIT_CENTER, FIT_XY }
    public ImageView(Context context) { super(context); }
    public void setScaleType(ScaleType scaleType) { }
    public void setImageBitmap(Bitmap bitmap) { }
}
