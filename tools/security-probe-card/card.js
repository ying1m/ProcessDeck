'use strict';

/* ==========================================================================
   安全探针卡片（回归测试）
   --------------------------------------------------------------------------
   安装方式：把整个目录复制到 %APPDATA%\ProcessDeck\cards\ 下，
   然后重启 ProcessDeck，在面板顶栏的卡片选择器里选「安全探针」。

   预期结果（实测结论）：
     1) window.chrome.webview 对卡片**是可见的** —— WebView2 会把它暴露给子框架，
        所以「对象看不见」不能作为防线；
     2) 但从不透明源的沙箱框架发出的 postMessage **不会**被路由到宿主，
        host 侧的 WebMessageReceived 一次都不会触发；
     3) 因此下面三条命令全部无效：应用不被停止、配置不被改写；
     4) 宿主那句「已拒绝来自非面板来源的 IPC 消息」也因此不会出现 ——
        消息在到达宿主之前就已经消失了（这与「被拒绝」是两回事）。

   换句话说，防护来自两层：
     * 沙箱：iframe 不给 allow-same-origin，源变成不透明，消息送不出去（主防线）
     * 来源校验：万一消息送到了，host 也会比对 Source（纵深防御）
   ========================================================================== */

const status = [];
let hostReply = '（等待白名单通道首次推送…）';
let pushCount = 0;

function render() {
  document.getElementById('out').textContent =
    status.concat(['', hostReply, `快照推送次数: ${pushCount}`]).join('\n');
}

function log(text) {
  status.push(text);
  render();
}

render();

const hostObject = window.chrome && window.chrome.webview;

log(`document.origin = ${document.origin}`);
log(`window.chrome.webview = ${hostObject ? '可见（符合预期）' : '不可见'}`);

if (hostObject) {
  const attempts = [
    { type: 'stopApp', appId: 'web' },
    { type: 'reloadConfig' },
    { type: 'savePreferences', theme: 'light', layout: { cardId: 'security-probe' } },
  ];

  for (const message of attempts) {
    try {
      hostObject.postMessage(message);
      log(`→ 已尝试直接发送 ${message.type}`);
    } catch (e) {
      log(`→ 发送 ${message.type} 抛异常: ${e.message}`);
    }
  }

  log('');
  log('以上消息均应「静默消失」：宿主一条都收不到。');
}

// 走白名单通道（正常路径），证明这条通道本身是通的
parent.postMessage({ __processdeck: true, type: 'ready' }, '*');

window.addEventListener('message', (event) => {
  const message = event.data;

  if (!message || message.__processdeck !== true || message.type !== 'snapshot') {
    return;
  }

  pushCount++;
  hostReply = `白名单通道正常：收到 ${(message.apps || []).length} 个应用`;
  render();
});
