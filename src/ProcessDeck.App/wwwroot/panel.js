'use strict';

/* ==========================================================================
   ProcessDeck 面板
   --------------------------------------------------------------------------
   与主程序之间走 WebView2 的 postMessage 通道（进程内消息，不开放任何网络端口）。
   面板只负责渲染与手势，所有进程/端口/文件操作都在 Host 侧完成。

   渲染策略：**按 id 增量协调，而不是每次全量重建 DOM**。
   快照每 250ms 就会推一次，如果每次都重建：
     * 拖拽排序会在半途被打断（元素被替换掉了）；
     * 日志区的滚动位置每 250ms 归位一次，根本没法看；
     * 按钮的 hover / focus 状态会不停闪烁。
   ========================================================================== */

const bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
const MAX_LOG_LINES = 14;

const board = document.getElementById('board');
const summary = document.getElementById('summary');
const engineStateEl = document.getElementById('engine-state');
const portWatchEl = document.getElementById('port-watch');
const viewportEl = document.getElementById('viewport');
const cardHost = document.getElementById('card-host');
const cardFrame = document.getElementById('card-frame');
const cardPicker = document.getElementById('card-picker');

const STATE_LABEL = {
  stopped: '已停止',
  starting: '启动中',
  running: '运行中',
  stopping: '停止中',
  failed: '失败',
};

const STOP_STAGE_LABEL = {
  StopCommand: '优雅停止命令',
  Interrupt: 'Ctrl+C',
  ForceKill: '强制回收',
  AlreadyStopped: '本就未运行',
};

let snapshot = null;
let cardOrder = [];
const cardElements = new Map();
let draggingId = null;

/* ---------------- 诊断读数 ---------------- */

function updateViewport() {
  viewportEl.textContent =
    `视口 ${window.innerWidth}x${window.innerHeight} @${window.devicePixelRatio}x`;
}

/* ---------------- 与 Host 通信 ---------------- */

function send(message) {
  if (bridge) {
    bridge.postMessage(message);
  }
}

function handleHostMessage(event) {
  const message = event.data;

  if (!message || typeof message !== 'object') {
    return;
  }

  if (message.type === 'snapshot') {
    applySnapshot(message.data);
  } else if (message.type === 'notice') {
    showToast(message.message, message.level);
  }
}

function applySnapshot(data) {
  if (!data) {
    return;
  }

  snapshot = data;

  // 布局里保存的顺序优先，其余按配置顺序补齐。
  const byId = new Map((data.apps || []).map((a) => [a.id, a]));
  const savedOrder = (data.layout && Array.isArray(data.layout.order)) ? data.layout.order : [];
  const ordered = [];

  for (const id of savedOrder) {
    if (byId.has(id)) {
      ordered.push(byId.get(id));
      byId.delete(id);
    }
  }

  for (const app of byId.values()) {
    ordered.push(app);
  }

  cardOrder = ordered.map((a) => a.id);
  reconcile(ordered);
  updateSummary();
  renderCardPicker(data.cards || []);
  applyActiveCard();
  pushToCard();
}

/* ---------------- 自定义卡片宿主 ---------------- */

const GRID_CARD_ID = '__grid__';

let activeCardId = GRID_CARD_ID;

function renderCardPicker(cards) {
  const desired = [GRID_CARD_ID].concat(cards.map((c) => c.id));
  const current = Array.from(cardPicker.options).map((o) => o.value);

  // 选项集合没变就不要重建 —— 重建会把用户正在展开的下拉关掉。
  if (desired.length === current.length && desired.every((id, i) => id === current[i])) {
    cardPicker.value = activeCardId;
    return;
  }

  cardPicker.textContent = '';

  const gridOption = document.createElement('option');
  gridOption.value = GRID_CARD_ID;
  gridOption.textContent = '网格（默认）';
  cardPicker.appendChild(gridOption);

  for (const card of cards) {
    const option = document.createElement('option');
    option.value = card.id;
    option.textContent = card.source === 'user' ? `${card.name}（用户）` : card.name;
    if (card.description) {
      option.title = card.description;
    }
    cardPicker.appendChild(option);
  }

  cardPicker.value = activeCardId;
}

function resolveActiveCardId() {
  const layout = (snapshot && snapshot.layout) || {};
  const wanted = layout.cardId || GRID_CARD_ID;
  const available = (snapshot && snapshot.cards) || [];

  // 卡片被删掉或改坏了就退回默认网格，不能让面板变成一片空白。
  if (wanted === GRID_CARD_ID || available.some((c) => c.id === wanted)) {
    return wanted;
  }

  return GRID_CARD_ID;
}

