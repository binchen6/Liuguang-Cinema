package android.text;

import java.util.Collection;
import java.util.regex.Pattern;

public final class TextUtils {
    private TextUtils() { }
    public static boolean isEmpty(CharSequence value) { return value == null || value.length() == 0; }
    public static boolean isDigitsOnly(CharSequence value) { return value != null && value.chars().allMatch(Character::isDigit); }
    public static boolean equals(CharSequence a, CharSequence b) { return a == null ? b == null : a.toString().contentEquals(b); }
    public static String join(CharSequence delimiter, Collection<?> values) { return String.join(delimiter, values.stream().map(String::valueOf).toList()); }
    public static String join(CharSequence delimiter, Object[] values) { return String.join(delimiter, java.util.Arrays.stream(values).map(String::valueOf).toList()); }
    public static String[] split(String text, String expression) { return text.split(Pattern.quote(expression)); }
    public static CharSequence concat(CharSequence... values) { return String.join("", java.util.Arrays.stream(values).map(String::valueOf).toArray(String[]::new)); }
    public static int indexOf(CharSequence text, char ch) { return text == null ? -1 : text.toString().indexOf(ch); }
    public static int lastIndexOf(CharSequence text, char ch) { return text == null ? -1 : text.toString().lastIndexOf(ch); }
    public static CharSequence substring(CharSequence text, int start, int end) { return text.subSequence(start, end); }
    public static boolean startsWith(CharSequence text, CharSequence prefix) { return text != null && text.toString().startsWith(prefix.toString()); }
    public static boolean endsWith(CharSequence text, CharSequence suffix) { return text != null && text.toString().endsWith(suffix.toString()); }
    public static boolean contains(CharSequence text, CharSequence sequence) { return text != null && text.toString().contains(sequence); }
}
