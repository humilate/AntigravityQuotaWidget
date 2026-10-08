// Antigravity Quota Widget Frontend Logic
const CIRCUMFERENCE = 87.96; // 2 * PI * 14

let currentQuota = null;
let currentSettings = {
  alwaysOnTop: true,
  refreshIntervalMinutes: 5,
  notifyThreshold: 20,
  startWithWindows: false,
  opacity: 0.95,
  theme: 'dark',
  isMiniMode: false,
  notifyOnRestore: true,
  snapToEdge: true,
  monitorConversations: true,
  notifyOnConversationComplete: true,
  soundOnConversationComplete: true
};

let isManualRefresh = false;
let isInitialLoad = true;
const activeAnimations = new Map();

const previousQuotas = {
  geminiWeekly: null,
  gemini5h: null,
  claudeWeekly: null,
  claude5h: null
};

// Main DOM Elements
const widgetContainer = document.getElementById('widgetContainer');

// Row Elements
const rowGeminiWeekly = document.getElementById('rowGeminiWeekly');
const rowGemini5h = document.getElementById('rowGemini5h');
const rowClaudeWeekly = document.getElementById('rowClaudeWeekly');
const rowClaude5h = document.getElementById('rowClaude5h');

// Gemini Elements
const geminiWeeklyPct = document.getElementById('geminiWeeklyPct');
const geminiWeeklyReset = document.getElementById('geminiWeeklyReset');
const geminiWeeklyRing = document.getElementById('geminiWeeklyRing');
const geminiWeeklyDelta = document.getElementById('geminiWeeklyDelta');

const gemini5hPct = document.getElementById('gemini5hPct');
const gemini5hReset = document.getElementById('gemini5hReset');
const gemini5hRing = document.getElementById('gemini5hRing');
const gemini5hDelta = document.getElementById('gemini5hDelta');

// Claude & GPT Elements
const claudeWeeklyPct = document.getElementById('claudeWeeklyPct');
const claudeWeeklyReset = document.getElementById('claudeWeeklyReset');
const claudeWeeklyRing = document.getElementById('claudeWeeklyRing');
const claudeWeeklyDelta = document.getElementById('claudeWeeklyDelta');

const claude5hPct = document.getElementById('claude5hPct');
const claude5hReset = document.getElementById('claude5hReset');
const claude5hRing = document.getElementById('claude5hRing');
const claude5hDelta = document.getElementById('claude5hDelta');

// Mini Capsule Elements
const btnMiniMode = document.getElementById('btnMiniMode');
const btnMiniExpand = document.getElementById('btnMiniExpand');
const miniGemini5h = document.getElementById('miniGemini5h');
const miniGeminiWeekly = document.getElementById('miniGeminiWeekly');
const mini5hVal = document.getElementById('mini5hVal');
const miniDot5h = document.getElementById('miniDot5h');
const miniWeeklyVal = document.getElementById('miniWeeklyVal');
const miniDotWeekly = document.getElementById('miniDotWeekly');

// Mini Conversation Elements
const miniConvDivider = document.getElementById('miniConvDivider');
const miniConvBadge = document.getElementById('miniConvBadge');
const miniConvPulse = document.getElementById('miniConvPulse');
const miniConvIcon = document.getElementById('miniConvIcon');
const miniConvTimer = document.getElementById('miniConvTimer');

// Card Mode Conversation Banner Elements
const convStatusBanner = document.getElementById('convStatusBanner');
const convStatusIconWrap = document.getElementById('convStatusIconWrap');
const convPulseRing = document.getElementById('convPulseRing');
const convStatusIcon = document.getElementById('convStatusIcon');
const convStatusTitle = document.getElementById('convStatusTitle');
const convStatusDesc = document.getElementById('convStatusDesc');
const convStatusBadge = document.getElementById('convStatusBadge');

// Status & Action Elements
const statusDot = document.getElementById('statusDot');
const updatedText = document.getElementById('updatedText');
const btnRefresh = document.getElementById('btnRefresh');
const btnTheme = document.getElementById('btnTheme');
const btnPinTopRight = document.getElementById('btnPinTopRight');
const btnHide = document.getElementById('btnHide');