function applyActiveCard() {
  activeCardId = resolveActiveCardId();

  if (cardPicker.value !== activeCardId) {
    cardPicker.value = activeCardId;
  }

  const usingCard = activeCardId !== GRID_CARD_ID;
  board.hidden = usingCard;
  cardHost.hidden = !usingCard;

  if (!usingCard) {
    cardFrame.removeAttribute('src');
    delete cardFrame.dataset.cardId;
    return;
  }

  const card = (snapshot.cards || []).find((c) => c.id === activeCardId);
  if (!card) {
    return;
  }

  if (cardFrame.dataset.cardId !== card.id) {
    cardFrame.dataset.cardId = card.id;
    cardFrame.src = card.entryUrl;
  }
}

cardPicker.addEventListener('change', () => {
  const layout = Object.assign({}, (snapshot && snapshot.layout) || {}, { cardId: cardPicker.value });
  send({ type: 'savePreferences', theme: snapshot ? snapshot.theme : 'dark', layout });
  applyActiveCard();
});

/**
 * 卡片能调用的全部方法 —— 这就是安全边界。
 * 没有文件、没有注册表、没有任意命令、没有网络。
 */
const CARD_API = {
  getApps: () => ((snapshot && snapshot.apps) || []),
  getTheme: () => ((snapshot && snapshot.theme) || 'dark'),
  start: (params) => {
    send({ type: 'startApp', appId: requireAppId(params) });
    return { accepted: true };
  },
  stop: (params) => {
    send({ type: 'stopApp', appId: requireAppId(params) });
    return { accepted: true };
  },
  restart: (params) => {
    send({ type: 'restartApp', appId: requireAppId(params) });
    return { accepted: true };
  },
};

function requireAppId(params) {
  const appId = params && params.appId;
  if (typeof appId !== 'string' || appId.length === 0) {
    throw new Error('缺少 appId 参数');
  }
  return appId;
}

function handleCardMessage(event) {
  // 关键校验：沙箱文档的 event.origin 是字符串 "null"，无法用于鉴权；
  // 唯一可信的判据是「发消息的窗口就是我们的那个 iframe」。
  if (!cardFrame.contentWindow || event.source !== cardFrame.contentWindow) {
    return;
  }

  const message = event.data;
  if (!message || message.__processdeck !== true) {
    return;
  }

  if (message.type === 'ready') {
    pushToCard();
    return;
  }

  if (message.type !== 'call') {
    return;
  }

  let ok = true;
  let value = null;
  let error = null;

  try {
    const handler = CARD_API[message.method];

    if (typeof handler !== 'function') {
      throw new Error(`不支持的调用：${message.method}`);
    }

    value = handler(message.params);
  } catch (e) {
    ok = false;
    error = String(e && e.message ? e.message : e);
  }

  cardFrame.contentWindow.postMessage(
    { __processdeck: true, type: 'result', callId: message.callId, ok, value, error },
    '*'
  );
}

function pushToCard() {
  if (!cardFrame.contentWindow || activeCardId === GRID_CARD_ID) {
    return;
  }

  cardFrame.contentWindow.postMessage(
    {
      __processdeck: true,
      type: 'snapshot',
      apps: (snapshot && snapshot.apps) || [],
      theme: (snapshot && snapshot.theme) || 'dark',
    },
    '*'
  );
}

window.addEventListener('message', handleCardMessage);

/* ---------------- 渲染 ---------------- */

function reconcile(apps) {
  const seen = new Set();

  apps.forEach((app, index) => {
    seen.add(app.id);
    let element = cardElements.get(app.id);

    if (!element) {
      element = createCard(app);
      cardElements.set(app.id, element);

      // 新卡片的日志区默认滚到底部，符合「看日志」的直觉。
      element.dataset.autoscroll = 'true';
    }

    updateCard(element, app);

    // 按目标顺序摆放；已在该位置的元素不动，减少无谓的 DOM 变更。
    const current = board.children[index];
    if (current !== element) {
      board.insertBefore(element, current || null);
    }
  });

  for (const [id, element] of cardElements) {
    if (!seen.has(id)) {
      element.remove();
      cardElements.delete(id);
    }
  }

  renderEmptyState(apps.length === 0);
}

