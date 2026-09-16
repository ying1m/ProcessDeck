# 工程笔记

这份文档记录开发 ProcessDeck 过程中踩到的、**不查源码或不做实验就一定会中招**的坑。
每一条都有可复现的验证方式，不是转述文档。

复现工具都在 `tools/` 下，可以独立运行。

---

## 1. ConPTY 挂不上子进程的两个隐藏条件

### 症状

`CreatePseudoConsole` 返回成功；从输出管道能读到 conhost 的 16 字节握手序列
`1b 5b 3f 39 30 30 31 68 1b 5b 3f 31 30 30 34 68`（`\x1b[?9001h\x1b[?1004h`）；
进程也正常启动。

**但是**：子进程的 `stdout.isatty()` 是 `false`，它的输出漏进了**父进程的控制台**，
而我们从管道里一个字节都读不到。

### 原因

微软官方样例（"Creating a Pseudoconsole session"）里用的是：

```c
CreateProcessW(NULL, cmd, NULL, NULL,
               TRUE,                       // bInheritHandles
               EXTENDED_STARTUPINFO_PRESENT, ...);
```

**这个组合在实际使用中是错的。** 必须同时满足：

1. `bInheritHandles = **FALSE**`
2. `STARTUPINFO.dwFlags |= **STARTF_USESTDHANDLES**`，且
   `hStdInput` / `hStdOutput` / `hStdError` 全部填 **`INVALID_HANDLE_VALUE`**

否则 `CreateProcess` 会把父进程的标准句柄交给子进程，伪控制台拿不到 stdin/stdout。

### 验证

`tools/ConPtyProbe` 用四个变体的矩阵 + 「让子进程把自己的 `isatty()` 写进文件」作为判据：

| 变体 | `bInheritHandles` | `STARTF_USESTDHANDLES` | 管道读到 | 子进程自述 |
|---|---|---|---|---|
| V1 | TRUE | ✗ | 16 字节（仅握手） | `isatty=False encoding='gbk'` |
| V5 | FALSE | ✗ | 16 字节（仅握手） | `isatty=False encoding='gbk'` |
| V8 | TRUE | ✓ | 16 字节 | 子进程没能写报告 |
| **V9** | **FALSE** | **✓** | **87 字节，含标记** | **`isatty=True encoding='utf-8'`** |

注意 `encoding` 从 `gbk` 变成 `utf-8` —— **Python 自己就知道终端变了**，
这正是 ConPTY 存在的意义。

---

## 2. `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 的 `lpValue` 是句柄值本身

```c
#define PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE \
    ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)   // == 0x00020016
```

C 样例写成：

```c
UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                          hPC, sizeof(hPC), NULL, NULL);
```

因为 `HPCON` 本身就是 `typedef VOID* HPCON`，**句柄值直接当指针用**。
在 C# 里如果多包一层：

```csharp
var mem = Marshal.AllocHGlobal(IntPtr.Size);
Marshal.WriteIntPtr(mem, hpc);
UpdateProcThreadAttribute(list, 0, attr, mem, (IntPtr)IntPtr.Size, ...);  // ✗
```

ConPTY 会收到一个垃圾句柄。症状：`CreateProcess` 仍然成功，但输出管道**一个字节都没有**
（连上面那 16 字节握手序列都没有）。对照实验见 `tools/ConPtyProbe` 的 V3。

正确写法是直接传句柄值：

```csharp
UpdateProcThreadAttribute(list, 0, attr, hpc, (IntPtr)IntPtr.Size, ...);  // ✓
```

---

## 3. `\r` 不是「清空当前行」

### 症状

终端输出整理出来**全是空行**，内容一个字不剩。

### 原因

把 `\r` 实现成「清空当前行缓冲」是一个很自然的直觉，但它是错的。

真实终端里 `\r` 只做一件事：**把光标移回行首，已经打印的内容原样留在屏幕上。**

而最常见的行结束序列是 `\r\n`。如果 `\r` 会清空缓冲：