// Settings Elements
const settingsOverlay = document.getElementById('settingsOverlay');
const btnSettings = document.getElementById('btnSettings');
const btnCloseSettings = document.getElementById('btnCloseSettings');
const btnSaveSettings = document.getElementById('btnSaveSettings');
const chkAlwaysOnTop = document.getElementById('chkAlwaysOnTop');
const chkStartWithWindows = document.getElementById('chkStartWithWindows');
const chkNotifyRestore = document.getElementById('chkNotifyRestore');
const chkSnapToEdge = document.getElementById('chkSnapToEdge');
const chkMonitorConv = document.getElementById('chkMonitorConv');
const chkNotifyConvComplete = document.getElementById('chkNotifyConvComplete');
const chkSoundConvComplete = document.getElementById('chkSoundConvComplete');
const rngOpacity = document.getElementById('rngOpacity');
const opacityVal = document.getElementById('opacityVal');
const refreshChips = document.getElementById('refreshChips');
const notifyChips = document.getElementById('notifyChips');


// Send command to C# Host
function sendHostMessage(msg) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(msg);
  } else {
    console.log("Mock host message:", msg);
  }
}

// Theme Management
function applyTheme(theme) {
  currentSettings.theme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', currentSettings.theme);
  try {
    localStorage.setItem('antigravity_theme', currentSettings.theme);
  } catch { }

  if (btnTheme) {
    btnTheme.setAttribute('title', currentSettings.theme === 'light' ? '切换为深色主题' : '切换为浅色主题');
  }
}

// Apply cached theme immediately to prevent flashing
try {
  const cachedTheme = localStorage.getItem('antigravity_theme');
  if (cachedTheme) {
    applyTheme(cachedTheme);
  }
} catch { }

if (btnTheme) {
  btnTheme.addEventListener('click', () => {
    const nextTheme = currentSettings.theme === 'light' ? 'dark' : 'light';
    applyTheme(nextTheme);
    sendHostMessage({
      action: 'saveSettings',
      settings: currentSettings
    });
  });
}

// Mini / Capsule Mode Management (Item 1)
function applyMiniMode(isMini) {
  if (isMini) {
    widgetContainer.classList.add('mode-mini');
  } else {
    widgetContainer.classList.remove('mode-mini');
  }
}

let lastMiniToggleTime = 0;
function toggleMiniMode() {
  const now = Date.now();
  if (now - lastMiniToggleTime < 350) return;
  lastMiniToggleTime = now;
  currentSettings.isMiniMode = !currentSettings.isMiniMode;
  applyMiniMode(currentSettings.isMiniMode);
  sendHostMessage({
    action: 'toggleMiniMode'
  });
}

if (btnMiniMode) btnMiniMode.addEventListener('click', toggleMiniMode);
if (btnMiniExpand) btnMiniExpand.addEventListener('click', toggleMiniMode);

// Smart Window Dragging & Reliable Double-Click Detection (Item 1)
let isMouseDown = false;
let startScreenX = 0;
let startScreenY = 0;
let hasSentDrag = false;
let lastClickTimestamp = 0;

document.addEventListener('mousedown', (e) => {
  if (e.button !== 0) return;
  if (e.target.closest('.no-drag') || e.target.closest('button') || e.target.closest('input') || e.target.closest('.switch') || e.target.closest('.chip') || e.target.closest('.settings-overlay')) return;

  const now = Date.now();
  if (now - lastClickTimestamp < 400) {
    // Double click detected!
    lastClickTimestamp = 0;
    isMouseDown = false;
    hasSentDrag = false;
    toggleMiniMode();
    return;
  }
  lastClickTimestamp = now;

  isMouseDown = true;
  hasSentDrag = false;
  startScreenX = e.screenX;
  startScreenY = e.screenY;
});

