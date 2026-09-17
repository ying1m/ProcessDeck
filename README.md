# ProcessDeck

> **A Windows desktop app manager with a fully customizable panel.**
> Start / stop arbitrary apps by command, reap whole process trees with Win32 Job Objects,
> capture real terminal output via ConPTY, and attribute ports back to the app that owns them.

**ProcessDeck** 把「一堆必须手敲命令才能启动、关闭方式各不相同、还会带出一串子进程占着端口」的本地应用，
变成一块可自定义的面板。

---

## 它解决什么问题

日常开发里总有这么几个东西：需要用 PowerShell 敲命令启动；有的关不掉只能杀进程；
有的会再拉出几个命令行；有的把端口占住之后你根本不知道是谁干的。
手写一堆 `.bat` 能凑合，但会迅速变成没人敢碰的意大利面。

既有的工具各缺一块：

| 类别 | 代表 | 缺什么 |
|---|---|---|
| 多进程编排器 | process-compose、mprocs、Foreman | 没有可自定义的图形界面；Windows 进程树处理粗糙 |
| 服务化封装 | NSSM、WinSW、Servy | 只能「装成服务常驻」，不能一条命令一条命令地启停 |
| 端口排查 | TCPView、Portpal | 只管「谁占了端口」，不管启动，也不管释放后重来 |
| 自建面板 | Dashy、Homarr、Home Assistant | 界面可自定义做得很好，但**只会开链接，完全不管进程** |

**把「Windows 原生进程树管控」×「每个应用自定义启停」×「高度可自定义 UI」三件事合起来的，目前没有。**
ProcessDeck 就是这块缺口。

---

## 核心特性

| 特性 | 实现方式 | 为什么重要 |
|---|---|---|
| **进程树精确回收** | Win32 Job Object + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` | 关句柄即由**内核**回收整棵树。不用 `taskkill /T /F`，既没有遍历竞态，也不是杀软的恶意行为特征 |
| **零孤儿保证** | 作业句柄随进程消亡自动关闭 | ProcessDeck 自己崩溃 / 被任务管理器结束 / 被误杀，**被管应用也不会变成占着端口的孤儿** |
| **真实终端输出** | ConPTY 伪控制台 | 子进程 `isatty()==true`，颜色、进度条、交互提示行为与手工敲命令完全一致；管道重定向会让很多 CLI 静默或改行为 |
| **端口归属** | IP Helper `GetExtendedTcpTable` + 作业成员 PID 求交集 | 冲突时告诉你「被『前端 dev server』占着」，而不是丢一个裸 PID；不做父进程链回溯，不怕 PID 复用 |
| **停止三态降级** | 停止命令 → Ctrl+C → 关作业对象 | 只做强杀的工具会经常打断应用清理，导致数据文件损坏、端口没释放 |
| **就绪探针** | 端口 / HTTP / 日志正则 | 进程存在 ≠ 服务可用。没有探针的界面会长期显示「运行中」但点开根本连不上 |
| **可自定义面板** | WPF 外壳 + WebView2，面板是纯 HTML/CSS/JS | 主题 = CSS 变量；布局由前端拥有、后端原样存取，改 UI 不需要动后端模型 |
| **单文件配置** | `%APPDATA%\ProcessDeck\deck.json` | 支持注释与尾逗号，中文不转义，可以直接手改、可以进版本库 |

---

## 快速开始

### 1. 准备配置

首次运行会生成空的 `deck.json`。路径显示在面板底部状态栏，也可以用环境变量 `PROCESSDECK_CONFIG` 覆盖（便携模式 / 多套配置）。

```jsonc
{
  "schemaVersion": 1,
  "theme": "dark",
  "apps": [
    {
      "id": "web",
      "name": "前端 dev server",
      "description": "Vite 开发服务器",
      "startCommand": "cmd.exe /c pnpm dev",
      "workingDirectory": "%USERPROFILE%\\projects\\my-app",
      "port": 5173,
      "autoStart": false,
      "stopTimeoutSeconds": 15,
      "readiness": { "kind": "Port", "timeoutSeconds": 60, "intervalMs": 300 }
    },
    {
      "id": "api",
      "name": "API 服务",
      "startCommand": "dotnet run --project src/Api",
      "stopCommand": "dotnet build-server shutdown",
      "port": 8080,
      "readiness": { "kind": "Http", "url": "http://127.0.0.1:8080/health", "timeoutSeconds": 90 }
    }
  ]
}
```

### 2. 配置字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 稳定标识，用于布局持久化。留空会按名字自动生成 |
| `name` | string | 面板上显示的名字 |
| `startCommand` | string | **原样**交给 `CreateProcess`，不做转义改写 |
| `stopCommand` | string | 可选。优雅停止命令，例如 `pg_ctl stop -D data` |
| `workingDirectory` | string | 可选。支持 `%VAR%` 展开 |
| `environment` | object | 追加/覆盖的环境变量（会与当前环境合并，不会丢掉 PATH） |
| `port` | number | 主端口。用于启动前冲突预检与端口就绪判定 |
| `extraPorts` | number[] | 附带端口，仅用于面板展示 |
| `readiness.kind` | `None`\|`Port`\|`Http`\|`Log` | 就绪判定方式 |
| `readiness.url` | string | `Http` 时必填 |
| `readiness.pattern` | string | `Log` 时必填，正则 |
| `readiness.timeoutSeconds` | number | 超时即判定启动失败 |
| `stopTimeoutSeconds` | number | 优雅停止的最长等待，默认 15 |
| `startGraceSeconds` | number | 无探针时，进程存活多久算就绪，默认 2 |
| `autoStart` | boolean | ProcessDeck 启动时自动拉起 |

### 3. 也可以用界面完成

面板右上角的「+ 添加应用」会打开表单：名称、启动命令、停止命令、工作目录、端口、就绪判定，
并带 Node / .NET / Python / PowerShell 几个模板。

首次启动时面板会显示向导，可以一键导入一个**只依赖 Windows 自带 PowerShell** 的示例应用 ——
装完就能点「启动」看到卡片变绿，不需要提前装任何东西。

配置文件始终是纯文本，两种方式可以混用：手改之后点一下「重新加载配置」即可。
应用卡片上还有「编辑」按钮；运行中的应用会要求先停止再改（避免命令改了、进程还是旧的）。

### 4. 从源码构建

```powershell
git clone https://github.com/ying1m/ProcessDeck.git
cd ProcessDeck
dotnet build ProcessDeck.slnx -c Release
dotnet run --project src/ProcessDeck.App
```

**要求**：.NET 10 SDK、Windows 10 1809+（ConPTY 需要）、WebView2 运行时（Win11 与较新 Win10 自带）。
不需要 Rust、不需要 C++ 工具链、不需要任何第三方 NuGet 包（只依赖 `Microsoft.Web.WebView2`）。

---

## 自定义卡片

面板默认是一个卡片网格。除此之外，你可以放入自己的卡片包，**完全替换**面板的呈现方式。

### 卡片包结构

一个卡片包就是一个目录：

```
my-card/
  card.json     必需，清单
  card.html     默认入口，运行在沙箱 iframe 中
  card.js       可选
  card.css      可选
