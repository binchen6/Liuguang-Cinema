package android.app;

import android.content.ContextWrapper;
import android.os.Bundle;

public class Application extends ContextWrapper {
    public interface ActivityLifecycleCallbacks {
        default void onActivityCreated(Activity activity, Bundle savedInstanceState) {}
        default void onActivityStarted(Activity activity) {}
        default void onActivityResumed(Activity activity) {}
        default void onActivityPaused(Activity activity) {}
        default void onActivityStopped(Activity activity) {}
        default void onActivitySaveInstanceState(Activity activity, Bundle outState) {}
        default void onActivityDestroyed(Activity activity) {}
    }

    public Application() { super(); }
    public void registerActivityLifecycleCallbacks(ActivityLifecycleCallbacks callback) {}
    public void unregisterActivityLifecycleCallbacks(ActivityLifecycleCallbacks callback) {}
}
