package android.widget;

import android.content.Context;
import android.view.View;

public class EditText extends View {
    private String text = "";
    public EditText(Context context) { super(context); }
    public void setText(CharSequence value) { text = value == null ? "" : value.toString(); }
    public String getText() { return text; }
}