document.addEventListener('mousemove', (e) => {
  if (!isMouseDown || hasSentDrag) return;
  const dist = Math.hypot(e.screenX - startScreenX, e.screenY - startScreenY);
  if (dist > 4) {
    hasSentDrag = true;
    lastClickTimestamp = 0; // Prevent drag release from being counted as a double-click
    sendHostMessage({ action: 'drag' });
  }
});

document.addEventListener('mouseup', () => {
  isMouseDown = false;
  hasSentDrag = false;
});

document.addEventListener('dblclick', (e) => {
  if (e.button !== 0) return;
  if (e.target.closest('.no-drag') || e.target.closest('button') || e.target.closest('input') || e.target.closest('.switch') || e.target.closest('.chip') || e.target.closest('.settings-overlay')) return;
  toggleMiniMode();
});

// Opacity Slider Real-Time Adjustment (Item 2)
if (rngOpacity) {
  rngOpacity.addEventListener('input', (e) => {
    const val = parseInt(e.target.value, 10);
    if (opacityVal) opacityVal.textContent = `${val}%`;
    const newOpacity = val / 100;
    currentSettings.opacity = newOpacity;
    // Real-time Win32 layered opacity adjustment
    sendHostMessage({
      action: 'setOpacity',
      opacity: newOpacity
    });
  });
}

// Counter Number Animation (0% -> target%)
function animateCounter(elem, start, end, duration) {
  if (activeAnimations.has(elem)) {
    cancelAnimationFrame(activeAnimations.get(elem));
    activeAnimations.delete(elem);
  }

  if (isNaN(end)) {
    elem.textContent = 'Unavailable';
    return;
  }

  if (start === end) {
    elem.textContent = `${end}%`;
    return;
  }

  const startTime = performance.now();

  function step(now) {
    const elapsed = now - startTime;
    const progress = Math.min(elapsed / duration, 1);
    // Ease-out cubic deceleration
    const ease = 1 - Math.pow(1 - progress, 3);
    const val = Math.round(start + (end - start) * ease);
    elem.textContent = `${val}%`;

    if (progress < 1) {
      const id = requestAnimationFrame(step);
      activeAnimations.set(elem, id);
    } else {
      elem.textContent = `${end}%`;
      activeAnimations.delete(elem);
    }
  }

  const id = requestAnimationFrame(step);
  activeAnimations.set(elem, id);
}

// Status Color Helper
function getDotColorClass(pct) {
  if (pct < 10) return 'status-critical';
  if (pct < 30) return 'status-low';
  if (pct < 70) return 'status-notice';
  return 'status-normal';
}

// Exact Absolute Time Formatter (Item 5)
function formatExactTime(resetTimeStr) {
  if (!resetTimeStr) return { short: '', full: '' };
  try {
    const d = new Date(resetTimeStr);
    if (isNaN(d.getTime())) return { short: '', full: '' };
    const now = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    const hours = pad(d.getHours());
    const mins = pad(d.getMinutes());

    const isToday = d.toDateString() === now.toDateString();
    const tomorrow = new Date(now);
    tomorrow.setDate(tomorrow.getDate() + 1);
    const isTomorrow = d.toDateString() === tomorrow.toDateString();

    let dayTextShort;
    let dayTextFull;

    if (isToday) {
      dayTextShort = `${hours}:${mins}`;
      dayTextFull = `今天 ${hours}:${mins} 恢复满额`;
    } else if (isTomorrow) {
      dayTextShort = `明天 ${hours}:${mins}`;
      dayTextFull = `明天 ${hours}:${mins} 恢复满额`;
    } else {
      const month = d.getMonth() + 1;
      const day = d.getDate();
      dayTextShort = `${month}/${day} ${hours}:${mins}`;
      dayTextFull = `${month}月${day}日 ${hours}:${mins} 恢复满额`;
    }
    return { short: dayTextShort, full: dayTextFull };
  } catch {
    return { short: '', full: '' };
  }
}

