// ============================================================================
// Models.cs —— 跨模块共享的数据模型
// ============================================================================

namespace MetrikLite;

/// <summary>Codex 配额窗口的一次读数。</summary>
public sealed record QuotaSnapshot(
    string AdapterId,
    string WindowKey,
    double RemainingPercent,
    long? ResetsAtMs,
    long CollectedAtMs,
    string Quality,
    string SourceLabel);

/// <summary>按 Agent 分组后的配额；Primary 是托盘图标显示的窗口。</summary>
public sealed record AgentQuota(
    string AdapterId,
    string DisplayName,
    string BrandHex,
    QuotaSnapshot Primary,
    IReadOnlyList<QuotaSnapshot> AllWindows);
