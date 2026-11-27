package tv.engine;

import android.content.SharedPreferences;
import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

final class MemoryPreferences implements SharedPreferences {
    private static final Gson GSON = new Gson();
    private final String name;
    private final File storageFile;
    private final Map<String, Object> values = new ConcurrentHashMap<>();

    MemoryPreferences(String name) {
        this(name, null);
    }

    MemoryPreferences(String name, File prefsDir) {
        this.name = name;
        if (prefsDir != null) {
            prefsDir.mkdirs();
            String safe = name.replaceAll("[^a-zA-Z0-9._-]", "_");
            this.storageFile = new File(prefsDir, safe + ".json");
            loadFromDisk();
        } else {
            this.storageFile = null;
        }
    }

    private void loadFromDisk() {
        if (storageFile == null || !storageFile.isFile()) return;
        try {
            String json = Files.readString(storageFile.toPath(), StandardCharsets.UTF_8);
            Map<String, Object> loaded = GSON.fromJson(json, new TypeToken<Map<String, Object>>() { }.getType());
            if (loaded != null) values.putAll(loaded);
        } catch (Exception ignored) { }
    }

    private synchronized void persistToDisk() {
        if (storageFile == null) return;
        try {
            File tmp = new File(storageFile.getParentFile(), storageFile.getName() + ".tmp");
            Files.writeString(tmp.toPath(), GSON.toJson(values), StandardCharsets.UTF_8);
            if (!tmp.renameTo(storageFile)) {
                Files.writeString(storageFile.toPath(), GSON.toJson(values), StandardCharsets.UTF_8);
                tmp.delete();
            }
        } catch (IOException ignored) { }
    }

    @Override public Map<String, ?> getAll() { return new HashMap<>(values); }
    @Override public String getString(String key, String fallback) { Object value = values.get(key); return value instanceof String ? (String) value : fallback; }
    @Override @SuppressWarnings("unchecked") public Set<String> getStringSet(String key, Set<String> fallback) { Object value = values.get(key); return value instanceof Set ? (Set<String>) value : fallback; }
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
        @Override public Editor putString(String key, String value) { if (value == null) removals.add(key); else updates.put(key, value); return this; }
        @Override public Editor putStringSet(String key, Set<String> value) { if (value == null) removals.add(key); else updates.put(key, value); return this; }
        @Override public Editor putInt(String key, int value) { updates.put(key, value); return this; }
        @Override public Editor putLong(String key, long value) { updates.put(key, value); return this; }
        @Override public Editor putFloat(String key, float value) { updates.put(key, value); return this; }
        @Override public Editor putBoolean(String key, boolean value) { updates.put(key, value); return this; }
        @Override public Editor remove(String key) { removals.add(key); return this; }
        @Override public Editor clear() { clear = true; return this; }
        @Override public boolean commit() { apply(); return true; }
        @Override public void apply() {
            if (clear) values.clear();
            removals.forEach(values::remove);
            values.putAll(updates);
            persistToDisk();
        }
    }
}
