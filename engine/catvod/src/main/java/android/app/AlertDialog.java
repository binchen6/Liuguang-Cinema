package android.app;

import android.content.Context;
import android.content.DialogInterface;
import android.view.View;
import android.view.Window;

public class AlertDialog {
    public Window getWindow() { return new Window(); }
    public boolean isShowing() { return false; }
    public void dismiss() { }
    public static class Builder {
        public Builder(Context context) { }
        public Builder setTitle(CharSequence value) { return this; }
        public Builder setMessage(CharSequence value) { return this; }
        public Builder setView(View value) { return this; }
        public Builder setPositiveButton(CharSequence value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setPositiveButton(int value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setNegativeButton(CharSequence value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setNegativeButton(int value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setNeutralButton(CharSequence value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setNeutralButton(int value, DialogInterface.OnClickListener listener) { return this; }
        public Builder setOnCancelListener(DialogInterface.OnCancelListener listener) { return this; }
        public Builder setOnDismissListener(DialogInterface.OnDismissListener listener) { return this; }
        public Builder setCancelable(boolean value) { return this; }
        public AlertDialog create() { return new AlertDialog(); }
        public AlertDialog show() { return new AlertDialog(); }
    }
}
