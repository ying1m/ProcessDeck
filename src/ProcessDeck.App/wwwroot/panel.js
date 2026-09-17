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
const themePicker = document.getElementById('theme-picker');
const terminalDrawer = document.getElementById('terminal-drawer');
const appForm = document.getElementById('app-form');
const appFormTitle = document.getElementById('app-form-title');
const appFormDelete = document.getElementById('app-form-delete');
const fieldTemplate = document.getElementById('field-template');
const fieldUrl = document.getElementById('field-url');
const fieldPattern = document.getElementById('field-pattern');

const formFields = {
  template: document.getElementById('f-template'),
  name: document.getElementById('f-name'),
  description: document.getElementById('f-description'),
  start: document.getElementById('f-start'),
  startBody: document.getElementById('f-start-body'),
  stop: document.getElementById('f-stop'),
  cwd: document.getElementById('f-cwd'),
  port: document.getElementById('f-port'),
  stopTimeout: document.getElementById('f-stoptimeout'),
  readiness: document.getElementById('f-readiness'),
  timeout: document.getElementById('f-timeout'),
  url: document.getElementById('f-url'),
  pattern: document.getElementById('f-pattern'),
  autostart: document.getElementById('f-autostart'),
  hidewindow: document.getElementById('f-hidewindow'),
};

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
  renderThemePicker(data.themes || []);
  applyTheme(data.theme || 'dark');
  applyActiveCard();
  pushToCard();
  renderTerminal();
}

/* ---------------- 主题 ---------------- */

/** 上一次套用过的变量名。换主题时必须先清掉，否则会残留上一个主题的颜色。 */
const appliedThemeVars = new Set();

function currentTheme() {
  const themes = (snapshot && snapshot.themes) || [];
  const wanted = (snapshot && snapshot.theme) || 'dark';

  return themes.find((t) => t.id === wanted)
    || themes.find((t) => t.id === 'dark')
    || { id: 'dark', base: 'dark', vars: {} };
}

function applyTheme(themeId) {
  const themes = (snapshot && snapshot.themes) || [];
  const theme = themes.find((t) => t.id === themeId)
    || themes.find((t) => t.id === 'dark')
    || { id: 'dark', base: 'dark', vars: {} };

  const root = document.documentElement;

  for (const name of appliedThemeVars) {
    root.style.removeProperty(name);
  }
  appliedThemeVars.clear();

  // base 决定哪些「没被主题覆盖」的变量从深色还是浅色基底继承。
  root.dataset.theme = theme.base === 'light' ? 'light' : 'dark';

  for (const [name, value] of Object.entries(theme.vars || {})) {
    root.style.setProperty(name, value);
    appliedThemeVars.add(name);
  }

  if (themePicker.value !== theme.id) {
    themePicker.value = theme.id;
  }
}

function renderThemePicker(themes) {
  const desired = themes.map((t) => t.id);
  const current = Array.from(themePicker.options).map((o) => o.value);

  if (desired.length === current.length && desired.every((id, i) => id === current[i])) {
    return;
  }

  themePicker.textContent = '';

  for (const theme of themes) {
    const option = document.createElement('option');
    option.value = theme.id;
    option.textContent = theme.source === 'user' ? `${theme.name}（用户）` : theme.name;
    if (theme.description) {
      option.title = theme.description;
    }
    themePicker.appendChild(option);
  }
}

