using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
}