```

`card.json`：

```json
{
  "id": "my-card",
  "name": "我的面板",
  "description": "一句话说明",
  "author": "you",
  "version": "1.0.0",
  "entry": "card.html",
  "order": 100
}
```

把目录放进 `%APPDATA%\ProcessDeck\cards\`，重启 ProcessDeck，
即可在顶栏的卡片选择器里看到它。内置卡片在安装目录的 `wwwroot\cards\`，
可以直接拿来改（`compact-list` 是一份完整参考实现）。

### 卡片能用的 API

通信只有一条通道 `postMessage`：

```js
// 卡片 → 宿主
parent.postMessage({ __processdeck: true, type: 'ready' }, '*');
parent.postMessage({
  __processdeck: true, type: 'call', callId: 'c1',
  method: 'start', params: { appId: 'web' }
}, '*');

// 宿主 → 卡片
window.addEventListener('message', (e) => {
  const m = e.data;
  if (m.__processdeck && m.type === 'snapshot') { /* m.apps, m.theme */ }
  if (m.__processdeck && m.type === 'result')   { /* m.callId, m.ok, m.value */ }
});
```

| 白名单方法 | 参数 | 返回 |
|---|---|---|
| `getApps` | — | 应用快照数组 |
| `getTheme` | — | 当前主题名 |
| `start` / `stop` / `restart` | `{ appId }` | `{ accepted: true }` |

白名单之外的方法一律返回错误。

### 安全边界

卡片运行在 `sandbox="allow-scripts"` 的 iframe 中，**刻意不给 `allow-same-origin`**：

- 拿不到父页面 DOM、cookie、localStorage
- 够不到文件系统、注册表，也无法自己起进程
- 只有上面那几个白名单方法能对外界产生影响

有一点值得说明：`window.chrome.webview` 对卡片**是可见的**（WebView2 会把它暴露给子框架），
但**来自不透明源框架的消息不会被路由到宿主**，所以这条路走不通。宿主另外还校验消息来源作为纵深防御。
完整分析与回归测试见 [`docs/engineering-notes.md`](docs/engineering-notes.md) 第 10 节与 `tools/security-probe-card/`。

> 换句话说：别人分享给你的卡片，**最坏也只能启停你自己配置的应用**，无法读写你的文件。

---

## 主题包

主题包和卡片包一样是一个目录，放进 `%APPDATA%\ProcessDeck\themes\`：

```
nord/
  theme.json