// Delta Badge Calculation & Display (Item 7)
function updateDeltaBadge(deltaElem, currentPct, prevPct) {
  if (!deltaElem) return;
  if (prevPct === null || currentPct === null || isNaN(currentPct) || isNaN(prevPct)) {
    deltaElem.className = 'quota-delta';
    deltaElem.textContent = '';
    return;
  }
  const diff = currentPct - prevPct;
  if (diff < 0) {
    deltaElem.className = 'quota-delta down';
    deltaElem.textContent = `↓ ${diff}%`;
  } else if (diff > 0) {
    deltaElem.className = 'quota-delta up';
    deltaElem.textContent = `↑ +${diff}%`;
  } else {
    deltaElem.className = 'quota-delta';
    deltaElem.textContent = '';
  }
}

// Update Bucket UI with smooth ring sweep, counter animation, and direct exact time badge
function updateBucket(rowElem, pctElem, resetElem, ringElem, bucket, modelName, isClaudeGroup = false, animateFromZero = false) {
  if (!bucket || bucket.remainingPercent === 'Unavailable') {
    pctElem.textContent = 'Unavailable';
    pctElem.className = 'quota-percent';
    resetElem.textContent = '';
    if (rowElem) rowElem.removeAttribute('title');
    ringElem.style.strokeDashoffset = CIRCUMFERENCE;
    ringElem.className = 'ring-fg';
    return;
  }

  const pct = parseInt(bucket.remainingPercent, 10);
  const colorClass = getDotColorClass(pct);

  pctElem.className = `quota-percent ${colorClass}`;
  ringElem.setAttribute('class', `ring-fg ${colorClass}`);

  const targetOffset = CIRCUMFERENCE * (1 - Math.max(0, Math.min(100, pct)) / 100);

  if (animateFromZero) {
    ringElem.style.transition = 'none';
    ringElem.style.strokeDashoffset = CIRCUMFERENCE;
    pctElem.textContent = '0%';
    void ringElem.getBoundingClientRect();

    requestAnimationFrame(() => {
      ringElem.style.transition = 'stroke-dashoffset 0.85s cubic-bezier(0.16, 1, 0.3, 1)';
      ringElem.style.strokeDashoffset = targetOffset;
      animateCounter(pctElem, 0, pct, 850);
    });
  } else {
    ringElem.style.transition = 'stroke-dashoffset 0.6s cubic-bezier(0.4, 0, 0.2, 1)';
    ringElem.style.strokeDashoffset = targetOffset;
    pctElem.textContent = `${pct}%`;
  }

  // Exact Reset Time
  const exact = formatExactTime(bucket.resetTime);

  if (pct >= 100 && (!bucket.resetIn || isClaudeGroup)) {
    resetElem.innerHTML = '<span style="color:var(--color-normal)">额度充足 100%</span>';
    if (rowElem) rowElem.title = `${modelName}: 100% (当前额度充足)`;
  } else if (bucket.resetIn) {
    if (exact.short) {
      resetElem.innerHTML = `Reset in ${bucket.resetIn} <span class="exact-time-badge">(${exact.short})</span>`;
    } else {
      resetElem.textContent = `Reset in ${bucket.resetIn}`;
    }
    const tip = exact.full
      ? `${modelName}: ${pct}% | ${exact.full} (剩余倒计时: ${bucket.resetIn})`
      : `${modelName}: ${pct}% | 剩余倒计时: ${bucket.resetIn}`;
    if (rowElem) rowElem.title = tip;
    resetElem.title = exact.full || `倒计时: ${bucket.resetIn}`;
  } else {
    resetElem.textContent = '';
    if (rowElem) rowElem.removeAttribute('title');
  }
}