```
写入 "hello from pty"   → 缓冲 = "hello from pty"
遇到 \r                 → 缓冲被清空
遇到 \n                 → 提交一个空行
```

整份日志就这么没了。

### 正确模型

维护 **字符缓冲 + 光标列**，写入时按列覆盖：

```
\r      → cursor = 0      （不清空）
\n      → 提交当前行，缓冲清空，cursor = 0
\b      → cursor--        （终端语义是移动光标，不删字符）
普通字符 → 覆盖缓冲[cursor]，cursor++
```

这样两类行为同时正确：

- 进度条 `\rprogress 25%` → 原地覆写，始终只有一行
- 普通 `\r\n` 换行 → 内容完整保留

验证见 `tools/JobSmokeTest` 第 4 组：Python 用 `\r` 连续覆写 5 次，
断言「`progress` 相关行数恰好为 1，且值为 `progress 100%`」。

---

## 4. Job Object：创建顺序不能颠倒

必须 **`CreateProcess(CREATE_SUSPENDED)` → `AssignProcessToJobObject` → `ResumeThread`**。

如果先 `ResumeThread` 再收编，进程可能在被收编之前就已经派生出逃逸的后代，
那棵子树永远收不干净。这是同类工具的经典 bug。

### 一个副作用很强的保证

因为作业设置了 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，
**即使 ProcessDeck 自己被强杀（崩溃、任务管理器结束进程、被杀软误杀），
内核也会在进程消亡时关闭作业句柄并回收整棵树**。

实测（`Stop-Process -Force` 硬杀 ProcessDeck，不执行任何清理代码）：

```
强杀前：被管 python 进程 2 个
强杀后：被管 python 进程 0 个
   端口 38440 已释放
   端口 38441 已释放
```

---

## 5. 端口归属：不要回溯父进程链

拿端口对应的 PID 之后，常见做法是沿父进程链往上找「谁启动的」。
这既慢，又会被 PID 复用骗到（PID 回收后可能挂到完全无关的进程上）。

正确做法：用 `JobObjectBasicProcessIdList` 取作业成员 PID，
和 IP Helper 的端口表**求交集**。归属是精确的，且是 O(n)。

`JobObjectBasicProcessIdList` 是变长结构（头部 + `SIZE_T` 数组），
不能直接 `PtrToStructure` 整个结构；缓冲不足时内核返回 `ERROR_MORE_DATA` (234)
并在头部写入实际需要的大小，据此重试。

---

## 6. `MIB_TCPROW_OWNER_PID.LocalPort` 的字节序

`GetExtendedTcpTable` 返回的本机端口是**网络字节序**：

```csharp
port = ((raw & 0x000000FF) << 8) | ((raw & 0x0000FF00) >> 8);
```

不做这一步会得到一堆看似随机的大数字。相比之下解析 `netstat -ano` 的文本输出
除了同样要处理这个，还额外受系统语言、输出截断、格式变动的影响。

---

## 7. 高 DPI 下的 WPF + WebView2 几何自洽

进程必须是 **PerMonitorV2** 感知的（通过 `app.manifest` 声明）。

不声明时的表现非常隐蔽：WPF 按 96 DPI 布局，WebView2 按显示器**真实**缩放渲染，
于是内嵌浏览器的视口尺寸会**大于物理屏幕**，面板下半部分被推到屏幕外看不见。

排查方法已经内置：`%LOCALAPPDATA%\ProcessDeck\shell.log` 会记录
「窗口 DIP 尺寸 / 控件 DIP 尺寸 / pixelsPerDip」，三者一旦不自洽立刻可见；
面板状态栏也会实时显示真实视口尺寸。

---

## 8. 取证工具本身也会骗人

排查第 7 条时，我一度以为「面板下半部分是黑的」是应用 bug。
实际上那是**截图脚本**的错：Windows PowerShell 5.1 是 DPI 不感知的，
`Screen.PrimaryScreen.Bounds` 返回的是虚拟化尺寸（2560×1600 的屏幕报成 1707×1067），
`CopyFromScreen` 于是只抓到了左上角一块并做了缩放。

