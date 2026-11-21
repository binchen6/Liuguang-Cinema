package android.app;

import java.util.LinkedHashMap;
import java.util.Map;

public final class ActivityThread {
    private final Map<Object, Object> mActivities = new LinkedHashMap<>();
    private static final ActivityThread INSTANCE = new ActivityThread();
    public static ActivityThread currentActivityThread() { return INSTANCE; }
}