themePicker.addEventListener('change', () => {
  // 先本地立即套用，不等宿主回推 —— 切主题要跟手。
  // 宿主持久化后会再推一次快照，两边最终一致。
  applyTheme(themePicker.value);
  pushToCard();

  send({
    type: 'savePreferences',
    theme: themePicker.value,
    layout: (snapshot && snapshot.layout) || null,
  });
});

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
      // 送完整主题对象（含自定义变量），卡片才能跟随用户主题。
      theme: currentTheme(),
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
      <button class="btn tiny ghost" data-act="edit">编辑</button>
      <button class="btn tiny ghost" data-act="copy">复制命令</button>
    </div>
  `;

  card.querySelector('[data-act="start"]').addEventListener('click', () => send({ type: 'startApp', appId: app.id }));
  card.querySelector('[data-act="stop"]').addEventListener('click', () => send({ type: 'stopApp', appId: app.id }));
  card.querySelector('[data-act="restart"]').addEventListener('click', () => send({ type: 'restartApp', appId: app.id }));
  card.querySelector('[data-act="edit"]').addEventListener('click', () => openAppForm(app.id));
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
  // 点一下则展开交互式终端 —— 交互式程序光看输出不够，还得能回答它的提示。
  const log = card.querySelector('.card-log');
  log.title = '点击打开交互式终端';
  log.addEventListener('scroll', () => {
    const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
    card.dataset.autoscroll = atBottom ? 'true' : 'false';
  });
  log.addEventListener('click', () => openTerminal(app.id));

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
  const existing = board.querySelector('.welcome');

  if (!isEmpty) {
    if (existing) {
      existing.remove();
    }
    return;
  }

  if (existing) {
    return;
  }

  const welcome = document.createElement('div');
  welcome.className = 'welcome';
  welcome.innerHTML = `
    <h2>欢迎使用 ProcessDeck</h2>
    <p>它把「必须手敲命令才能启动、关闭方式还各不相同」的本地应用，变成一块可自定义的面板。</p>
    <ol>
      <li>创建第一个应用，填上启动命令即可；</li>
      <li>停止命令可以不填 —— 会先往终端发 Ctrl+C，最后强制回收整棵进程树；</li>
      <li>填了端口，启动前就会做冲突预检，并告诉你端口被谁占着。</li>
    </ol>
    <div class="welcome-actions">
      <button class="btn primary" id="btn-first-app">创建第一个应用</button>
      <button class="btn" id="btn-import-demo">导入一个示例</button>
      <button class="btn ghost" id="btn-open-folder">打开配置文件夹</button>
      <button class="btn ghost" id="btn-reload">重新加载配置</button>
    </div>
    <p class="welcome-path" id="welcome-path"></p>
  `;

  board.appendChild(welcome);

  welcome.querySelector('#welcome-path').textContent =
    `配置文件：${(snapshot && snapshot.configurationPath) || '（未知）'}`;

  welcome.querySelector('#btn-first-app').addEventListener('click', () => openAppForm(null));
  welcome.querySelector('#btn-import-demo').addEventListener('click', importExampleApp);
  welcome.querySelector('#btn-open-folder').addEventListener('click', () => send({ type: 'openConfigFolder' }));
  welcome.querySelector('#btn-reload').addEventListener('click', () => send({ type: 'reloadConfig' }));
}

/**
 * 一个「一定能跑起来」的示例：只用 Windows 自带的 PowerShell 监听一个端口。
 * 不依赖 Node / Python / .NET，装完就能点「启动」看到卡片变绿。
 */
function importExampleApp() {
  const startCommand = `powershell -NoProfile -Command "$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,38400); $l.Start(); Write-Host 'listening on 38400'; while($true){ Start-Sleep -Seconds 1 }"`;

  send({
    type: 'saveApp',
    app: {
      id: 'example-listener',
      name: '示例：本地端口服务',
      description: '用 PowerShell 监听 38400 端口，演示端口就绪判定。',
      startCommand,
      port: 38400,
      stopTimeoutSeconds: 5,
      autoStart: false,
      readiness: { kind: 'Port', timeoutSeconds: 20, intervalMs: 300 },
    },
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

/* ---------------- 交互式终端 ---------------- */

const terminalTitle = document.getElementById('terminal-title');
const terminalMeta = document.getElementById('terminal-meta');
const terminalOutput = document.getElementById('terminal-output');
const terminalLine = document.getElementById('terminal-line');
const terminalSend = document.getElementById('terminal-send');
const terminalCtrlC = document.getElementById('terminal-ctrlc');
const terminalCtrlD = document.getElementById('terminal-ctrld');
const terminalDot = terminalDrawer.querySelector('.dot');

let terminalAppId = null;

function openTerminal(appId) {
  if (!findApp(appId)) {
    return;
  }

  terminalAppId = appId;
  terminalDrawer.hidden = false;
  terminalOutput.dataset.content = '';

  // 告诉宿主放大这个应用的日志下发量。卡片仍然只拿 30 行。
  send({ type: 'focusApp', appId });

  renderTerminal();
  terminalLine.focus();
}

function closeTerminal() {
  if (terminalDrawer.hidden) {
    return;
  }

  terminalDrawer.hidden = true;
  terminalAppId = null;
  send({ type: 'focusApp', appId: null });
}

function renderTerminal() {
  if (terminalDrawer.hidden || !terminalAppId) {
    return;
  }

  const app = findApp(terminalAppId);

  if (!app) {
    closeTerminal();
    return;
  }

  terminalDot.className = `dot ${app.state || 'stopped'}`;
  terminalTitle.textContent = app.name || app.id;

  const bits = [STATE_LABEL[app.state] || app.state, `${app.processes || 0} 进程`];
  if (app.pid) {
    bits.push(`PID ${app.pid}`);
  }

  const ports = Object.keys(app.ports || {});
  if (ports.length > 0) {
    bits.push(`端口 ${ports.join(', ')}`);
  }

  terminalMeta.textContent = bits.join(' · ');

  const lines = app.logTail || [];
  const text = lines.length > 0 ? lines.join('\n') : '（暂无输出）';

  if (terminalOutput.dataset.content !== text) {
    const atBottom = terminalOutput.scrollHeight - terminalOutput.scrollTop - terminalOutput.clientHeight < 48;
    terminalOutput.dataset.content = text;
    terminalOutput.textContent = text;

    if (atBottom) {
      terminalOutput.scrollTop = terminalOutput.scrollHeight;
    }
  }

  const running = app.state === 'running' || app.state === 'starting';
  terminalLine.disabled = !running;
  terminalSend.disabled = !running;
  terminalCtrlC.disabled = !running;
  terminalCtrlD.disabled = !running;
  terminalLine.placeholder = running ? '输入内容后回车发送到应用终端…' : '应用未在运行，无法输入';
}

function sendTerminalLine() {
  if (!terminalAppId || terminalLine.disabled) {
    return;
  }

  const text = terminalLine.value;

  // 空回车不代劳：不少 CLI 会把「裸回车」解释成「重复上一条命令」。
  if (!text) {
    return;
  }

  // 行尾必须是 \r。伪控制台里的回车键就是这个字符；
  // 只发 \n 在相当多程序里不会触发提交，表现为「输入了但没反应」。
  send({ type: 'sendInput', appId: terminalAppId, text: `${text}\r` });
  terminalLine.value = '';
  terminalLine.focus();
}

function sendControlChar(char) {
  if (!terminalAppId) {
    return;
  }

  send({ type: 'sendInput', appId: terminalAppId, text: char });
  terminalLine.focus();
}

terminalSend.addEventListener('click', sendTerminalLine);

terminalLine.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    sendTerminalLine();
  }
});

terminalCtrlC.addEventListener('click', () => sendControlChar('\u0003'));
terminalCtrlD.addEventListener('click', () => sendControlChar('\u0004'));
document.getElementById('terminal-close').addEventListener('click', closeTerminal);

/* ---------------- 新建 / 编辑应用表单 ---------------- */

let editingAppId = null;

/* ---------------- 运行环境 + 简易 / 详细两种写法 ----------------
   「简易」只是少打字的输入方式：它一定会被拼成完整的命令行再保存。
   配置里永远只存完整命令行，实际运行的也是它 ——
   简易与详细之间不存在第二套语义，来回切换不会改变行为。 */

const runtimeChipRow = document.getElementById('f-runtimes');
const runtimeHint = document.getElementById('f-runtime-hint');
const startSimpleBox = document.getElementById('f-start-simple');
const startPreview = document.getElementById('f-start-preview');
const startWarn = document.getElementById('f-start-warn');
const startModeSeg = document.getElementById('f-start-mode');

const RUNTIMES = [
  {
    id: 'direct',
    label: '直接运行程序',
    hint: '填完整的程序名与参数。exe、dotnet、python 这类本身就可执行的用这一项。',
    placeholder: '例如 python -m http.server 8000 --bind 127.0.0.1',
    compose: (body) => body,
    parsePrefixes: [],
  },
  {
    id: 'powershell',
    label: 'PowerShell',
    hint: '直接写 PowerShell 语句，不必自己写 powershell.exe。命令里的引号尽量用单引号。',
    placeholder: '例如 Get-Process | Select-Object -First 5',
    compose: (body) => `powershell -NoProfile -ExecutionPolicy Bypass -Command "${body}"`,
    parsePrefixes: [
      'powershell -NoProfile -ExecutionPolicy Bypass -Command "',
      'powershell -NoProfile -Command "',
      'powershell -Command "',
    ],
    suffix: '"',
  },
  {
    id: 'pwsh',
    label: 'PowerShell 7',
    hint: '同上，但用 PowerShell 7（pwsh.exe）。',
    placeholder: '例如 Get-ChildItem | Measure-Object',
    compose: (body) => `pwsh -NoProfile -ExecutionPolicy Bypass -Command "${body}"`,
    parsePrefixes: [
      'pwsh -NoProfile -ExecutionPolicy Bypass -Command "',
      'pwsh -NoProfile -Command "',
      'pwsh -Command "',
    ],
    suffix: '"',
  },
  {
    id: 'cmd',
    label: 'cmd / 批处理',
    hint: 'npm、pnpm、yarn、.bat 必须走这一项 —— 它们是 .cmd 脚本而不是可执行文件，直接写会启动失败。',
    placeholder: '例如 pnpm dev',
    compose: (body) => `cmd.exe /c "${body}"`,
    parsePrefixes: ['cmd.exe /c "', 'cmd /c "'],
    suffix: '"',
  },
  {
    id: 'python',
    label: 'Python 代码',
    hint: '写 Python 代码片段，会自动包成 python -c "..."。',
    placeholder: '例如 import sys; print(sys.version)',
    compose: (body) => `python -c "${body}"`,
    parsePrefixes: ['python -c "'],
    suffix: '"',
  },
  {
    id: 'node',
    label: 'Node.js 代码',
    hint: '写 JS 片段，会自动包成 node -e "..."。',
    placeholder: '例如 console.log(process.version)',
    compose: (body) => `node -e "${body}"`,
    parsePrefixes: ['node -e "'],
    suffix: '"',
  },
];

let startRuntimeId = 'direct';
let startMode = 'simple';

function currentRuntime() {
  return RUNTIMES.find((r) => r.id === startRuntimeId) || RUNTIMES[0];
}

/** 从完整命令行反推运行环境与简易内容。匹配不上就退回详细方式。 */
function parseStartCommand(command) {
  const text = (command || '').trim();

  if (!text) {
    return { runtimeId: 'direct', body: '', mode: 'simple' };
  }

  for (const runtime of RUNTIMES) {
    for (const prefix of runtime.parsePrefixes || []) {
      if (!text.startsWith(prefix)) {
        continue;
      }

      let body = text.slice(prefix.length);

      if (runtime.suffix && body.endsWith(runtime.suffix)) {
        body = body.slice(0, -runtime.suffix.length);
      }

      return { runtimeId: runtime.id, body, mode: 'simple' };
    }
  }

  return { runtimeId: 'direct', body: text, mode: 'detailed' };
}

/** 编辑器内容对应的完整命令行。保存与预览共用它，保证只有一个事实来源。 */
function composeStartCommand() {
  if (startMode === 'detailed') {
    return formFields.start.value.trim();
  }

  return currentRuntime().compose(formFields.startBody.value.trim()).trim();
}

/** 供模板调用：指定运行环境并填入简易内容。 */
function setStartEditor(runtimeId, body) {
  startRuntimeId = runtimeId;
  startMode = 'simple';
  formFields.startBody.value = body;
  renderStartEditor();
}

function renderStartEditor() {
  const runtime = currentRuntime();

  runtimeChipRow.textContent = '';

  for (const item of RUNTIMES) {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = `chip${item.id === startRuntimeId ? ' is-active' : ''}`;
    chip.textContent = item.label;

    chip.addEventListener('click', () => {
      // 从详细切过来时，把已经写好的内容当作简易内容接过去，不让用户白写一遍。
      if (startMode === 'detailed' && formFields.start.value.trim()) {
        formFields.startBody.value = formFields.start.value.trim();
      }

      startRuntimeId = item.id;
      startMode = 'simple';
      renderStartEditor();
    });

    runtimeChipRow.appendChild(chip);
  }

  for (const button of startModeSeg.querySelectorAll('.seg-btn')) {
    button.classList.toggle('is-active', button.dataset.mode === startMode);
  }

  const simple = startMode === 'simple';
  startSimpleBox.hidden = !simple;
  formFields.start.hidden = simple;

  formFields.startBody.placeholder = runtime.placeholder || '';
  runtimeHint.textContent = runtime.hint || '';

  const composed = composeStartCommand();
  startPreview.textContent = composed || '（还没有内容）';

  // 简易内容里出现双引号时，拼出来的命令行会被提前截断 —— 必须当场说清楚，
  // 否则用户只会看到一个莫名其妙的启动失败。
  const body = formFields.startBody.value;
  const quoteRisk = simple && body.includes('"') && runtime.id !== 'direct';

  startWarn.hidden = !quoteRisk;
  startWarn.textContent = quoteRisk
    ? '简易内容里出现了双引号，拼出来的命令行可能被提前截断。请改用单引号，或切到「详细」自己写完整命令行。'
    : '';
}

for (const button of startModeSeg.querySelectorAll('.seg-btn')) {
  button.addEventListener('click', () => {
    const next = button.dataset.mode;

    if (next === startMode) {
      return;
    }

    if (next === 'detailed') {
      // 切到详细：把当前拼好的命令行填进去当起点。
      formFields.start.value = composeStartCommand();
    } else if (formFields.start.value.trim()) {
      // 切回简易：从详细内容反推运行环境与内容。
      const parsed = parseStartCommand(formFields.start.value);
      startRuntimeId = parsed.runtimeId;
      formFields.startBody.value = parsed.body;
    }

    startMode = next;
    renderStartEditor();
  });
}

formFields.startBody.addEventListener('input', renderStartEditor);
formFields.start.addEventListener('input', renderStartEditor);

const TEMPLATES = [
  {
    id: '',
    label: '—— 不用模板 ——',
    apply: () => {},
  },
  {
    id: 'node',
    label: 'Node / pnpm dev',
    apply: () => {
      formFields.name.value = '前端 dev server';
      setStartEditor('cmd', 'pnpm dev');
      formFields.port.value = '5173';
      formFields.readiness.value = 'Port';
    },
  },
  {
    id: 'dotnet',
    label: '.NET 项目',
    apply: () => {
      formFields.name.value = 'API 服务';
      setStartEditor('direct', 'dotnet run --project .');
      formFields.port.value = '5000';
      formFields.readiness.value = 'Http';
      formFields.url.value = 'http://127.0.0.1:5000/';
    },
  },
  {
    id: 'python-http',
    label: 'Python 静态服务',
    apply: () => {
      formFields.name.value = '静态网页服务';
      setStartEditor('direct', 'python -m http.server 8000 --bind 127.0.0.1');
      formFields.port.value = '8000';
      formFields.readiness.value = 'Port';
    },
  },
  {
    id: 'powershell',
    label: 'PowerShell 脚本',
    apply: () => {
      formFields.name.value = '自定义脚本';
      setStartEditor('powershell', '& .\\run.ps1');
      formFields.readiness.value = 'None';
    },
  },
  {
    id: 'powershell-inline',
    label: 'PowerShell 命令',
    apply: () => {
      formFields.name.value = '自定义命令';
      setStartEditor('powershell', 'Get-Date');
      formFields.readiness.value = 'None';
    },
  },
];

for (const template of TEMPLATES) {
  const option = document.createElement('option');
  option.value = template.id;
  option.textContent = template.label;
  formFields.template.appendChild(option);
}

formFields.template.addEventListener('change', () => {
  const template = TEMPLATES.find((t) => t.id === formFields.template.value);
  if (template) {
    template.apply();
    updateReadinessFields();
  }
});

/** 就绪类型决定要显示哪个补充字段，避免让用户面对一堆无关输入框。 */
function updateReadinessFields() {
  const kind = formFields.readiness.value;
  fieldUrl.hidden = kind !== 'Http';
  fieldPattern.hidden = kind !== 'Log';
}

formFields.readiness.addEventListener('change', updateReadinessFields);

function findApp(appId) {
  return ((snapshot && snapshot.apps) || []).find((a) => a.id === appId) || null;
}

function openAppForm(appId) {
  editingAppId = appId || null;

  const app = editingAppId ? findApp(editingAppId) : null;
  const definition = (app && app.definition) || null;

  appFormTitle.textContent = app ? `编辑「${app.name}」` : '新建应用';
  appFormDelete.hidden = !app;
  // 模板只在新建时有意义：编辑已有应用时套模板会把用户填好的东西冲掉。
  fieldTemplate.hidden = !!app;

  formFields.template.value = '';
  formFields.name.value = definition ? definition.name || '' : '';
  formFields.description.value = definition ? definition.description || '' : '';

  // 启动命令：先试着从已有的完整命令行反推出运行环境与简易内容；
  // 推不出来就落到详细方式，把原文原样交给用户。
  const startText = definition ? definition.startCommand || '' : '';
  const parsedStart = parseStartCommand(startText);
  startRuntimeId = parsedStart.runtimeId;
  startMode = parsedStart.mode;
  formFields.start.value = startText;
  formFields.startBody.value = parsedStart.body;

  formFields.stop.value = definition ? definition.stopCommand || '' : '';
  formFields.cwd.value = definition ? definition.workingDirectory || '' : '';
  formFields.port.value = definition && definition.port ? String(definition.port) : '';
  formFields.stopTimeout.value = String((definition && definition.stopTimeoutSeconds) || 15);

  const readiness = (definition && definition.readiness) || {};
  formFields.readiness.value = readiness.kind || 'None';
  formFields.timeout.value = String(readiness.timeoutSeconds || 60);
  formFields.url.value = readiness.url || '';
  formFields.pattern.value = readiness.pattern || '';
  formFields.autostart.checked = !!(definition && definition.autoStart);
  formFields.hidewindow.checked = !!(definition && definition.hideWindow);

  updateReadinessFields();
  renderStartEditor();
  appForm.hidden = false;
  formFields.name.focus();
}

function closeAppForm() {
  appForm.hidden = true;
  editingAppId = null;
}

function submitAppForm() {
  const name = formFields.name.value.trim();

  // 简易方式在这里被拼成完整命令行；保存的永远是完整命令行。
  const startCommand = composeStartCommand();

  if (!name) {
    showToast('请填写名称', 'error');
    formFields.name.focus();
    return;
  }

  if (!startCommand) {
    showToast('请填写启动命令', 'error');
    (startMode === 'simple' ? formFields.startBody : formFields.start).focus();
    return;
  }

  const kind = formFields.readiness.value;
  const readiness = { kind, timeoutSeconds: Number(formFields.timeout.value) || 60 };

  if (kind === 'Http') {
    readiness.url = formFields.url.value.trim();
    if (!readiness.url) {
      showToast('HTTP 就绪判定需要填写健康检查地址', 'error');
      return;
    }
  }

  if (kind === 'Log') {
    readiness.pattern = formFields.pattern.value.trim();
    if (!readiness.pattern) {
      showToast('日志就绪判定需要填写匹配正则', 'error');
      return;
    }
  }

  const portText = formFields.port.value.trim();

  // Host 侧会用和手改 JSON 完全相同的校验再查一遍，这里只做即时反馈。
  send({
    type: 'saveApp',
    app: {
      id: editingAppId || '',
      name,
      description: formFields.description.value.trim() || null,
      startCommand,
      stopCommand: formFields.stop.value.trim() || null,
      workingDirectory: formFields.cwd.value.trim() || null,
      port: portText ? Number(portText) : null,
      stopTimeoutSeconds: Number(formFields.stopTimeout.value) || 15,
      startGraceSeconds: 2,
      autoStart: formFields.autostart.checked,
      hideWindow: formFields.hidewindow.checked,
      readiness,
    },
  });

  closeAppForm();
}

function deleteEditingApp() {
  if (!editingAppId) {
    return;
  }

  const app = findApp(editingAppId);
  const label = app ? app.name : editingAppId;

  if (!confirm(`确定删除「${label}」吗？\n\n如果它正在运行，会先被停止。`)) {
    return;
  }

  send({ type: 'deleteApp', appId: editingAppId });
  closeAppForm();
}

document.getElementById('app-form-save').addEventListener('click', submitAppForm);
document.getElementById('app-form-cancel').addEventListener('click', closeAppForm);
document.getElementById('app-form-close').addEventListener('click', closeAppForm);
appFormDelete.addEventListener('click', deleteEditingApp);

// 点遮罩或按 Esc 关闭
appForm.addEventListener('mousedown', (e) => {
  if (e.target === appForm) {
    closeAppForm();
  }
});

window.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') {
    return;
  }

  if (!appForm.hidden) {
    closeAppForm();
    return;
  }

  if (!terminalDrawer.hidden) {
    closeTerminal();
  }
});

/* ---------------- 启动 ---------------- */

document.getElementById('btn-add').addEventListener('click', () => openAppForm(null));

window.addEventListener('resize', updateViewport);
updateViewport();

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