`tools/shot.ps1` 在创建任何 GDI 对象**之前**先调用
`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`，
之后才能拿到真实的 2560×1600 全分辨率画面。

**教训**：当观测结果和代码逻辑矛盾时，先怀疑观测手段。

---

## 9. CSP 的 `frame-src` 会回落到 `default-src`，把自定义卡片一起拦掉

面板的 CSP 基线是 `default-src 'none'`，然后逐项开口。
加入沙箱卡片时 iframe 直接显示：

> 已阻止此内容。请与网站所有者联系以解决问题。

原因不是 X-Frame-Options，也不是 WebView2 的虚拟主机，
而是 **CSP 里没有写 `frame-src`，于是它回落到 `default-src 'none'`**，所有框架一律禁止。

```html
<!-- ✗ 卡片加载不出来 -->
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'self'; script-src 'self'">

<!-- ✓ 显式列出卡片来源 -->
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'self' 'unsafe-inline'; script-src 'self';
               frame-src 'self' https://processdeck-cards.local">
```

内置卡片在 `'self'`（processdeck.local）下；用户卡片在 `%APPDATA%`，
需要单独映射一个虚拟主机（`processdeck-cards.local`）并写进 `frame-src`。

---

## 10. 沙箱卡片的真实安全边界（实测，不是推测）

### 反直觉的第一点：`window.chrome.webview` 对卡片**是可见的**

自定义卡片跑在 `sandbox="allow-scripts"` 的 iframe 里（刻意不给 `allow-same-origin`）。
按直觉，被沙箱隔离的框架应该碰不到宿主对象。**实测并非如此**：

```
沙箱 iframe 内的 document.origin = undefined
window.chrome.webview 是否可见: 可见
→ 直接发送 {type:"stopApp", appId:"web"}（未抛异常）
```

所以「沙箱会让宿主对象不可见」这个假设不成立，不能把它当作防线。

### 但第二层挡住了它

给宿主加了一行诊断，打印每一条到达的 IPC 消息：

```
IPC 收到消息 source=https://processdeck.local/index.html len=27   ← 面板的 ensureConfigFile
IPC 收到消息 source=https://processdeck.local/index.html len=16   ← 面板的 hello
```

**卡片发出的三条消息，一条都没到。** 同时：

- 被管应用没有被停止（端口 38440 / 38441 仍在监听）
- 配置文件没有被改写（`theme` 仍是 `dark`）

原因：**从不透明源（opaque origin）的沙箱框架发出的 `postMessage` 不会被路由到宿主**。
消息在到达 `WebMessageReceived` 之前就消失了，所以连「已拒绝」的日志都不会产生 ——
「消息送不到」和「消息被拒绝」是两件不同的事，排查时别混淆。

### 结论：两层防线

| 层 | 机制 | 作用 |
|---|---|---|
| 沙箱（主防线） | `sandbox="allow-scripts"`，不给 `allow-same-origin` | 框架源变不透明，`chrome.webview.postMessage` 送不出去 |
| 来源校验（纵深防御） | 比对 `CoreWebView2WebMessageReceivedEventArgs.Source` | 万一消息送到了，非面板文档一律拒绝 |

第二层仍然必须写：它防的是「以后有人给 iframe 加上 `allow-same-origin`」、
或者卡片被放到非沙箱框架里加载这类改动。

### 一个实现细节

`e.Source` 在不透明源的框架上可能直接**抛异常**。如果不在 `try/catch` 里处理，
异常会从事件处理器里逃出去，结果是「攻击被挡住了，但日志里什么都不留下」——
排查时极其难查。正确做法是：读不到来源就按不可信处理，并留下日志。

回归测试脚本保留在 `tools/security-probe-card/`，复制到用户卡片目录即可复跑。