// Update Quota View
function renderQuota(data, animateFromZero = false) {
  currentQuota = data;

  const gWeekly = data.gemini?.weekly?.remainingPercent != null ? parseInt(data.gemini.weekly.remainingPercent, 10) : null;
  const g5h = data.gemini?.fiveHour?.remainingPercent != null ? parseInt(data.gemini.fiveHour.remainingPercent, 10) : null;
  const cWeekly = data.claudeGpt?.weekly?.remainingPercent != null ? parseInt(data.claudeGpt.weekly.remainingPercent, 10) : null;
  const c5h = data.claudeGpt?.fiveHour?.remainingPercent != null ? parseInt(data.claudeGpt.fiveHour.remainingPercent, 10) : null;

  // Update delta badges (Item 7)
  updateDeltaBadge(geminiWeeklyDelta, gWeekly, previousQuotas.geminiWeekly);
  updateDeltaBadge(gemini5hDelta, g5h, previousQuotas.gemini5h);
  updateDeltaBadge(claudeWeeklyDelta, cWeekly, previousQuotas.claudeWeekly);
  updateDeltaBadge(claude5hDelta, c5h, previousQuotas.claude5h);

  previousQuotas.geminiWeekly = gWeekly;
  previousQuotas.gemini5h = g5h;
  previousQuotas.claudeWeekly = cWeekly;
  previousQuotas.claude5h = c5h;

  // Update Mini Capsule (Item 1 & Item 5)
  if (g5h !== null && !isNaN(g5h)) {
    mini5hVal.textContent = `${g5h}%`;
    miniDot5h.className = `mini-dot ${getDotColorClass(g5h)}`;
  } else {
    mini5hVal.textContent = '--%';
  }
  if (gWeekly !== null && !isNaN(gWeekly)) {
    miniWeeklyVal.textContent = `${gWeekly}%`;
    miniDotWeekly.className = `mini-dot ${getDotColorClass(gWeekly)}`;
  } else {
    miniWeeklyVal.textContent = '--%';
  }

  if (miniGemini5h) {
    const exact5h = formatExactTime(data.gemini?.fiveHour?.resetTime);
    miniGemini5h.title = `Gemini 5-Hour: ${g5h ?? '--'}%` + (exact5h.full ? ` | ${exact5h.full}` : '') + ` (剩余: ${data.gemini?.fiveHour?.resetIn || '即将恢复'})`;
  }
  if (miniGeminiWeekly) {
    const exactWeekly = formatExactTime(data.gemini?.weekly?.resetTime);
    miniGeminiWeekly.title = `Gemini Weekly: ${gWeekly ?? '--'}%` + (exactWeekly.full ? ` | ${exactWeekly.full}` : '') + ` (剩余: ${data.gemini?.weekly?.resetIn || '即将恢复'})`;
  }

  // Update full cards with row tooltips and exact time badges
  updateBucket(rowGeminiWeekly, geminiWeeklyPct, geminiWeeklyReset, geminiWeeklyRing, data.gemini?.weekly, 'Gemini Weekly', false, animateFromZero);
  updateBucket(rowGemini5h, gemini5hPct, gemini5hReset, gemini5hRing, data.gemini?.fiveHour, 'Gemini 5-Hour', false, animateFromZero);
  updateBucket(rowClaudeWeekly, claudeWeeklyPct, claudeWeeklyReset, claudeWeeklyRing, data.claudeGpt?.weekly, 'Claude & GPT Weekly', true, animateFromZero);
  updateBucket(rowClaude5h, claude5hPct, claude5hReset, claude5hRing, data.claudeGpt?.fiveHour, 'Claude & GPT 5-Hour', true, animateFromZero);

  // Status & Updated Timestamp
  btnRefresh.classList.remove('spinning');
  if (data.status === 'ok') {
    statusDot.className = 'status-dot ok';
    updatedText.textContent = `Updated ${data.updatedAt || ''}`;
  } else {
    statusDot.className = 'status-dot error';
    updatedText.textContent = data.errorMessage || 'Connection failed';
  }
}

