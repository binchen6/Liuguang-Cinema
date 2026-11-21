namespace TV.Models;

public sealed record EpisodeTiming(long IntroEndMs = 0, long OutroStartMs = 0)
{
    public static EpisodeTiming Normalize(EpisodeTiming? value)
    {
        if (value is null) return new();
        var intro = Math.Clamp(value.IntroEndMs, 0, 6 * 60 * 60 * 1000L);
        var outro = Math.Clamp(value.OutroStartMs, 0, 6 * 60 * 60 * 1000L);
        return new EpisodeTiming(intro, outro > intro ? outro : 0);
    }
}
