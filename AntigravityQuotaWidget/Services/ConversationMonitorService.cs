using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using AntigravityQuotaWidget.Models;

namespace AntigravityQuotaWidget.Services;

public class ConversationMonitorService
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly DispatcherTimer _timer;
    private readonly object _lock = new();

    private FileSystemWatcher? _fileWatcher;
    private DateTime _lastFileChangeTime = DateTime.MinValue;
    private readonly Dictionary<string, TrackedConversation> _trackedConversations = new();
    private CompletedConversationInfo? _lastCompletedInfo;
    private DateTime _lastCompletedTime = DateTime.MinValue;

    public ConversationStatusPayload CurrentStatus { get; private set; } = new();
    public event Action<ConversationStatusPayload>? OnStatusChanged;
    public event Action<CompletedConversationInfo>? OnConversationCompleted;

    private class TrackedConversation
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
        public int StepCount { get; set; }
    }

    public ConversationMonitorService(SettingsService settingsService, NotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => CheckStatus();

        SetupFileWatcher();
    }

    public void Start()
    {
        _timer.Start();
        CheckStatus();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_fileWatcher != null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
    }

    private static string GetDbPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".gemini", "antigravity", "conversation_summaries.db");
    }

    private void SetupFileWatcher()
    {
        try
        {
            var dbPath = GetDbPath();
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _fileWatcher = new FileSystemWatcher(dir)
                {
                    Filter = "conversation_summaries.db*",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };

                _fileWatcher.Changed += OnFileChanged;
                _fileWatcher.Created += OnFileChanged;
                _fileWatcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CONV_MONITOR] FileWatcher init error: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFileChangeTime).TotalMilliseconds < 200)
        {
            return; // Debounce rapid writes
        }
        _lastFileChangeTime = now;

        // Dispatch check
        _timer.Dispatcher.BeginInvoke(CheckStatus);
    }

    private static bool IsAntigravityRunning()
    {
        try
        {
            return Process.GetProcessesByName("antigravity").Length > 0 ||
                   Process.GetProcessesByName("language_server").Length > 0;
        }
        catch
        {
            return true; // Assume true on process check failure
        }
    }

    public void CheckStatus()
    {
        if (!_settingsService.CurrentSettings.MonitorConversations)
        {
            if (CurrentStatus.IsBusy)
            {
                CurrentStatus = new ConversationStatusPayload();
                OnStatusChanged?.Invoke(CurrentStatus);
            }
            return;
        }

        lock (_lock)
        {
            try
            {
                var dbPath = GetDbPath();
                if (!File.Exists(dbPath)) return;

                bool isAppAlive = IsAntigravityRunning();
                var activeFromDb = new List<(string Id, string Title, int StepCount, string? LastUserInputTime)>();

                if (isAppAlive)
                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Shared
                    }.ToString();

                    using var conn = new SqliteConnection(connStr);
                    conn.Open();

                    using var cmdTimeout = conn.CreateCommand();
                    cmdTimeout.CommandText = "PRAGMA busy_timeout = 1000;";
                    cmdTimeout.ExecuteNonQuery();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT conversation_id, title, status, not_fully_idle, step_count, last_user_input_time 
                        FROM conversation_summaries 
                        WHERE not_fully_idle = 1 OR status = 'CASCADE_RUN_STATUS_RUNNING';";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        string title = reader.IsDBNull(1) ? "未命名对话" : reader.GetString(1);
                        int steps = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                        string? inputTime = reader.IsDBNull(5) ? null : reader.GetString(5);

                        if (!string.IsNullOrEmpty(id))
                        {
                            activeFromDb.Add((id, title, steps, inputTime));
                        }
                    }
                }

                var currentActiveIds = activeFromDb.Select(x => x.Id).ToHashSet();

                // 1. Detect newly active conversations
                foreach (var (id, title, steps, inputTime) in activeFromDb)
                {
                    if (!_trackedConversations.TryGetValue(id, out var tracked))
                    {
                        DateTime startUtc = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(inputTime) &&
                            DateTime.TryParse(inputTime, null, DateTimeStyles.AdjustToUniversal, out var parsedStart))
                        {
                            startUtc = parsedStart;
                        }

                        tracked = new TrackedConversation
                        {
                            Id = id,
                            Title = string.IsNullOrWhiteSpace(title) ? "当前对话" : title,
                            StartTimeUtc = startUtc,
                            FirstSeenUtc = DateTime.UtcNow,
                            StepCount = steps
                        };
                        _trackedConversations[id] = tracked;
                        App.Log($"[CONV_MONITOR] Conversation started: {id} - {title}");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(title)) tracked.Title = title;
                        tracked.StepCount = steps;
                    }
                }

                // 2. Detect completed conversations
                var finishedIds = _trackedConversations.Keys.Where(k => !currentActiveIds.Contains(k)).ToList();
                foreach (var fid in finishedIds)
                {
                    if (_trackedConversations.TryGetValue(fid, out var finished))
                    {
                        int duration = (int)(DateTime.UtcNow - finished.StartTimeUtc).TotalSeconds;
                        if (duration <= 0)
                        {
                            duration = (int)(DateTime.UtcNow - finished.FirstSeenUtc).TotalSeconds;
                        }
                        if (duration <= 0) duration = 1;

                        _lastCompletedInfo = new CompletedConversationInfo
                        {
                            Id = finished.Id,
                            Title = finished.Title,
                            DurationSeconds = duration,
                            CompletedAt = DateTime.Now.ToString("HH:mm:ss")
                        };
                        _lastCompletedTime = DateTime.UtcNow;

                        App.Log($"[CONV_MONITOR] Conversation completed: {finished.Id} - {finished.Title} in {duration}s");

                        // Trigger notifications
                        _notificationService.NotifyConversationCompleted(finished.Title, duration, _settingsService.CurrentSettings, finished.Id);
                        OnConversationCompleted?.Invoke(_lastCompletedInfo);

                        _trackedConversations.Remove(fid);
                    }
                }

                // 3. Prepare payload
                var activeList = new List<ActiveConversationInfo>();
                foreach (var tracked in _trackedConversations.Values)
                {
                    int duration = Math.Max(1, (int)(DateTime.UtcNow - tracked.StartTimeUtc).TotalSeconds);
                    activeList.Add(new ActiveConversationInfo
                    {
                        Id = tracked.Id,
                        Title = tracked.Title,
                        StepCount = tracked.StepCount,
                        DurationSeconds = duration,
                        StartedAt = tracked.StartTimeUtc.ToLocalTime().ToString("HH:mm:ss")
                    });
                }

                // Keep last completed info active for 10 seconds
                CompletedConversationInfo? recentCompleted = null;
                if (_lastCompletedInfo != null && (DateTime.UtcNow - _lastCompletedTime).TotalSeconds < 10)
                {
                    recentCompleted = _lastCompletedInfo;
                }
                else
                {
                    _lastCompletedInfo = null;
                }

                var payload = new ConversationStatusPayload
                {
                    IsBusy = activeList.Count > 0,
                    ActiveCount = activeList.Count,
                    ActiveConversations = activeList,
                    LastCompleted = recentCompleted
                };

                CurrentStatus = payload;
                OnStatusChanged?.Invoke(payload);
            }
            catch (Exception ex)
            {
                App.Log($"[CONV_MONITOR] CheckStatus error: {ex.Message}");
            }
        }
    }
}
