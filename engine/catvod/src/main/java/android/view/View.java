package android.view;

import android.content.Context;

public class View {
    private ViewParent parent;
    public View() { }
    public View(Context context) { }
    public ViewParent getParent() { return parent; }
    public void setParent(ViewParent value) { parent = value; }
}
