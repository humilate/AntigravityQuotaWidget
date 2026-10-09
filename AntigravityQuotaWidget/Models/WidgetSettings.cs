using System.Text.Json.Serialization;

namespace AntigravityQuotaWidget.Models;

public class WidgetSettings
{
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = true;

    [JsonPropertyName("refreshIntervalMinutes")]
    public int RefreshIntervalMinutes { get; set; } = 5; // 1, 5, 10, 30

    [JsonPropertyName("notifyThreshold")]
    public int NotifyThreshold { get; set; } = 20; // 0 (off), 10, 20, 30

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonPropertyName("windowX")]
    public double? WindowX { get; set; }

    [JsonPropertyName("windowY")]
    public double? WindowY { get; set; }

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.95;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark"; // "dark" or "light"

    [JsonPropertyName("isMiniMode")]
    public bool IsMiniMode { get; set; } = false;

    [JsonPropertyName("notifyOnRestore")]
    public bool NotifyOnRestore { get; set; } = true;

    [JsonPropertyName("snapToEdge")]
    public bool SnapToEdge { get; set; } = true;

    [JsonPropertyName("monitorConversations")]
    public bool MonitorConversations { get; set; } = true;

    [JsonPropertyName("notifyOnConversationComplete")]
    public bool NotifyOnConversationComplete { get; set; } = true;

    [JsonPropertyName("soundOnConversationComplete")]
    public bool SoundOnConversationComplete { get; set; } = true;

    [JsonPropertyName("launchWithAntigravity")]
    public bool LaunchWithAntigravity { get; set; } = false;

    [JsonPropertyName("exitWithAntigravity")]
    public bool ExitWithAntigravity { get; set; } = true;
}

