'use strict';

/* ==========================================================================
   紧凑列表卡片
   --------------------------------------------------------------------------
   这张卡片演示了第三方卡片能做什么、不能做什么：

     能：读取应用列表与状态、订阅变化、请求启动/停止/重启、读取主题。
     不能：碰文件系统、碰注册表、自己起进程、读宿主 DOM、访问 localStorage
           （沙箱 iframe 的源是不透明的）。

   通信只有一条通道：postMessage。父页面只放行白名单方法，
   其余调用一律返回错误。
   ========================================================================== */

const pending = new Map();
let sequence = 0;

const STATE_LABEL = {
  stopped: '已停止',
  starting: '启动中',
  running: '运行中',
  stopping: '停止中',
  failed: '失败',
};

/** 调用宿主提供的白名单方法。 */
function call(method, params) {
  return new Promise((resolve, reject) => {
    const callId = `call-${++sequence}`;
    pending.set(callId, { resolve, reject });

    parent.postMessage(
      { __processdeck: true, type: 'call', callId, method, params: params || null },
      '*'
    );

    setTimeout(() => {
      if (pending.delete(callId)) {
        reject(new Error(`调用 ${method} 超时（宿主未响应）`));
      }
    }, 8000);
  });
}

window.addEventListener('message', (event) => {
  const message = event.data;

  if (!message || message.__processdeck !== true) {
    return;
  }

  if (message.type === 'snapshot') {
    applyTheme(message.theme);
    render(message.apps || [], message.theme);
    return;
  }

  if (message.type === 'result') {
    const entry = pending.get(message.callId);
    if (!entry) {
      return;
    }

    pending.delete(message.callId);

    if (message.ok) {
      entry.resolve(message.value);
    } else {
      entry.reject(new Error(message.error || '调用失败'));
    }
  }
});

let lastSignature = '';

/** 上一次套用过的变量名，换主题时先清掉，避免残留上一个主题的颜色。 */
const appliedThemeVars = new Set();

/**
 * 套用宿主下发的主题。
 * 卡片拿到的是完整主题对象（含 base 与 vars），
 * 所以用户自定义主题也能作用到卡片上，而不是只有内置深浅两套。
 */
function applyTheme(theme) {
  if (!theme) {
    return;
  }

  const root = document.documentElement;

  for (const name of appliedThemeVars) {
    root.style.removeProperty(name);
  }
  appliedThemeVars.clear();

  root.dataset.theme = theme.base === 'light' ? 'light' : 'dark';

  for (const [name, value] of Object.entries(theme.vars || {})) {
    root.style.setProperty(name, value);
    appliedThemeVars.add(name);
  }
}

function render(apps, theme) {
  // 增量判断：状态没变就不重建 DOM，否则按钮会闪、hover 会断。
  const signature = apps.map((a) => `${a.id}:${a.state}:${a.processes}:${JSON.stringify(a.ports || {})}:${a.error || ''}`).join('|');
  if (signature === lastSignature) {
    return;
  }

  lastSignature = signature;

  const container = document.getElementById('rows');
  document.getElementById('hint').hidden = apps.length > 0;
  container.textContent = '';

  for (const app of apps) {
    container.appendChild(buildRow(app));
  }
}

function buildRow(app) {
  const row = document.createElement('div');
  row.className = 'row';

  const dot = document.createElement('span');
  dot.className = `dot ${app.state || 'stopped'}`;

  const name = document.createElement('span');
  name.className = 'name';
  name.textContent = app.name || app.id;

  const detail = document.createElement('span');
  detail.className = 'detail';

  if (app.error) {
    const err = document.createElement('span');
    err.className = 'err';
    err.textContent = app.error;
    detail.appendChild(err);
  } else {
    const ports = Object.keys(app.ports || {});
    const parts = [];
    if (ports.length > 0) parts.push(`:${ports.join(' :')}`);
    else if (app.port) parts.push(`:${app.port}`);
    parts.push(`${app.processes || 0} 进程`);
    if (app.pid) parts.push(`PID ${app.pid}`);
    parts.push(STATE_LABEL[app.state] || app.state);
    detail.textContent = parts.join('  ·  ');
  }

  const actions = document.createElement('span');
  actions.className = 'actions';

  const busy = app.state === 'starting' || app.state === 'stopping';

  const startButton = document.createElement('button');
  startButton.className = 'primary';
  startButton.textContent = '启动';
  startButton.disabled = busy || app.state === 'running';
  startButton.addEventListener('click', () => call('start', { appId: app.id }).catch(showError));

  const stopButton = document.createElement('button');
  stopButton.textContent = '停止';
  stopButton.disabled = busy || app.state === 'stopped';
  stopButton.addEventListener('click', () => call('stop', { appId: app.id }).catch(showError));

  actions.appendChild(startButton);
  actions.appendChild(stopButton);

  row.appendChild(dot);
  row.appendChild(name);
  row.appendChild(detail);
  row.appendChild(actions);

  return row;
}

function showError(error) {
  document.getElementById('hint').hidden = false;
  document.getElementById('hint').textContent = `调用失败：${error.message}`;
}

// 通知宿主：卡片已就绪，可以开始推数据了。
parent.postMessage({ __processdeck: true, type: 'ready' }, '*');