function createCard(app) {
  const card = document.createElement('article');
  card.className = 'card';
  card.dataset.appId = app.id;
  card.draggable = true;

  card.innerHTML = `
    <div class="card-head">
      <span class="dot"></span>
      <span class="card-title"></span>
      <span class="card-state mono"></span>
    </div>
    <div class="card-meta"></div>
    <div class="card-error" hidden></div>
    <pre class="card-log mono"></pre>
    <div class="card-actions">
      <button class="btn tiny primary" data-act="start">启动</button>
      <button class="btn tiny" data-act="stop">停止</button>
      <button class="btn tiny" data-act="restart">重启</button>
      <span class="spacer"></span>
      <button class="btn tiny ghost" data-act="copy">复制命令</button>
    </div>
  `;

  card.querySelector('[data-act="start"]').addEventListener('click', () => send({ type: 'startApp', appId: app.id }));
  card.querySelector('[data-act="stop"]').addEventListener('click', () => send({ type: 'stopApp', appId: app.id }));
  card.querySelector('[data-act="restart"]').addEventListener('click', () => send({ type: 'restartApp', appId: app.id }));
  card.querySelector('[data-act="copy"]').addEventListener('click', () => {
    navigator.clipboard.writeText(app.startCommand || '');
    showToast('启动命令已复制到剪贴板', 'info');
  });

  card.addEventListener('dragstart', (e) => {
    draggingId = app.id;
    card.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
    // Firefox 要求设置数据否则不触发拖拽
    e.dataTransfer.setData('text/plain', app.id);
  });

  card.addEventListener('dragend', () => {
    draggingId = null;
    card.classList.remove('dragging');
    board.querySelectorAll('.drop-target').forEach((el) => el.classList.remove('drop-target'));
  });

  card.addEventListener('dragover', (e) => {
    if (!draggingId || draggingId === app.id) {
      return;
    }
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    card.classList.add('drop-target');
  });

  card.addEventListener('dragleave', () => card.classList.remove('drop-target'));

  card.addEventListener('drop', (e) => {
    e.preventDefault();
    card.classList.remove('drop-target');

    const sourceId = draggingId || e.dataTransfer.getData('text/plain');
    if (!sourceId || sourceId === app.id) {
      return;
    }

    reorder(sourceId, app.id);
  });

  // 日志区：用户手动往上滚时就不再自动跟随，否则没法复制历史输出。
  const log = card.querySelector('.card-log');
  log.addEventListener('scroll', () => {
    const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
    card.dataset.autoscroll = atBottom ? 'true' : 'false';
  });

  return card;
}

function updateCard(card, app) {
  const state = app.state || 'stopped';

  card.querySelector('.dot').className = `dot ${state}`;
  card.querySelector('.card-title').textContent = app.name || app.id;
  card.querySelector('.card-state').textContent = STATE_LABEL[state] || state;

  // ---- 元信息 ----
  const metas = [];
  const ports = app.ports || {};
  const portKeys = Object.keys(ports);

  if (portKeys.length > 0) {
    metas.push(`<span><span class="k">端口</span> <span class="mono">${portKeys.join(', ')}</span></span>`);
  } else if (app.port) {
    metas.push(`<span><span class="k">端口</span> <span class="mono">${app.port}</span></span>`);
  }

  metas.push(`<span><span class="k">进程</span> <span class="mono">${app.processes ?? 0}</span></span>`);

  if (app.pid) {
    metas.push(`<span><span class="k">根 PID</span> <span class="mono">${app.pid}</span></span>`);
  }

  if (app.stopStage && STOP_STAGE_LABEL[app.stopStage]) {
    metas.push(`<span><span class="k">停止方式</span> ${STOP_STAGE_LABEL[app.stopStage]}</span>`);
  }

  card.querySelector('.card-meta').innerHTML = metas.join('');

  // ---- 错误 ----
  const errorBox = card.querySelector('.card-error');
  if (app.error) {
    errorBox.hidden = false;
    errorBox.textContent = app.error;
  } else {
    errorBox.hidden = true;
    errorBox.textContent = '';
  }

  // ---- 日志（内容不变就不动 DOM，避免滚动位置抖动）----
  const log = card.querySelector('.card-log');
  const lines = (app.logTail || []).slice(-MAX_LOG_LINES);
  const text = lines.length > 0 ? lines.join('\n') : '（暂无输出）';

  if (log.dataset.content !== text) {
    log.dataset.content = text;
    log.textContent = text;

    if (card.dataset.autoscroll !== 'false') {
      log.scrollTop = log.scrollHeight;
    }
  }

  // ---- 按钮可用性 ----
  const busy = state === 'starting' || state === 'stopping';
  card.querySelector('[data-act="start"]').disabled = busy || state === 'running';
  card.querySelector('[data-act="stop"]').disabled = busy || state === 'stopped';
  card.querySelector('[data-act="restart"]').disabled = busy;
}