// Recalculate local countdowns
function updateLocalCountdowns() {
  if (!currentQuota) return;

  function calcDiff(resetTimeStr) {
    if (!resetTimeStr) return null;
    const target = new Date(resetTimeStr).getTime();
    const now = Date.now();
    const diffMs = target - now;
    if (diffMs <= 0) return '即将恢复';

    const sec = Math.floor(diffMs / 1000);
    const d = Math.floor(sec / 86400);
    const h = Math.floor((sec % 86400) / 3600);
    const m = Math.floor((sec % 3600) / 60);

    if (d > 0) return `${d}d ${h}h`;
    if (h > 0) return `${h}h ${m}m`;
    return `${m}m`;
  }

  // Gemini Weekly
  if (currentQuota.gemini?.weekly?.resetTime) {
    const r = calcDiff(currentQuota.gemini.weekly.resetTime);
    const exact = formatExactTime(currentQuota.gemini.weekly.resetTime);
    if (r) {
      geminiWeeklyReset.innerHTML = exact.short
        ? `Reset in ${r} <span class="exact-time-badge">(${exact.short})</span>`
        : `Reset in ${r}`;
      if (miniGeminiWeekly) {
        const gWeekly = currentQuota.gemini.weekly.remainingPercent;
        miniGeminiWeekly.title = `Gemini Weekly: ${gWeekly ?? '--'}%` + (exact.full ? ` | ${exact.full}` : '') + ` (剩余: ${r})`;
      }
    }
  }

  // Gemini 5-Hour
  if (currentQuota.gemini?.fiveHour?.resetTime) {
    const r = calcDiff(currentQuota.gemini.fiveHour.resetTime);
    const exact = formatExactTime(currentQuota.gemini.fiveHour.resetTime);
    if (r) {
      gemini5hReset.innerHTML = exact.short
        ? `Reset in ${r} <span class="exact-time-badge">(${exact.short})</span>`
        : `Reset in ${r}`;
      if (miniGemini5h) {
        const g5h = currentQuota.gemini.fiveHour.remainingPercent;
        miniGemini5h.title = `Gemini 5-Hour: ${g5h ?? '--'}%` + (exact.full ? ` | ${exact.full}` : '') + ` (剩余: ${r})`;
      }
    }
  }

  // Claude & GPT Weekly (only if reset countdown is active)
  if (currentQuota.claudeGpt?.weekly?.resetTime && currentQuota.claudeGpt.weekly.remainingPercent < 100) {
    const r = calcDiff(currentQuota.claudeGpt.weekly.resetTime);
    const exact = formatExactTime(currentQuota.claudeGpt.weekly.resetTime);
    if (r) {
      claudeWeeklyReset.innerHTML = exact.short
        ? `Reset in ${r} <span class="exact-time-badge">(${exact.short})</span>`
        : `Reset in ${r}`;
    }
  }

  // Claude & GPT 5-Hour (only if reset countdown is active)
  if (currentQuota.claudeGpt?.fiveHour?.resetTime && currentQuota.claudeGpt.fiveHour.remainingPercent < 100) {
    const r = calcDiff(currentQuota.claudeGpt.fiveHour.resetTime);
    const exact = formatExactTime(currentQuota.claudeGpt.fiveHour.resetTime);
    if (r) {
      claude5hReset.innerHTML = exact.short
        ? `Reset in ${r} <span class="exact-time-badge">(${exact.short})</span>`
        : `Reset in ${r}`;
    }
  }
}

setInterval(updateLocalCountdowns, 30000);

// Host message receiver
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg) return;

    if (msg.type === 'manualRefreshTriggered') {
      isManualRefresh = true;
      btnRefresh.classList.add('spinning');
      updatedText.textContent = '刷新中...';
      [geminiWeeklyRing, gemini5hRing, claudeWeeklyRing, claude5hRing].forEach(ring => {
        ring.style.transition = 'stroke-dashoffset 0.35s ease-out';
        ring.style.strokeDashoffset = CIRCUMFERENCE;
      });
    } else if (msg.type === 'quotaUpdate') {
      const fromZero = isManualRefresh || isInitialLoad;
      isManualRefresh = false;
      isInitialLoad = false;
      renderQuota(msg.payload, fromZero);
    } else if (msg.type === 'settings') {
      currentSettings = msg.payload;
      syncSettingsUI();
    } else if (msg.type === 'conversationStatusUpdate') {
      renderConversationStatus(msg.payload);
    }
  });
}

function formatSeconds(sec) {
  if (sec < 60) return `${sec}s`;
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return `${m}:${s.toString().padStart(2, '0')}`;
}

let lastCompletedTimer = null;

