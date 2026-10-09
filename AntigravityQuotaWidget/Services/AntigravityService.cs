using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using AntigravityQuotaWidget.Models;

namespace AntigravityQuotaWidget.Services;

public class AntigravityService
{
    private static readonly HttpClientHandler InsecureHandler = new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };

    private static readonly HttpClient Client = new(InsecureHandler)
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private int? _lastWorkingPort;
    private bool _lastWorkingIsHttps = false;
    private int? _lastPid;
    private string? _lastCsrfToken;

    #region Win32 TCP Table Interop
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwOutBufLen,
        bool sort,
        int ipVersion,
        TCP_TABLE_CLASS tblClass,
        uint reserved = 0);

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1;
        public byte localPort2;
        public byte localPort3;
        public byte localPort4;
        public uint remoteAddr;
        public byte remotePort1;
        public byte remotePort2;
        public byte remotePort3;
        public byte remotePort4;
        public uint owningPid;

        public ushort LocalPort => (ushort)((localPort1 << 8) + localPort2);
    }

    private static List<int> GetListeningPortsForPid(int pid)
    {
        var ports = new List<int>();
        int bufferSize = 0;
        const int AF_INET = 2; // IPv4

        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER);
        if (bufferSize <= 0) return ports;

        IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint ret = GetExtendedTcpTable(pTable, ref bufferSize, false, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER);
            if (ret == 0)
            {
                int numEntries = Marshal.ReadInt32(pTable);
                IntPtr rowPtr = IntPtr.Add(pTable, 4);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    if (row.owningPid == pid && row.state == 2) // MIB_TCP_STATE_LISTEN = 2
                    {
                        if (!ports.Contains(row.LocalPort))
                        {
                            ports.Add(row.LocalPort);
                        }
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading TCP table: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(pTable);
        }

        return ports;
    }
    #endregion

    public (int pid, string csrfToken) DiscoverProcess()
    {
        // 1. Fast path: check if cached process is still alive and valid
        if (_lastPid.HasValue && !string.IsNullOrEmpty(_lastCsrfToken))
        {
            try
            {
                var proc = Process.GetProcessById(_lastPid.Value);
                if (!proc.HasExited && proc.ProcessName.Contains("language_server", StringComparison.OrdinalIgnoreCase))
                {
                    return (_lastPid.Value, _lastCsrfToken);
                }
            }
            catch
            {
                _lastPid = null;
                _lastCsrfToken = null;
            }
        }

        // 2. Targeted search: Query specific language_server processes
        var candidates = Process.GetProcessesByName("language_server");
        if (candidates.Length == 0)
        {
            candidates = Process.GetProcesses()
                .Where(p => p.ProcessName.Contains("language_server", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var proc in candidates)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {proc.Id}"
                );
                foreach (var obj in searcher.Get())
                {
                    var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                    var match = Regex.Match(cmdLine, @"--csrf_token\s+([a-zA-Z0-9_-]+)");
                    if (match.Success)
                    {
                        _lastPid = proc.Id;
                        _lastCsrfToken = match.Groups[1].Value;
                        return (_lastPid.Value, _lastCsrfToken);
                    }
                }
            }
            catch { }
        }

        // 3. Fallback: Full WMI query if process name didn't match directly
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%'"
            );

            foreach (var obj in searcher.Get())
            {
                var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                var pidObj = obj["ProcessId"];
                if (pidObj == null) continue;

                int pid = Convert.ToInt32(pidObj);
                var match = Regex.Match(cmdLine, @"--csrf_token\s+([a-zA-Z0-9_-]+)");
                if (match.Success)
                {
                    _lastPid = pid;
                    _lastCsrfToken = match.Groups[1].Value;
                    return (pid, _lastCsrfToken);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WMI Process search error: {ex.Message}");
        }

        throw new InvalidOperationException("Antigravity 未运行或无法读取 language_server 进程。");
    }

    private List<int> GetCandidatePorts(int pid)
    {
        var ports = new HashSet<int>();

        // 1. If we had a last working port, prioritize it
        if (_lastWorkingPort.HasValue)
        {
            ports.Add(_lastWorkingPort.Value);
        }

        // 2. Query listening TCP sockets of language_server
        var tcpPorts = GetListeningPortsForPid(pid);
        foreach (var p in tcpPorts)
        {
            ports.Add(p);
        }

        // 3. Fallback: Parse from main.log
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Antigravity", "logs", "main.log"
            );

            if (File.Exists(logPath))
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                int? logPort = null;
                while ((line = reader.ReadLine()) != null)
                {
                    var m = Regex.Match(line, @"(?:Local:\s+https?://127\.0\.0\.1:|listening on https?://127\.0\.0\.1:)(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int lp))
                    {
                        logPort = lp;
                    }
                }
                if (logPort.HasValue)
                {
                    ports.Add(logPort.Value);
                    ports.Add(logPort.Value + 1); // 2009 https -> 2010 http
                }
            }
        }
        catch { }

        return ports.ToList();
    }

    public async Task<string> FetchRawQuotaAsync()
    {
        var (pid, token) = DiscoverProcess();

        // 1. Fast path: try cached working port directly (0ms overhead)
        if (_lastWorkingPort.HasValue)
        {
            var res = await TryRequestAsync(_lastWorkingPort.Value, token, _lastWorkingIsHttps);
            if (res != null) return res;

            // Try alternate protocol on same port
            var resAlt = await TryRequestAsync(_lastWorkingPort.Value, token, !_lastWorkingIsHttps);
            if (resAlt != null)
            {
                _lastWorkingIsHttps = !_lastWorkingIsHttps;
                return resAlt;
            }
        }

        // 2. Slow fallback: inspect candidate ports only if cached port failed
        var ports = GetCandidatePorts(pid);
        foreach (var port in ports)
        {
            if (port == _lastWorkingPort) continue; // Already tried

            var resHttp = await TryRequestAsync(port, token, false);
            if (resHttp != null)
            {
                _lastWorkingPort = port;
                _lastWorkingIsHttps = false;
                return resHttp;
            }

            var resHttps = await TryRequestAsync(port, token, true);
            if (resHttps != null)
            {
                _lastWorkingPort = port;
                _lastWorkingIsHttps = true;
                return resHttps;
            }
        }

        throw new HttpRequestException("探测所有端口均未能获取到 Antigravity 额度。");
    }

    private static async Task<string?> TryRequestAsync(int port, string token, bool isHttps)
    {
        try
        {
            var proto = isHttps ? "https" : "http";
            var url = $"{proto}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-codeium-csrf-token", token);
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            var resp = await Client.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync();
            }
        }
        catch
        {
            // Port not available or TLS error
        }
        return null;
    }

    #region Win32 Window Activation Interop
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public static void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(hWnd, SW_SHOW);
            }

            IntPtr fgWnd = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fgWnd, out _);
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);

            if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
            {
                AttachThreadInput(fgThread, targetThread, true);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                AttachThreadInput(fgThread, targetThread, false);
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[OPEN_CONV] ActivateWindow error: {ex.Message}");
        }
    }

    public static IntPtr FindAntigravityWindow()
    {
        try
        {
            var procs = Process.GetProcessesByName("Antigravity")
                .Concat(Process.GetProcessesByName("antigravity"))
                .ToList();

            if (procs.Count == 0) return IntPtr.Zero;

            var pids = procs.Select(p => p.Id).ToHashSet();

            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch { }
            }

            IntPtr foundHwnd = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pids.Contains((int)pid))
                {
                    var sb = new StringBuilder(256);
                    GetClassName(hWnd, sb, 256);
                    string cls = sb.ToString();
                    if (cls.Contains("Chrome_WidgetWin_1"))
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                    if (foundHwnd == IntPtr.Zero)
                    {
                        foundHwnd = hWnd;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (foundHwnd == IntPtr.Zero)
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();
                    if (title.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }

            return foundHwnd;
        }
        catch (Exception ex)
        {
            App.Log($"[OPEN_CONV] FindAntigravityWindow error: {ex.Message}");
            return IntPtr.Zero;
        }
    }
    #endregion

    public static string? GetLatestConversationId()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(userProfile, ".gemini", "antigravity", "conversation_summaries.db");
            if (!File.Exists(dbPath)) return null;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id FROM conversation_summaries ORDER BY rowid DESC LIMIT 1;";
            var res = cmd.ExecuteScalar();
            return res?.ToString();
        }
        catch (Exception ex)
        {
            App.Log($"[OPEN_CONV] GetLatestConversationId error: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> SendSetBrowserOpenConversationAsync(int port, string token, bool isHttps, string conversationId)
    {
        try
        {
            var proto = isHttps ? "https" : "http";
            var url = $"{proto}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/SetBrowserOpenConversation";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-codeium-csrf-token", token);
            var payload = JsonSerializer.Serialize(new { cascadeId = conversationId });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await Client.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> NavigateConversationViaCdpAsync(string conversationId)
    {
        try
        {
            // 1. Read DevToolsActivePort from %APPDATA%\Antigravity\DevToolsActivePort
            int? devToolsPort = null;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var portFile = Path.Combine(appData, "Antigravity", "DevToolsActivePort");
            if (File.Exists(portFile))
            {
                var lines = await File.ReadAllLinesAsync(portFile);
                if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out int p))
                {
                    devToolsPort = p;
                }
            }

            if (!devToolsPort.HasValue)
            {
                App.Log("[CDP_NAV] DevToolsActivePort file not found or invalid.");
                return false;
            }

            // 2. Fetch page target from /json/list
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var listJson = await http.GetStringAsync($"http://127.0.0.1:{devToolsPort.Value}/json/list");
            using var doc = JsonDocument.Parse(listJson);

            string? wsUrl = null;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.TryGetProperty("type", out var typeElem) && typeElem.GetString() == "page")
                {
                    if (elem.TryGetProperty("webSocketDebuggerUrl", out var wsElem))
                    {
                        var url = elem.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        if (url.Contains("127.0.0.1") || url.Contains("antigravity"))
                        {
                            wsUrl = wsElem.GetString();
                            break;
                        }
                        wsUrl ??= wsElem.GetString();
                    }
                }
            }

            if (string.IsNullOrEmpty(wsUrl))
            {
                App.Log("[CDP_NAV] No suitable page target found in /json/list.");
                return false;
            }

            // 3. Connect via ClientWebSocket and evaluate navigation script
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // First: Page.bringToFront to ensure Chromium prioritizes the window/tab
            try
            {
                var btfReq = new { id = 1, method = "Page.bringToFront" };
                var btfBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(btfReq));
                await ws.SendAsync(new ArraySegment<byte>(btfBytes), WebSocketMessageType.Text, true, cts.Token);
                var btfBuf = new byte[1024];
                await ws.ReceiveAsync(new ArraySegment<byte>(btfBuf), cts.Token);
            }
            catch { }

            // Second: Runtime.evaluate to switch conversation
            string script = $@"
(() => {{
  const targetId = '{conversationId}';
  // 1. If row exists in sidebar, click its anchor link
  const row = document.querySelector('[data-cascade-id=""' + targetId + '""]');
  if (row) {{
    const a = row.querySelector('a') || row;
    a.click();
    return 'clicked-row';
  }}
  // 2. Otherwise navigate via TanStack router in React fiber
  const anyElem = document.querySelector('[data-cascade-id]') || document.body.firstElementChild;
  if (anyElem) {{
    const fk = Object.keys(anyElem).find(k => k.startsWith('__reactFiber'));
    let curr = fk ? anyElem[fk] : null;
    let router = null;
    while (curr) {{
      if (curr.dependencies) {{
        let dep = curr.dependencies.firstContext;
        while (dep) {{
          if (dep.memoizedValue && typeof dep.memoizedValue.navigate === 'function') {{
            router = dep.memoizedValue;
            break;
          }}
          dep = dep.next;
        }}
      }}
      if (router) break;
      curr = curr.return;
    }}
    if (router) {{
      router.navigate({{ to: '/c/$cascadeId', params: {{ cascadeId: targetId }} }});
      return 'fiber-router';
    }}
  }}
  // 3. Fallback: window.location.href
  window.location.href = window.location.origin + '/c/' + targetId;
  return 'location-href';
}})()
";

            var req = new
            {
                id = 2,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = script,
                    returnByValue = true
                }
            };

            var reqBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req));
            await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, cts.Token);

            var buf = new byte[2048];
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
            var respText = Encoding.UTF8.GetString(buf, 0, res.Count);
            App.Log($"[CDP_NAV] Result: {respText}");

            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            catch { }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[CDP_NAV] Error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> OpenConversationAsync(string? conversationId, string? conversationTitle = null)
    {
        try
        {
            if (string.IsNullOrEmpty(conversationId))
            {
                conversationId = GetLatestConversationId();
            }

            App.Log($"[OPEN_CONV] Opening conversation: {conversationId} (Title: {conversationTitle ?? "N/A"})");

            // 1. Bring Antigravity window to front via Win32
            var hWnd = FindAntigravityWindow();
            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
            }

            // 2. Perform instant conversation navigation via CDP
            bool cdpSuccess = false;
            if (!string.IsNullOrEmpty(conversationId))
            {
                cdpSuccess = await NavigateConversationViaCdpAsync(conversationId);
            }

            // 3. Fallback: Notify language server via RPC if CDP was not available
            if (!cdpSuccess && !string.IsNullOrEmpty(conversationId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (pid, token) = DiscoverProcess();
                        if (_lastWorkingPort.HasValue)
                        {
                            await SendSetBrowserOpenConversationAsync(_lastWorkingPort.Value, token, _lastWorkingIsHttps, conversationId);
                        }
                        else
                        {
                            var ports = GetCandidatePorts(pid);
                            foreach (var port in ports)
                            {
                                if (await SendSetBrowserOpenConversationAsync(port, token, false, conversationId) ||
                                    await SendSetBrowserOpenConversationAsync(port, token, true, conversationId))
                                {
                                    _lastWorkingPort = port;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[OPEN_CONV] SetBrowserOpenConversation warning: {ex.Message}");
                    }
                });

                // Launch Antigravity protocol / deep link as fallback
                var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var exePath = Path.Combine(localApp, "Programs", "antigravity", "Antigravity.exe");
                string linkArg = $"antigravity://cascade/{conversationId}";

                try
                {
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = linkArg,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[OPEN_CONV] Process launch notice: {ex.Message}");
                }
            }

            // 4. Ensure window is in foreground after navigation
            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[OPEN_CONV] OpenConversationAsync error: {ex.Message}");
            return false;
        }
    }
}