function reorder(sourceId, targetId) {
  const next = cardOrder.filter((id) => id !== sourceId);
  const targetIndex = next.indexOf(targetId);

  if (targetIndex < 0) {
    return;
  }

  next.splice(targetIndex, 0, sourceId);
  cardOrder = next;

  persistLayout();
}

function persistLayout() {
  const layout = Object.assign({}, (snapshot && snapshot.layout) || {}, { order: cardOrder });
  send({ type: 'savePreferences', theme: snapshot ? snapshot.theme : 'dark', layout });
}

/* ---------------- 汇总 / 空状态 / 提示 ---------------- */

function updateSummary() {
  const apps = (snapshot && snapshot.apps) || [];
  const running = apps.filter((a) => a.state === 'running').length;
  const failed = apps.filter((a) => a.state === 'failed').length;

  summary.innerHTML =
    `<span>共 <b>${apps.length}</b> 个应用</span>` +
    `<span>运行中 <b>${running}</b></span>` +
    `<span>异常 <b>${failed}</b></span>`;

  engineStateEl.textContent = snapshot && snapshot.engineState
    ? snapshot.engineState
    : '引擎：未连接';

  portWatchEl.textContent = snapshot && snapshot.configurationPath
    ? `配置 ${snapshot.configurationPath}`
    : '端口监控：待接入';
}

function renderEmptyState(isEmpty) {
  const existing = board.querySelector('.empty-state');

  if (!isEmpty) {
    if (existing) {
      existing.remove();
    }
    return;
  }

  if (existing) {
    return;
  }

  const empty = document.createElement('div');
  empty.className = 'empty-state';
  empty.innerHTML = `
    <h2>还没有任何应用</h2>
    <p>ProcessDeck 用一个 JSON 文件描述要管理的应用：启动命令、停止方式、工作目录、端口、就绪探针。</p>
    <p class="mono" id="empty-config-path"></p>
    <div class="empty-actions">
      <button class="btn primary" id="btn-reload">重新加载配置</button>
    </div>
  `;

  board.appendChild(empty);

  const pathEl = empty.querySelector('#empty-config-path');
  pathEl.textContent = (snapshot && snapshot.configurationPath) || '';

  empty.querySelector('#btn-reload').addEventListener('click', () => {
    send({ type: 'reloadConfig' });
    showToast('已请求重新加载配置', 'info');
  });
}

let toastTimer = null;

function showToast(message, level) {
  let toast = document.querySelector('.toast');

  if (!toast) {
    toast = document.createElement('div');
    toast.className = 'toast';
    document.body.appendChild(toast);
  }

  toast.textContent = message;
  toast.dataset.level = level || 'info';
  toast.classList.add('visible');

  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove('visible'), 4000);
}

/* ---------------- 主题 ---------------- */

const btnTheme = document.getElementById('btn-theme');

btnTheme.addEventListener('click', () => {
  const root = document.documentElement;
  const next = root.dataset.theme === 'light' ? 'dark' : 'light';
  root.dataset.theme = next;
  localStorage.setItem('processdeck.theme', next);
  send({ type: 'savePreferences', theme: next, layout: (snapshot && snapshot.layout) || null });
});

document.getElementById('btn-add').addEventListener('click', () => {
  showToast('下一步接入：新建应用向导（启动命令 / 停止方式 / 端口 / 就绪探针）', 'info');
});

/* ---------------- 启动 ---------------- */

window.addEventListener('resize', updateViewport);
updateViewport();

const savedTheme = localStorage.getItem('processdeck.theme');
if (savedTheme) {
  document.documentElement.dataset.theme = savedTheme;
}

if (bridge) {
  bridge.addEventListener('message', handleHostMessage);
  send({ type: 'ensureConfigFile' });
  send({ type: 'hello' });
} else {
  // 脱离外壳单独打开页面时（例如设计阶段调试 CSS），给一份静态示例。
  engineStateEl.textContent = '引擎：未连接（页面独立打开）';
  applySnapshot({
    apps: [
      {
        id: 'demo-1', name: '示例卡片', state: 'running', port: 5173, pid: 12345,
        processes: 3, ports: { 5173: 12345 }, logTail: ['这是页面独立打开时的示例数据。'],
      },
    ],
    theme: 'dark',
    layout: null,
    configurationPath: '(未连接主程序)',
    engineState: '示例模式',
  });
}
