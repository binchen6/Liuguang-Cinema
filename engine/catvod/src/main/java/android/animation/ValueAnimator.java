package android.animation;

public class ValueAnimator extends Animator {
    public interface AnimatorUpdateListener { void onAnimationUpdate(ValueAnimator animation); }
    private Object animatedValue = 0f;
    public static ValueAnimator ofFloat(float... values) {
        ValueAnimator a = new ValueAnimator();
        if (values != null && values.length > 0) a.animatedValue = values[values.length - 1];
        return a;
    }
    public static ValueAnimator ofInt(int... values) {
        ValueAnimator a = new ValueAnimator();
        if (values != null && values.length > 0) a.animatedValue = values[values.length - 1];
        return a;
    }
    public Object getAnimatedValue() { return animatedValue; }
    public void addUpdateListener(AnimatorUpdateListener listener) {}
    @Override public ValueAnimator setDuration(long duration) { return this; }
}