function renderConversationStatus(status) {
  if (!status) return;

  const isMonitored = currentSettings.monitorConversations !== false;

  if (isMonitored && status.isBusy && status.activeConversations && status.activeConversations.length > 0) {
    if (lastCompletedTimer) {
      clearTimeout(lastCompletedTimer);
      lastCompletedTimer = null;
    }

    const top = status.activeConversations[0];
    const durStr = formatSeconds(top.durationSeconds || 1);
    const titleStr = top.title || '当前对话';

    // Mini Capsule Mode
    if (miniConvDivider) miniConvDivider.style.display = 'block';
    if (miniConvBadge) {
      miniConvBadge.style.display = 'flex';
      miniConvBadge.className = 'mini-conv-badge busy';
      miniConvBadge.title = `正在进行中: 「${titleStr}」(已耗时 ${durStr})`;
    }
    if (miniConvIcon) miniConvIcon.textContent = '⚡';
    if (miniConvTimer) miniConvTimer.textContent = durStr;

    // Full Card Mode
    if (convStatusBanner) {
      convStatusBanner.className = 'conv-status-banner busy';
      convStatusBanner.title = `正在生成: ${titleStr}`;
    }
    if (convStatusIcon) convStatusIcon.textContent = '⚡';
    if (convStatusTitle) convStatusTitle.textContent = `进行中: ${titleStr}`;
    if (convStatusDesc) convStatusDesc.textContent = `已耗时 ${durStr} · 正在思考与生成...`;
    if (convStatusBadge) convStatusBadge.textContent = durStr;

  } else if (isMonitored && status.lastCompleted) {
    const durStr = formatSeconds(status.lastCompleted.durationSeconds || 1);
    const titleStr = status.lastCompleted.title || '当前对话';

    // Mini Capsule Mode
    if (miniConvDivider) miniConvDivider.style.display = 'block';
    if (miniConvBadge) {
      miniConvBadge.style.display = 'flex';
      miniConvBadge.className = 'mini-conv-badge completed';
      miniConvBadge.title = `「${titleStr}」已生成完成 (耗时 ${durStr})`;
    }
    if (miniConvIcon) miniConvIcon.textContent = '✓';
    if (miniConvTimer) miniConvTimer.textContent = `完成 ${durStr}`;

    // Full Card Mode
    if (convStatusBanner) {
      convStatusBanner.className = 'conv-status-banner completed';
      convStatusBanner.title = `「${titleStr}」已生成完成`;
    }
    if (convStatusIcon) convStatusIcon.textContent = '🎉';
    if (convStatusTitle) convStatusTitle.textContent = '🎉 对话生成完毕！';
    if (convStatusDesc) convStatusDesc.textContent = `「${titleStr}」耗时 ${durStr}`;
    if (convStatusBadge) convStatusBadge.textContent = '✓ 完成';

  } else {
    // Idle Mode
    if (miniConvDivider) miniConvDivider.style.display = 'none';
    if (miniConvBadge) {
      miniConvBadge.style.display = 'none';
      miniConvBadge.className = 'mini-conv-badge';
    }

    if (convStatusBanner) {
      convStatusBanner.className = 'conv-status-banner idle';
      convStatusBanner.title = 'AI 对话监控就绪，随时响应';
    }
    if (convStatusIcon) convStatusIcon.textContent = '🧠';
    if (convStatusTitle) convStatusTitle.textContent = '所有对话已就绪';
    if (convStatusDesc) convStatusDesc.textContent = '等待新指令 · 监控中';
    if (convStatusBadge) convStatusBadge.textContent = '就绪';
  }
}

// Action Buttons
function triggerManualRefresh() {
  isManualRefresh = true;
  btnRefresh.classList.add('spinning');
  updatedText.textContent = '刷新中...';

  // Smooth collapse to 0 while waiting for refresh response
  [geminiWeeklyRing, gemini5hRing, claudeWeeklyRing, claude5hRing].forEach(ring => {
    ring.style.transition = 'stroke-dashoffset 0.35s ease-out';
    ring.style.strokeDashoffset = CIRCUMFERENCE;
  });

  sendHostMessage({ action: 'refresh' });
}

