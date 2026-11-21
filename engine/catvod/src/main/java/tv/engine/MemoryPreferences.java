package tv.engine;

import android.content.SharedPreferences;

import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

final class MemoryPreferences implements SharedPreferences {
    private final String name;
    private final Map<String, Object> values = new HashMap<>();
    MemoryPreferences(String name) { this.name = name; }
    @Override public Map<String, ?> getAll() { return values; }
    @Override public String getString(String key, String fallback) { Object value = values.get(key); return value instanceof String ? (String) value : fallback; }
    @Override public Set<String> getStringSet(String key, Set<String> fallback) { Object value = values.get(key); return value instanceof Set ? (Set<String>) value : fallback; }
    @Override public int getInt(String key, int fallback) { Object value = values.get(key); return value instanceof Number ? ((Number) value).intValue() : fallback; }
    @Override public long getLong(String key, long fallback) { Object value = values.get(key); return value instanceof Number ? ((Number) value).longValue() : fallback; }
    @Override public float getFloat(String key, float fallback) { Object value = values.get(key); return value instanceof Number ? ((Number) value).floatValue() : fallback; }
    @Override public boolean getBoolean(String key, boolean fallback) { Object value = values.get(key); return value instanceof Boolean ? (Boolean) value : fallback; }
    @Override public boolean contains(String key) { return values.containsKey(key); }
    @Override public Editor edit() { return new MemoryEditor(); }
    @Override public void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener) { }
    @Override public void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener) { }

    private final class MemoryEditor implements Editor {
        private final Map<String, Object> updates = new HashMap<>();
        private final Set<String> removals = new HashSet<>();
        private boolean clear;
        @Override public Editor putString(String key, String value) { updates.put(key, value); return this; }
        @Override public Editor putStringSet(String key, Set<String> value) { updates.put(key, value); return this; }
        @Override public Editor putInt(String key, int value) { updates.put(key, value); return this; }
        @Override public Editor putLong(String key, long value) { updates.put(key, value); return this; }
        @Override public Editor putFloat(String key, float value) { updates.put(key, value); return this; }
        @Override public Editor putBoolean(String key, boolean value) { updates.put(key, value); return this; }
        @Override public Editor remove(String key) { removals.add(key); return this; }
        @Override public Editor clear() { clear = true; return this; }
        @Override public boolean commit() { apply(); return true; }
        @Override public void apply() { if (clear) values.clear(); removals.forEach(values::remove); values.putAll(updates); }
    }
}
