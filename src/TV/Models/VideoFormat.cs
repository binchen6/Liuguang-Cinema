using System.Globalization;

namespace TV.Models;

public readonly record struct VideoFormat(int Height, int FrameRate)
{
    public bool Matches(QualityOption option) =>
        (option.Height <= 0 || Height == option.Height) && (!option.FrameRate.HasValue || FrameRate == option.FrameRate.Value);

    public static int ReadFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)) return 0;
        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var divisor) || divisor <= 0)
                return 0;
            rate /= divisor;
        }
        return HighFrameRate(rate);
    }

    public static int HighFrameRate(double rate) => rate is >= 47.5 and <= 1000 ? (int)Math.Round(rate) : 0;

    public static IReadOnlyList<QualityOption> ToOptions(IEnumerable<VideoFormat> formats, bool markHighestOriginal = true)
    {
        var options = new List<QualityOption>();
        foreach (var format in formats.Where(value => value.Height > 0).Distinct()
            .OrderByDescending(value => value.Height).ThenByDescending(value => value.FrameRate))
        {
            var original = markHighestOriginal && options.Count == 0;
            var resolution = $"{format.Height}p";
            var label = original
                ? format.FrameRate > 0 ? $"原画（{resolution} · {format.FrameRate}帧）" : $"原画（{resolution}）"
                : format.FrameRate > 0 ? $"{resolution}（{format.FrameRate}帧）" : resolution;
            options.Add(new QualityOption(label, format.Height) { FrameRate = format.FrameRate, IsOriginal = original });
        }
        return options;
    }
}
