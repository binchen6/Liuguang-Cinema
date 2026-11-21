using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TV.Services.Imaging;

internal static class BitmapImages
{
    public static async Task<BitmapImage> FromBytesAsync(byte[] bytes, CancellationToken token = default)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(token);
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream).AsTask(token);
        return image;
    }
}
