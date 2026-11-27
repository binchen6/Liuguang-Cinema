package android.os;

import java.util.HashMap;
import java.util.Map;

public class Bundle {
    private final Map<String, Object> values = new HashMap<>();
    public void putString(String key, String value) { values.put(key, value); }
    public String getString(String key) { Object v = values.get(key); return v == null ? null : String.valueOf(v); }
    public String getString(String key, String def) { String v = getString(key); return v != null ? v : def; }
    public void putInt(String key, int value) { values.put(key, value); }
    public int getInt(String key, int def) { Object v = values.get(key); return v instanceof Number n ? n.intValue() : def; }
    public boolean containsKey(String key) { return values.containsKey(key); }
}
