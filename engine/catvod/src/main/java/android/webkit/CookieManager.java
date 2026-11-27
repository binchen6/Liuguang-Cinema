package android.webkit;

import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

public class CookieManager {
    private static final CookieManager INSTANCE = new CookieManager();
    private final Map<String, String> cookies = new ConcurrentHashMap<>();

    public static CookieManager getInstance() {
        return INSTANCE;
    }

    public String getCookie(String url) {
        if (url == null) return "";
        String direct = cookies.get(url);
        if (direct != null) return direct;
        try {
            java.net.URI uri = java.net.URI.create(url);
            String host = uri.getHost();
            if (host != null) {
                String byHost = cookies.get(host);
                if (byHost != null) return byHost;
            }
        } catch (Exception ignored) { }
        return "";
    }

    public void setCookie(String url, String value) {
        if (url == null || value == null) return;
        cookies.put(url, value);
        try {
            java.net.URI uri = java.net.URI.create(url);
            if (uri.getHost() != null) cookies.put(uri.getHost(), value);
        } catch (Exception ignored) { }
    }

    public void removeAllCookies(ValueCallback<Boolean> callback) {
        cookies.clear();
        if (callback != null) callback.onReceiveValue(true);
    }

    public void flush() { }
}
