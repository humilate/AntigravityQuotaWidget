using System.Text.Json;
using System.Windows.Threading;
using AntigravityQuotaWidget.Models;

namespace AntigravityQuotaWidget.Services;

public class QuotaService
{
    private readonly AntigravityService _antigravityService;
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;

    private readonly DispatcherTimer _pollTimer;

    public QuotaPayload? LastQuota { get; private set; }
    public event Action<QuotaPayload>? OnQuotaUpdated;

    public QuotaService(
        AntigravityService antigravityService,
        SettingsService settingsService,
        NotificationService notificationService)
    {
        _antigravityService = antigravityService;
        _settingsService = settingsService;
        _notificationService = notificationService;

        _pollTimer = new DispatcherTimer();
        UpdatePollInterval(_settingsService.CurrentSettings.RefreshIntervalMinutes);
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _pollTimer.Start();
        _ = RefreshAsync();
    }

    public void UpdatePollInterval(int minutes)
    {
        if (minutes < 1) minutes = 1;
        _pollTimer.Interval = TimeSpan.FromMinutes(minutes);
    }

    public async Task<QuotaPayload> RefreshAsync()
    {
        try
        {
            var rawJson = await _antigravityService.FetchRawQuotaAsync();
            var parsed = ParseQuotaJson(rawJson);

            LastQuota = parsed;
            _notificationService.CheckAndNotify(parsed, _settingsService.CurrentSettings);
            OnQuotaUpdated?.Invoke(parsed);
            return parsed;
        }
        catch (Exception)
        {
            var errPayload = LastQuota ?? new QuotaPayload();
            errPayload.Status = "error";
            errPayload.ErrorMessage = "Connection failed";
            errPayload.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");

            OnQuotaUpdated?.Invoke(errPayload);
            return errPayload;
        }
    }

    private void RecalculateCountdowns()
    {
        if (LastQuota == null || LastQuota.Status != "ok") return;

        bool changed = false;

        void UpdateBucket(QuotaBucketInfo bucket)
        {
            if (!string.IsNullOrEmpty(bucket.ResetTime))
            {
                var newReset = FormatCountdown(bucket.ResetTime);
                if (bucket.ResetIn != newReset)
                {
                    bucket.ResetIn = newReset;
                    changed = true;
                }
            }
        }

        UpdateBucket(LastQuota.Gemini.Weekly);
        UpdateBucket(LastQuota.Gemini.FiveHour);
        UpdateBucket(LastQuota.ClaudeGpt.Weekly);
        UpdateBucket(LastQuota.ClaudeGpt.FiveHour);

        if (changed)
        {
            OnQuotaUpdated?.Invoke(LastQuota);
        }
    }

    public static string? FormatCountdown(string? resetTimeStr)
    {
        if (string.IsNullOrEmpty(resetTimeStr)) return null;
        if (!DateTime.TryParse(resetTimeStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var resetUtc))
        {
            return null;
        }

        var diff = resetUtc - DateTime.UtcNow;
        if (diff.TotalSeconds <= 0) return "0m";

        var days = (int)diff.TotalDays;
        var hours = diff.Hours;
        var mins = diff.Minutes;

        if (days > 0)
        {
            return $"{days}d {hours}h";
        }
        if (hours > 0)
        {
            return $"{hours}h {mins}m";
        }
        return $"{mins}m";
    }

    private static QuotaPayload ParseQuotaJson(string rawJson)
    {
        var payload = new QuotaPayload
        {
            UpdatedAt = DateTime.Now.ToString("HH:mm:ss"),
            Status = "ok"
        };

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("response", out var resp)) return payload;
        if (!resp.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return payload;

        JsonElement? geminiGroup = null;
        JsonElement? claudeGroup = null;

        foreach (var g in groups.EnumerateArray())
        {
            var name = g.TryGetProperty("displayName", out var dName) ? dName.GetString() ?? "" : "";
            if (name.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                geminiGroup = g;
            }
            else if (name.Contains("Claude", StringComparison.OrdinalIgnoreCase) || name.Contains("GPT", StringComparison.OrdinalIgnoreCase))
            {
                claudeGroup = g;
            }
        }

        void PopulateBucket(JsonElement? group, string windowId, QuotaBucketInfo target, bool omitResetIfFull = false)
        {
            if (!group.HasValue || !group.Value.TryGetProperty("buckets", out var buckets))
            {
                target.RemainingPercent = "Unavailable";
                return;
            }

            foreach (var b in buckets.EnumerateArray())
            {
                var bid = b.TryGetProperty("bucketId", out var idElem) ? idElem.GetString() ?? "" : "";
                var win = b.TryGetProperty("window", out var winElem) ? winElem.GetString() ?? "" : "";

                if (bid.Contains(windowId, StringComparison.OrdinalIgnoreCase) || win.Equals(windowId, StringComparison.OrdinalIgnoreCase))
                {
                    if (b.TryGetProperty("remainingFraction", out var frac))
                    {
                        var pct = (int)Math.Round(frac.GetDouble() * 100);
                        target.RemainingPercent = pct;

                        if (omitResetIfFull && pct >= 100)
                        {
                            target.ResetIn = null;
                            target.ResetTime = null;
                        }
                        else if (b.TryGetProperty("resetTime", out var rt))
                        {
                            target.ResetTime = rt.GetString();
                            target.ResetIn = FormatCountdown(target.ResetTime);
                        }
                    }
                    else
                    {
                        target.RemainingPercent = "Unavailable";
                    }
                    return;
                }
            }
            target.RemainingPercent = "Unavailable";
        }

        PopulateBucket(geminiGroup, "weekly", payload.Gemini.Weekly, omitResetIfFull: false);
        PopulateBucket(geminiGroup, "5h", payload.Gemini.FiveHour, omitResetIfFull: false);
        PopulateBucket(claudeGroup, "weekly", payload.ClaudeGpt.Weekly, omitResetIfFull: true);
        PopulateBucket(claudeGroup, "5h", payload.ClaudeGpt.FiveHour, omitResetIfFull: true);

        return payload;
    }
}
