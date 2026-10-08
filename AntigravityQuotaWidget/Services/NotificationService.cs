using System.Windows.Forms;
using AntigravityQuotaWidget.Models;

namespace AntigravityQuotaWidget.Services;

public class NotificationService
{
    private readonly NotifyIcon _notifyIcon;
    private int _lastNotifiedThreshold = -1;
    private string? _lastNotifiedResetTime;
    private int? _previousGemini5hPct;
    private int? _previousWeeklyPct;

    public NotificationService(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public void CheckAndNotify(QuotaPayload currentQuota, WidgetSettings settings)
    {
        var fiveHourBucket = currentQuota.Gemini.FiveHour;
        if (fiveHourBucket.RemainingPercent is not int remainingPct) return;

        // Quota Restored to 100% notification (Item 6)
        if (settings.NotifyOnRestore)
        {
            if (_previousGemini5hPct.HasValue && _previousGemini5hPct.Value < 100 && remainingPct == 100)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "🎉 额度满血恢复！",
                    "Gemini 5-Hour 额度已恢复至 100%。",
                    ToolTipIcon.Info
                );
            }

            if (_previousWeeklyPct.HasValue && _previousWeeklyPct.Value < 100 && currentQuota.Gemini.Weekly.RemainingPercent is int weeklyPct && weeklyPct == 100)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "🎉 每周额度已重置！",
                    "Gemini Weekly 额度已重置至 100%。",
                    ToolTipIcon.Info
                );
            }
        }

        _previousGemini5hPct = remainingPct;
        if (currentQuota.Gemini.Weekly.RemainingPercent is int wPct)
        {
            _previousWeeklyPct = wPct;
        }

        if (settings.NotifyThreshold <= 0) return; // Low quota notification disabled

        var currentResetTime = fiveHourBucket.ResetTime;

        // If a new 5h cycle has started, reset the notification tracker
        if (currentResetTime != _lastNotifiedResetTime)
        {
            _lastNotifiedResetTime = currentResetTime;
            _lastNotifiedThreshold = -1;
        }

        // Check if remaining percentage has dropped below or reached threshold
        if (remainingPct <= settings.NotifyThreshold)
        {
            // Only notify once per threshold per cycle
            if (_lastNotifiedThreshold == -1 || remainingPct < _lastNotifiedThreshold)
            {
                _lastNotifiedThreshold = remainingPct;
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Antigravity 额度提醒",
                    $"Gemini 5-Hour 额度剩余：{remainingPct}% ({fiveHourBucket.ResetIn ?? "即将恢复"})",
                    ToolTipIcon.Warning
                );
            }
        }
    }
}
