using System.Text.Json.Serialization;

namespace AntigravityQuotaWidget.Models;

public class QuotaBucketInfo
{
    [JsonPropertyName("remainingPercent")]
    public object RemainingPercent { get; set; } = 100; // int or "Unavailable"

    [JsonPropertyName("resetIn")]
    public string? ResetIn { get; set; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; set; }
}

public class QuotaGroupInfo
{
    [JsonPropertyName("weekly")]
    public QuotaBucketInfo Weekly { get; set; } = new();

    [JsonPropertyName("fiveHour")]
    public QuotaBucketInfo FiveHour { get; set; } = new();
}

public class QuotaPayload
{
    [JsonPropertyName("gemini")]
    public QuotaGroupInfo Gemini { get; set; } = new();

    [JsonPropertyName("claudeGpt")]
    public QuotaGroupInfo ClaudeGpt { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok"; // "ok", "error", "connecting"

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