```

```json
{
  "id": "nord",
  "name": "Nord",
  "description": "低对比度的冷色调",
  "author": "you",
  "version": "1.0.0",
  "base": "dark",
  "vars": {
    "--bg": "#2e3440",
    "--bg-card": "#3b4252",
    "--accent": "#88c0d0",
    "--state-running": "#a3be8c"
  }
}
```

`base` 决定**没被覆盖**的变量从深色还是浅色继承，所以一个主题只需要写它真正想改的那几个。
内置的 `nord` 就是一份完整例子。

### 标准变量

面板与卡片共用同一套变量名，主题包改的就是这些：

| 变量 | 用途 |
|---|---|
| `--bg` / `--bg-elevated` / `--bg-card` / `--bg-card-hover` | 背景层次 |
| `--border` / `--border-strong` | 描边 |
| `--fg` / `--fg-muted` / `--fg-faint` | 三级文字 |
| `--accent` / `--accent-fg` | 强调色与其上的文字色 |
| `--state-running` / `--state-stopped` / `--state-starting` / `--state-failed` | 状态点 |
| `--log-bg` / `--log-fg` | 终端输出区 |
| `--radius` / `--radius-sm` / `--gap` / `--pad` | 圆角与间距 |
| `--font` / `--font-mono` | 字体 |

### 安全约束

变量名必须以 `--` 开头；值里不允许出现 `url(`、`expression(`、`@import`、分号或花括号。

这不是洁癖：如果一个可分享的主题能往被用作 `background` 的变量里塞 `url(...)`，
那么别人一打开面板就会向该地址发起请求 —— 等于一个静默的「谁在用这个主题」信标。
不合规的变量会被丢弃，并在快照的 `themeProblems` 里报告出来。

---

## ⚠️ 关于杀毒软件误报

这个工具天生踩杀软的命门：**启动进程、回收进程树、结束占用端口的进程**。
如果还加上「无签名自编译 exe」，被误报几乎是必然的。

ProcessDeck 在设计上刻意避开了几个最典型的高危特征：

- ❌ **不用** `taskkill /F /T` + 隐藏窗口的组合 —— 改为关作业对象句柄，由内核回收
- ❌ **不遍历进程列表批量 `Stop-Process`** —— 不需要枚举，作业成员由内核维护
- ❌ **不加壳、不混淆、不做单文件压缩** —— 保持普通 IL
- ❌ **不注入、不 hook、不自提升、不改注册表启动项**
- ❌ **应用进程默认带可见控制台**（本来就要看输出，不需要躲）

即便如此，**首次运行仍可能被 SmartScreen 或 HIPS 拦截**。建议做法是把项目目录加进杀软的信任区，
而不是关闭实时防护。如果你遇到误报，欢迎提 issue 并附上杀软名称与版本。

---

## 架构

```
┌── WPF 外壳（原生）──────────────────────────────────────────┐
│  托盘图标 / 置顶窗口 / 单实例 / 高 DPI(PerMonitorV2)         │
│                                                             │
│  ┌── WebView2 面板区域 ────────────────────────────────┐   │
│  │   index.html + panel.css + panel.js                  │   │
│  │   卡片网格 / 拖拽排序 / CSS 变量主题                  │   │
│  └──────────────────────────────────────────────────────┘   │
└──────── WebView2 WebMessage（进程内，不开放网络端口）────────┘
┌── ProcessDeck.Core（引擎，可脱离界面单独测试）──────────────┐
│  ProcessLauncher   挂起态创建 → 原子收编作业 → 恢复         │
│  JobObject         作业对象、成员 PID、KILL_ON_JOB_CLOSE     │
│  ConPtySession     伪控制台、读写、resize                    │
│  TerminalOutputBuffer  字节流 → 文本行（光标模型 + 转义剥离）│
│  PortScanner       IP Helper 结构化端口快照                  │
│  AppSupervisor     状态机 / 就绪探针 / 停止三态 / 端口归属   │
│  ConfigurationStore  带注释的 JSON 配置读写                  │
└──────────────────────────────────────────────────────────────┘
```

### 为什么 IPC 不用「监听 127.0.0.1 + token」

WebView2 自带 `postMessage` / `WebMessageReceived` 通道，走**进程内消息**：
既不打开任何网络端口，也就不存在 token 泄漏、被其他本地进程探测或 CSRF 的问题。
安全性严格优于本地 HTTP 服务，同时少一个需要维护的端口。

---

## 开发状态与路线图

当前是**可用的早期版本**，引擎部分（进程树、ConPTY、端口归属、状态机、停止三态、IPC）已有端到端测试覆盖。

- [x] Job Object 进程树回收
- [x] ConPTY 真实终端输出
- [x] 端口占用归属与冲突预检
- [x] 配置模型 + 状态机 + 就绪探针
- [x] 停止三态降级
- [x] WebView2 面板 + IPC + 实时日志
- [x] 拖拽排序 + CSS 变量主题
- [x] 沙箱化自定义卡片（含安全回归测试 `tools/security-probe-card`）
- [x] 首运向导与图形化新建 / 编辑应用
- [x] 主题包（可分享的 CSS 变量集，含变量值与白名单校验）
- [ ] 面板内嵌终端（交互式输入）
- [ ] 自动更新与 winget 发布

---

## 协议

[MIT](LICENSE)
