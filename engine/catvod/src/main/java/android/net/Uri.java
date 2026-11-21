package android.net;

import java.net.URI;
import java.net.URLDecoder;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public class Uri {
    private final String value;
    private final URI uri;
    protected Uri(String value) { this.value = value; this.uri = URI.create(value); }
    public static Uri parse(String value) { return new Uri(value); }
    public static Uri fromFile(java.io.File file) { return new Uri(file.toURI().toString()); }
    public String getEncodedPath() { return uri.getRawPath(); }
    public static String encode(String value) { return encode(value, null); }
    public static String encode(String value, String allow) {
        StringBuilder result = new StringBuilder();
        for (byte b : value.getBytes(StandardCharsets.UTF_8)) {
            char c = (char) (b & 0xff);
            if (Character.isLetterOrDigit(c) || "-_.!~*'()".indexOf(c) >= 0 || (allow != null && allow.indexOf(c) >= 0)) result.append(c);
            else result.append('%').append(String.format("%02X", b));
        }
        return result.toString();
    }
    public static String decode(String value) { return URLDecoder.decode(value, StandardCharsets.UTF_8); }
    public String getScheme() { return uri.getScheme(); }
    public String getHost() { return uri.getHost(); }
    public String getAuthority() { return uri.getAuthority(); }
    public String getPath() { return uri.getPath(); }
    public int getPort() { return uri.getPort(); }
    public boolean isAbsolute() { return uri.isAbsolute(); }
    public String getLastPathSegment() { List<String> parts = getPathSegments(); return parts.isEmpty() ? null : parts.get(parts.size() - 1); }
    public List<String> getPathSegments() { List<String> result = new ArrayList<>(); for (String part : getPath().split("/")) if (!part.isEmpty()) result.add(decode(part)); return result; }
    public String getQueryParameter(String name) { return query().get(name); }
    public List<String> getQueryParameters(String name) { return List.of(getQueryParameter(name)); }
    public Builder buildUpon() { return new Builder(value); }
    private Map<String, String> query() {
        Map<String, String> result = new LinkedHashMap<>();
        if (uri.getRawQuery() == null) return result;
        for (String pair : uri.getRawQuery().split("&")) {
            int split = pair.indexOf('=');
            result.put(decode(split < 0 ? pair : pair.substring(0, split)), split < 0 ? "" : decode(pair.substring(split + 1)));
        }
        return result;
    }
    @Override public String toString() { return value; }

    public static final class Builder {
        private String scheme = "", authority = "", path = "", query = "";
        public Builder() { }
        private Builder(String value) {
            Uri parsed = parse(value);
            scheme = parsed.getScheme() == null ? "" : parsed.getScheme();
            authority = parsed.getAuthority() == null ? "" : parsed.getAuthority();
            path = parsed.getPath() == null ? "" : parsed.getPath();
            query = parsed.uri.getRawQuery() == null ? "" : parsed.uri.getRawQuery();
        }
        public Builder scheme(String value) { scheme = value; return this; }
        public Builder authority(String value) { authority = value; return this; }
        public Builder path(String value) { path = value; return this; }
        public Builder appendPath(String value) { path = path.replaceAll("/$", "") + "/" + value.replaceAll("^/", ""); return this; }
        public Builder appendQueryParameter(String name, String value) { query += (query.isEmpty() ? "" : "&") + encode(name) + "=" + encode(value); return this; }
        public Builder clearQuery() { query = ""; return this; }
        public Uri build() { return parse((scheme.isEmpty() ? "" : scheme + "://") + authority + path + (query.isEmpty() ? "" : "?" + query)); }
    }
}