btnRefresh.addEventListener('click', triggerManualRefresh);

btnPinTopRight.addEventListener('click', () => {
  sendHostMessage({ action: 'pinTopRight' });
});

btnHide.addEventListener('click', () => {
  sendHostMessage({ action: 'closeToTray' });
});

// Settings Overlay Management
btnSettings.addEventListener('click', () => {
  syncSettingsUI();
  settingsOverlay.classList.add('show');
  sendHostMessage({ action: 'settingsOpened' });
});

btnCloseSettings.addEventListener('click', () => {
  settingsOverlay.classList.remove('show');
  sendHostMessage({ action: 'settingsClosed' });
});

function syncSettingsUI() {
  chkAlwaysOnTop.checked = currentSettings.alwaysOnTop;
  chkStartWithWindows.checked = currentSettings.startWithWindows;
  if (chkNotifyRestore) chkNotifyRestore.checked = currentSettings.notifyOnRestore ?? true;
  if (chkSnapToEdge) chkSnapToEdge.checked = currentSettings.snapToEdge ?? true;
  if (chkMonitorConv) chkMonitorConv.checked = currentSettings.monitorConversations ?? true;
  if (chkNotifyConvComplete) chkNotifyConvComplete.checked = currentSettings.notifyOnConversationComplete ?? true;
  if (chkSoundConvComplete) chkSoundConvComplete.checked = currentSettings.soundOnConversationComplete ?? true;
  
  if (rngOpacity) {
    const pct = Math.round((currentSettings.opacity || 0.95) * 100);
    rngOpacity.value = pct;
    if (opacityVal) opacityVal.textContent = `${pct}%`;
  }

  if (currentSettings.theme) {
    applyTheme(currentSettings.theme);
  }

  applyMiniMode(currentSettings.isMiniMode);

  // Refresh interval chips
  Array.from(refreshChips.children).forEach(chip => {
    const val = parseInt(chip.getAttribute('data-val'), 10);
    chip.classList.toggle('active', val === currentSettings.refreshIntervalMinutes);
  });

  // Notify threshold chips
  Array.from(notifyChips.children).forEach(chip => {
    const val = parseInt(chip.getAttribute('data-val'), 10);
    chip.classList.toggle('active', val === currentSettings.notifyThreshold);
  });
}

refreshChips.addEventListener('click', (e) => {
  const chip = e.target.closest('.chip');
  if (!chip) return;
  const val = parseInt(chip.getAttribute('data-val'), 10);
  currentSettings.refreshIntervalMinutes = val;
  syncSettingsUI();
});

notifyChips.addEventListener('click', (e) => {
  const chip = e.target.closest('.chip');
  if (!chip) return;
  const val = parseInt(chip.getAttribute('data-val'), 10);
  currentSettings.notifyThreshold = val;
  syncSettingsUI();
});

btnSaveSettings.addEventListener('click', () => {
  currentSettings.alwaysOnTop = chkAlwaysOnTop.checked;
  currentSettings.startWithWindows = chkStartWithWindows.checked;
  if (chkNotifyRestore) currentSettings.notifyOnRestore = chkNotifyRestore.checked;
  if (chkSnapToEdge) currentSettings.snapToEdge = chkSnapToEdge.checked;
  if (chkMonitorConv) currentSettings.monitorConversations = chkMonitorConv.checked;
  if (chkNotifyConvComplete) currentSettings.notifyOnConversationComplete = chkNotifyConvComplete.checked;
  if (chkSoundConvComplete) currentSettings.soundOnConversationComplete = chkSoundConvComplete.checked;
  if (rngOpacity) currentSettings.opacity = parseInt(rngOpacity.value, 10) / 100;

  sendHostMessage({
    action: 'saveSettings',
    settings: currentSettings
  });
  sendHostMessage({ action: 'settingsClosed' });
  settingsOverlay.classList.remove('show');
});


// Request initial settings on load
window.addEventListener('DOMContentLoaded', () => {
  sendHostMessage({ action: 'ready' });
});
