import { execSync } from 'node:child_process';
import http from 'node:http';
import https from 'node:https';

/**
 * 自动从 Windows 系统中发现运行中的 Antigravity language_server.exe 进程、
 * 提取 CSRF Token 并探测监听端口
 */
export function discoverAntigravity() {
  const psCmd = `Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server*' } | Select-Object ProcessId, CommandLine | ConvertTo-Json`;
  let output;
  try {
    output = execSync(`powershell -NoProfile -Command "${psCmd}"`, { encoding: 'utf-8' });
  } catch (err) {
    throw new Error('无法查询 Windows 进程列表，请确认 Antigravity 是否正在运行。');
  }

  let proc;
  try {
    const raw = JSON.parse(output);
    proc = Array.isArray(raw) ? raw[0] : raw;
  } catch (e) {
    throw new Error('未找到 Antigravity language_server.exe 进程，请先启动 Antigravity。');
  }

  if (!proc || !proc.CommandLine) {
    throw new Error('未找到 Antigravity language_server.exe 进程，请先启动 Antigravity。');
  }

  const tokenMatch = proc.CommandLine.match(/--csrf_token\s+([a-zA-Z0-9_-]+)/);
  if (!tokenMatch) {
    throw new Error('无法从 language_server.exe 命令行解析 CSRF Token。');
  }

  const pid = proc.ProcessId;
  const csrfToken = tokenMatch[1];

  // 查询该进程的所有本地监听端口
  const netCmd = `Get-NetTCPConnection -OwningProcess ${pid} -State Listen | Select-Object -ExpandProperty LocalPort`;
  const netOut = execSync(`powershell -NoProfile -Command "${netCmd}"`, { encoding: 'utf-8' });
  const ports = netOut
    .split(/\r?\n/)
    .map(s => parseInt(s.trim(), 10))
    .filter(p => !isNaN(p));

  return { pid, csrfToken, ports };
}

/**
 * 向本地 language_server 接口发送 POST 请求获取真实额度
 */
export async function queryQuota(port, csrfToken, isHttps = false) {
  const lib = isHttps ? https : http;
  const url = `${isHttps ? 'https' : 'http'}://127.0.0.1:${port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary`;

  return new Promise((resolve, reject) => {
    const req = lib.request(
      url,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'x-codeium-csrf-token': csrfToken
        },
        agent: isHttps ? new https.Agent({ rejectUnauthorized: false }) : undefined,
        timeout: 3000
      },
      (res) => {
        let body = '';
        res.on('data', chunk => body += chunk);
        res.on('end', () => {
          if (res.statusCode === 200) {
            try {
              resolve(JSON.parse(body));
            } catch (e) {
              reject(new Error(`解析响应 JSON 失败: ${e.message}`));
            }
          } else {
            reject(new Error(`HTTP 状态码: ${res.statusCode}`));
          }
        });
      }
    );

    req.on('error', reject);
    req.on('timeout', () => {
      req.destroy();
      reject(new Error('请求超时'));
    });

    req.write(JSON.stringify({}));
    req.end();
  });
}

/**
 * 自动尝试可用端口获取真实额度
 */
export async function getAntigravityQuota() {
  const { csrfToken, ports } = discoverAntigravity();

  for (const port of ports) {
    for (const isHttps of [false, true]) {
      try {
        const raw = await queryQuota(port, csrfToken, isHttps);
        return parseQuotaResponse(raw);
      } catch (err) {
        // 继续探测下一个端口/协议
      }
    }
  }

  throw new Error('探测了所有监听端口，未能连接到 Antigravity 额度服务。');
}

/**
 * 将 ISO 时间转换为类似 "5d 9h" 或 "3h 18m" 的倒计时
 */
export function formatCountdown(resetTimeStr) {
  if (!resetTimeStr) return null;
  const target = new Date(resetTimeStr).getTime();
  const now = Date.now();
  const diffMs = target - now;
  if (diffMs <= 0) return '0m';

  const diffSec = Math.floor(diffMs / 1000);
  const days = Math.floor(diffSec / 86400);
  const hours = Math.floor((diffSec % 86400) / 3600);
  const mins = Math.floor((diffSec % 3600) / 60);

  if (days > 0) {
    return `${days}d ${hours}h`;
  }
  if (hours > 0) {
    return `${hours}h ${mins}m`;
  }
  return `${mins}m`;
}

/**
 * 将原始响应解析为结构化 Quota 对象
 */
export function parseQuotaResponse(raw) {
  const groups = raw?.response?.groups || [];

  const geminiGroup = groups.find(g => /gemini/i.test(g.displayName || '')) || groups[0];
  const claudeGptGroup = groups.find(g => /claude|gpt/i.test(g.displayName || '')) || groups[1];

  const getBucket = (group, pattern) => {
    if (!group?.buckets) return null;
    return group.buckets.find(b =>
      pattern.test(b.bucketId || '') || pattern.test(b.displayName || '') || pattern.test(b.window || '')
    );
  };

  const geminiWeeklyBucket = getBucket(geminiGroup, /weekly/i);
  const gemini5hBucket = getBucket(geminiGroup, /5h|five.*hour/i);

  const claudeWeeklyBucket = getBucket(claudeGptGroup, /weekly/i);
  const claude5hBucket = getBucket(claudeGptGroup, /5h|five.*hour/i);

  const formatBucket = (b) => {
    if (!b) return { remainingPercent: 'Unavailable', resetIn: null, resetTime: null };
    const pct = b.remainingFraction !== undefined ? Math.round(b.remainingFraction * 100) : 'Unavailable';
    return {
      remainingPercent: pct,
      resetIn: formatCountdown(b.resetTime),
      resetTime: b.resetTime || null
    };
  };

  return {
    gemini: {
      weekly: formatBucket(geminiWeeklyBucket),
      fiveHour: formatBucket(gemini5hBucket)
    },
    claudeGpt: {
      weekly: formatBucket(claudeWeeklyBucket),
      fiveHour: formatBucket(claude5hBucket)
    },
    raw
  };
}

// 终端运行测试
async function main() {
  try {
    const quota = await getAntigravityQuota();

    console.log(`Gemini Weekly: ${quota.gemini.weekly.remainingPercent}%`);
    if (quota.gemini.weekly.remainingPercent < 100 && quota.gemini.weekly.resetIn) {
      console.log(`Reset: ${quota.gemini.weekly.resetIn}`);
    }
    console.log();

    console.log(`Gemini 5 Hour: ${quota.gemini.fiveHour.remainingPercent}%`);
    if (quota.gemini.fiveHour.remainingPercent < 100 && quota.gemini.fiveHour.resetIn) {
      console.log(`Reset: ${quota.gemini.fiveHour.resetIn}`);
    }
    console.log();

    console.log(`Claude/GPT Weekly: ${quota.claudeGpt.weekly.remainingPercent}%`);
    if (quota.claudeGpt.weekly.remainingPercent < 100 && quota.claudeGpt.weekly.resetIn) {
      console.log(`Reset: ${quota.claudeGpt.weekly.resetIn}`);
    }
    console.log();

    console.log(`Claude/GPT 5 Hour: ${quota.claudeGpt.fiveHour.remainingPercent}%`);
    if (quota.claudeGpt.fiveHour.remainingPercent < 100 && quota.claudeGpt.fiveHour.resetIn) {
      console.log(`Reset: ${quota.claudeGpt.fiveHour.resetIn}`);
    }
  } catch (err) {
    console.error('获取额度失败:', err.message);
    process.exit(1);
  }
}

if (process.argv[1] && process.argv[1].endsWith('check_quota.mjs')) {
  main();
}
