namespace TV.Services.AI;

public sealed record AiModelOption(string Id, string DisplayName, string Description, bool SupportsRealtime = false);

public sealed record AiLanguageOption(string ApiName, string DisplayName);

public sealed record AiSubtitleConfig(
    string ApiKey,
    string Region = "cn",
    string AsrModel = AiSubtitleModels.Qwen3AsrFlashFiletrans,
    string MtModel = AiSubtitleModels.QwenMtFlash,
    string TargetLanguage = "Chinese",
    bool Bilingual = true,
    bool RealtimeTranslate = false,
    string SourceLanguage = "auto")
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public string HttpBaseUrl => string.Equals(Region, "intl", StringComparison.OrdinalIgnoreCase)
        ? "https://dashscope-intl.aliyuncs.com"
        : "https://dashscope.aliyuncs.com";

    public string WsBaseUrl => string.Equals(Region, "intl", StringComparison.OrdinalIgnoreCase)
        ? "wss://dashscope-intl.aliyuncs.com"
        : "wss://dashscope.aliyuncs.com";

    public bool SupportsRealtime => string.Equals(AsrModel, AiSubtitleModels.Qwen3AsrFlashFiletrans, StringComparison.OrdinalIgnoreCase);
}

public sealed record SubtitleWord(TimeSpan Start, TimeSpan End, string Text);

public sealed record SubtitleCue(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? TranslatedText = null,
    IReadOnlyList<SubtitleWord>? Words = null);

public sealed record AiSubtitleProgress(string Stage, double? Percent = null);

public sealed record AiSubtitleResult(string FilePath, int CueCount, string ModelUsed, bool IsTranslated);

public sealed record LiveCaptionUpdate(string OriginalText, string? TranslatedText, bool IsFinal);

public static class AiSubtitleModels
{
    public const string BailianApiKeyConsoleUrl = "https://bailian.console.aliyun.com/?apiKey=1#/api-key";
    public const string ParaformerV2 = "paraformer-v2";
    public const string Qwen3AsrFlashFiletrans = "qwen3-asr-flash-filetrans";
    public const string Qwen3AsrFlashRealtime = "qwen3-asr-flash-realtime";
    public const string QwenMtFlash = "qwen-mt-flash";
    public const string QwenMtPlus = "qwen-mt-plus";

    public const string SettingApiKey = "ai.bailian.apiKey";
    public const string SettingRegion = "ai.bailian.region";
    public const string SettingAsrModel = "ai.asr.model";
    public const string SettingMtModel = "ai.mt.model";
    public const string SettingTargetLanguage = "ai.mt.targetLanguage";
    public const string SettingBilingual = "ai.mt.bilingual";
    public const string SettingRealtimeTranslate = "ai.realtime.translate";
    public const string SettingSourceLanguage = "ai.asr.sourceLanguage";

    public static readonly IReadOnlyList<AiModelOption> AsrModels =
    [
        new(ParaformerV2, "paraformer-v2（便宜）", "高性价比长音频识别，适合常规影视批量生成字幕", SupportsRealtime: false),
        new(Qwen3AsrFlashFiletrans, "qwen3-asr-flash-filetrans（准确 · 支持实时字幕）", "高精度多语种大模型识别，同时解锁实时字幕流式转写", SupportsRealtime: true)
    ];

    public static readonly IReadOnlyList<AiModelOption> MtModels =
    [
        new(QwenMtFlash, "qwen-mt-flash（便宜）", "极速低成本专用翻译模型，适合整片字幕快速翻译"),
        new(QwenMtPlus, "qwen-mt-plus（高质量）", "旗舰级专业翻译模型，语境理解与译文质量更佳")
    ];

    public static readonly IReadOnlyList<AiLanguageOption> TargetLanguages =
    [
        new("Chinese", "简体中文"),
        new("Traditional Chinese", "繁體中文"),
        new("English", "English"),
        new("Japanese", "日本語"),
        new("Korean", "한국어"),
        new("French", "Français"),
        new("German", "Deutsch"),
        new("Spanish", "Español")
    ];

    public static int FindAsrIndex(string? id)
    {
        for (var i = 0; i < AsrModels.Count; i++)
            if (string.Equals(AsrModels[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return 1; // Default to qwen3-asr-flash-filetrans
    }

    public static int FindMtIndex(string? id)
    {
        for (var i = 0; i < MtModels.Count; i++)
            if (string.Equals(MtModels[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return 0; // Default to qwen-mt-flash
    }

    public static int FindTargetLanguageIndex(string? apiName)
    {
        for (var i = 0; i < TargetLanguages.Count; i++)
            if (string.Equals(TargetLanguages[i].ApiName, apiName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0; // Default to Chinese
    }
}
