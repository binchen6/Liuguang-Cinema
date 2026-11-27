package android.net;

import java.net.URI;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

public class Uri {
    public static final Uri EMPTY = new Uri("");
    private final String value;
    private final URI uri;

    protected Uri(String value) {
        this.value = value == null ? "" : value;
        this.uri = safeCreate(this.value);
    }

    private static URI safeCreate(String raw) {
        if (raw == null || raw.isEmpty()) return null;
        try {
            return URI.create(raw);
        } catch (Exception ignored) {
            try {
                return URI.create(raw.replace(" ", "%20").replace("|", "%7C").replace("^", "%5E"));
            } catch (Exception ignoredAgain) {
                return null;
            }
        }
    }

    public static Uri parse(String value) { return value == null ? EMPTY : new Uri(value); }
    public static Uri fromFile(java.io.File file) { return new Uri(file.toURI().toString()); }
    public String getEncodedPath() { return uri != null ? uri.getRawPath() : null; }
    public String getEncodedQuery() { return uri != null ? uri.getRawQuery() : null; }
    public String getQuery() { return uri != null ? uri.getQuery() : null; }
    public String getFragment() { return uri != null ? uri.getFragment() : null; }
    public String getSchemeSpecificPart() { return uri != null ? uri.getSchemeSpecificPart() : value; }

    public static String encode(String value) { return encode(value, null); }
    public static String encode(String value, String allow) {
        if (value == null) return null;
        StringBuilder result = new StringBuilder();
        for (byte b : value.getBytes(StandardCharsets.UTF_8)) {
            char c = (char) (b & 0xff);
            if (Character.isLetterOrDigit(c) || "-_.!~*'()".indexOf(c) >= 0 || (allow != null && allow.indexOf(c) >= 0)) result.append(c);
            else result.append('%').append(String.format("%02X", b));
        }
        return result.toString();
    }

    public static String decode(String value) {
        if (value == null) return null;
        try { return URLDecoder.decode(value, StandardCharsets.UTF_8); }
        catch (Exception ignored) { return value; }
    }

    public String getScheme() { return uri != null ? uri.getScheme() : null; }
    public String getHost() { return uri != null ? uri.getHost() : null; }
    public String getAuthority() { return uri != null ? uri.getAuthority() : null; }
    public String getPath() { return uri != null ? uri.getPath() : ""; }
    public int getPort() { return uri != null ? uri.getPort() : -1; }
    public boolean isAbsolute() { return uri != null && uri.isAbsolute(); }
    public String getLastPathSegment() { List<String> parts = getPathSegments(); return parts.isEmpty() ? null : parts.get(parts.size() - 1); }

    public List<String> getPathSegments() {
        List<String> result = new ArrayList<>();
        String path = getPath();
        if (path == null) return result;
        for (String part : path.split("/")) if (!part.isEmpty()) result.add(decode(part));
        return result;
    }

    public Set<String> getQueryParameterNames() { return Collections.unmodifiableSet(query().keySet()); }
    public String getQueryParameter(String name) { return query().get(name); }
    public List<String> getQueryParameters(String name) {
        String v = getQueryParameter(name);
        return v == null ? Collections.emptyList() : List.of(v);
    }

    public Builder buildUpon() { return new Builder(value); }

    private Map<String, String> query() {
        Map<String, String> result = new LinkedHashMap<>();
        String rawQuery = uri != null ? uri.getRawQuery() : null;
        if (rawQuery == null) {
            int q = value.indexOf('?');
            if (q >= 0 && q + 1 < value.length()) {
                int hash = value.indexOf('#', q);
                rawQuery = hash > q ? value.substring(q + 1, hash) : value.substring(q + 1);
            }
        }
        if (rawQuery == null || rawQuery.isEmpty()) return result;
        for (String pair : rawQuery.split("&")) {
            if (pair.isEmpty()) continue;
            int split = pair.indexOf('=');
            result.put(decode(split < 0 ? pair : pair.substring(0, split)), split < 0 ? "" : decode(pair.substring(split + 1)));
        }
        return result;
    }

    @Override public String toString() { return value; }

    public static final class Builder {
        private String scheme = "", authority = "", path = "", query = "", fragment = "";
        public Builder() { }
        private Builder(String value) {
            Uri parsed = parse(value);
            scheme = parsed.getScheme() == null ? "" : parsed.getScheme();
            authority = parsed.getAuthority() == null ? "" : parsed.getAuthority();
            path = parsed.getPath() == null ? "" : parsed.getPath();
            query = parsed.getEncodedQuery() == null ? "" : parsed.getEncodedQuery();
            fragment = parsed.getFragment() == null ? "" : parsed.getFragment();
        }
        public Builder scheme(String value) { scheme = value == null ? "" : value; return this; }
        public Builder authority(String value) { authority = value == null ? "" : value; return this; }
        public Builder encodedAuthority(String value) { authority = value == null ? "" : value; return this; }
        public Builder path(String value) { path = value == null ? "" : value; return this; }
        public Builder encodedPath(String value) { path = value == null ? "" : value; return this; }
        public Builder appendPath(String value) { path = path.replaceAll("/$", "") + "/" + (value == null ? "" : value.replaceAll("^/", "")); return this; }
        public Builder appendEncodedPath(String value) { return appendPath(value); }
        public Builder appendQueryParameter(String name, String value) { query += (query.isEmpty() ? "" : "&") + encode(name) + "=" + encode(value); return this; }
        public Builder encodedQuery(String value) { query = value == null ? "" : value; return this; }
        public Builder clearQuery() { query = ""; return this; }
        public Builder fragment(String value) { fragment = value == null ? "" : value; return this; }
        public Uri build() {
            StringBuilder sb = new StringBuilder();
            if (!scheme.isEmpty()) sb.append(scheme).append("://");
            sb.append(authority).append(path);
            if (!query.isEmpty()) sb.append('?').append(query);
            if (!fragment.isEmpty()) sb.append('#').append(fragment);
            return parse(sb.toString());
        }
    }
}
