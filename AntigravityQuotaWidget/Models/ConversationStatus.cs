using System.Text.Json.Serialization;

namespace AntigravityQuotaWidget.Models;

public class ConversationStatusPayload
{
    [JsonPropertyName("isBusy")]
    public bool IsBusy { get; set; } = false;

    [JsonPropertyName("activeCount")]
    public int ActiveCount { get; set; } = 0;

    [JsonPropertyName("activeConversations")]
    public List<ActiveConversationInfo> ActiveConversations { get; set; } = new();

    [JsonPropertyName("lastCompleted")]
    public CompletedConversationInfo? LastCompleted { get; set; }
}

public class ActiveConversationInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("stepCount")]
    public int StepCount { get; set; } = 0;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 0;

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";
}

public class CompletedConversationInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 0;

    [JsonPropertyName("completedAt")]
    public string CompletedAt { get; set; } = "";
}
