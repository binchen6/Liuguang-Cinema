package android.app;

import android.content.Context;
import android.content.DialogInterface;
import android.view.View;
import android.view.Window;

public class Dialog implements DialogInterface {
    private final Context context;
    private final Window window = new Window();
    private boolean showing;
    public Dialog(Context context) { this.context = context; }
    public Dialog(Context context, int themeResId) { this.context = context; }
    public Context getContext() { return context; }
    public Window getWindow() { return window; }
    public void setContentView(View view) {}
    public void setTitle(CharSequence title) {}
    public void setCancelable(boolean flag) {}
    public void setCanceledOnTouchOutside(boolean cancel) {}
    public void setOnDismissListener(OnDismissListener listener) {}
    public void show() { showing = true; }
    public boolean isShowing() { return showing; }
    @Override public void dismiss() { showing = false; }
    @Override public void cancel() { showing = false; }
    public interface OnDismissListener { void onDismiss(DialogInterface dialog); }
}
