package android.content;

public final class ClipData {
    private final String text;
    private ClipData(String text) { this.text = text; }
    public static ClipData newPlainText(CharSequence label, CharSequence text) { return new ClipData(text.toString()); }
    public String getText() { return text; }
}
