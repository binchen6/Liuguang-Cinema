package android.net;

public final class UrlQuerySanitizer {
    private final Uri uri;
    public UrlQuerySanitizer(String url) { uri = Uri.parse(url); }
    public String getValue(String name) { return uri.getQueryParameter(name); }
}
