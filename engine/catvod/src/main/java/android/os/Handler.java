package android.os;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class Handler {
    private static final ScheduledExecutorService EXECUTOR = Executors.newScheduledThreadPool(2);
    public Handler() { }
    public Handler(Looper looper) { }
    public boolean post(Runnable runnable) { EXECUTOR.execute(runnable); return true; }
    public boolean postDelayed(Runnable runnable, long delayMillis) { EXECUTOR.schedule(runnable, delayMillis, TimeUnit.MILLISECONDS); return true; }
    public void removeCallbacks(Runnable runnable) { }
}
