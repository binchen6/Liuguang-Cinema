package android.webkit;

import android.content.Context;

public class WebView extends android.view.ViewGroup {
    public WebView(Context context) { super(context); }
    public void loadUrl(String url) { }
    public void evaluateJavascript(String script, ValueCallback<String> callback) { if (callback != null) callback.onReceiveValue(""); }
    public void setWebViewClient(WebViewClient client) { }
    public WebSettings getSettings() { return new WebSettings(); }
    public void destroy() { }
}
