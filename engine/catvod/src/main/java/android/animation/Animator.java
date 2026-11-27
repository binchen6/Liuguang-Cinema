package android.animation;

public abstract class Animator {
    public interface AnimatorListener {
        default void onAnimationStart(Animator animation) {}
        default void onAnimationEnd(Animator animation) {}
        default void onAnimationCancel(Animator animation) {}
        default void onAnimationRepeat(Animator animation) {}
    }
    public void start() {}
    public void cancel() {}
    public void end() {}
    public boolean isRunning() { return false; }
    public Animator setDuration(long duration) { return this; }
    public void addListener(AnimatorListener listener) {}
}
