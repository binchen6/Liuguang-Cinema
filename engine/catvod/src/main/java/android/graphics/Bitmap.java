package android.graphics;

import java.io.IOException;
import java.io.OutputStream;

public final class Bitmap {
    public enum Config { ARGB_8888, RGB_565 }
    public enum CompressFormat { PNG, JPEG }
    private final int width;
    private final int height;
    private final int[] pixels;
    private Bitmap(int width, int height) { this.width = width; this.height = height; this.pixels = new int[width * height]; }
    public static Bitmap createBitmap(int width, int height, Config config) { return new Bitmap(width, height); }
    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public void setPixel(int x, int y, int color) { pixels[y * width + x] = color; }
    public int getPixel(int x, int y) { return pixels[y * width + x]; }
    public void setPixels(int[] source, int offset, int stride, int x, int y, int width, int height) {
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++) pixels[(y + row) * this.width + x + column] = source[offset + row * stride + column];
    }
    public boolean compress(CompressFormat format, int quality, OutputStream stream) throws IOException {
        java.awt.image.BufferedImage image = new java.awt.image.BufferedImage(width, height, java.awt.image.BufferedImage.TYPE_INT_ARGB);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) image.setRGB(x, y, pixels[y * width + x]);
        return javax.imageio.ImageIO.write(image, format == CompressFormat.JPEG ? "jpg" : "png", stream);
    }
}
