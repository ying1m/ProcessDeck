using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ProcessDeck.Core.Net;
using ProcessDeck.Core.Supervision;

// ============================================================================
// JobSmokeTest —— 验证 ProcessDeck 的核心机制
//
//   1. Job Object 进程树回收：cmd.exe → ping.exe，关句柄应全部消失
//   2. 端口归属：cmd.exe → python(监听端口)，端口应能归回所属作业
//   3. ConPTY：子进程应看到真实终端（isatty=true），且输出能被正确整理
//
// 对照组刻意不用 Job Object / 不用伪控制台，用来复现痛点本身。
// ============================================================================

Console.OutputEncoding = Encoding.UTF8;

const string PingCommandLine = "cmd.exe /c ping -n 300 127.0.0.1 >nul";
const int TestPort = 38417;
const int TotalChecks = 10;

var failures = new List<string>();
var pass = 0;

// ---------------- 1. 对照组：不用 Job Object ----------------
Console.WriteLine("=== 1. 对照组：不用 Job Object，只杀根进程 ===");
var baselineA = PidsOf("ping");
var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 300 127.0.0.1 >nul")
{
    UseShellExecute = false,
    CreateNoWindow = true,
};

using (var root = Process.Start(psi)!)
{
    Thread.Sleep(1500);
    var spawned = PidsOf("ping").Except(baselineA).ToArray();
    Console.WriteLine($"  cmd.exe PID = {root.Id}，它派生的 ping PID = [{string.Join(", ", spawned)}]");

    if (spawned.Length == 0)
    {
        failures.Add("对照组：没观察到派生子进程，测试前提不成立");
    }

    root.Kill();
    root.WaitForExit(3000);
    Thread.Sleep(800);

    var survivors = spawned.Where(IsAlive).ToArray();
    Console.WriteLine($"  杀掉 cmd.exe 之后仍存活的 ping：{survivors.Length} 个 [{string.Join(", ", survivors)}]");

    if (survivors.Length > 0)
    {
        Console.WriteLine("  → 痛点复现：根进程死了，子进程还活着（还会继续占着端口）");
        pass++;
        foreach (var pid in survivors)
        {
            TryKill(pid);
        }
    }
    else
    {
        failures.Add("对照组：子进程意外一起死了，说明测试结构不对");
    }
}

// ---------------- 2. 实验组：Job Object 进程树回收 ----------------
Console.WriteLine();
Console.WriteLine("=== 2. 实验组：托管进 Job Object ===");
var baselineB = PidsOf("ping");
var sup = ProcessLauncher.Start(new LaunchOptions
{
    CommandLine = PingCommandLine,
    CreateNewConsole = false,
    NoWindow = true,
    DisplayName = "processdeck-smoke-test",
});

Thread.Sleep(1800);
var grandchildren = PidsOf("ping").Except(baselineB).ToArray();
var rootPid = sup.ProcessId;

Console.WriteLine($"  根进程 PID = {rootPid}，它派生的 ping PID = [{string.Join(", ", grandchildren)}]");
Console.WriteLine($"  作业内活跃进程数 = {sup.ActiveProcessCount}，历史进入总数 = {sup.TotalProcessCount}");

if (grandchildren.Length > 0 && sup.ActiveProcessCount >= 2)
{
    Console.WriteLine("  → 子孙进程自动入组成功（这是 taskkill 方案做不到的原子性）");
    pass++;
}
else
{
    failures.Add($"作业只捕获了 {sup.ActiveProcessCount} 个进程，子孙未自动入组");
}

Console.WriteLine("  关闭作业句柄 …");
sup.Dispose(); // 关句柄 → 内核回收整棵树
Thread.Sleep(1000);

var leftovers = new[] { rootPid }.Concat(grandchildren).Where(IsAlive).ToArray();
Console.WriteLine($"  关闭句柄后仍存活的进程：{leftovers.Length} 个 [{string.Join(", ", leftovers)}]");

if (leftovers.Length == 0)
{
    Console.WriteLine("  → 整棵进程树被内核一次性回收，没有孤儿残留");
    pass++;
}
else
{
    failures.Add($"仍有 {leftovers.Length} 个进程存活，进程树未收干净");
    foreach (var pid in leftovers)
    {
        TryKill(pid);
    }
}

// ---------------- 3. 端口归属 ----------------
Console.WriteLine();
Console.WriteLine("=== 3. 端口归属：端口 → PID → 作业 ===");

var portScript = Path.Combine(Path.GetTempPath(), "processdeck_port_holder.py");
File.WriteAllText(portScript, """
    import socket, sys, time
    port = int(sys.argv[1])
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", port))
    s.listen(5)
    time.sleep(120)
    """);

Console.WriteLine($"  启动前 {TestPort} 是否已占用：{PortScanner.IsPortInUse(TestPort)}");

var sup2 = ProcessLauncher.Start(new LaunchOptions
{
    CommandLine = $"cmd.exe /c python \"{portScript}\" {TestPort}",
    CreateNewConsole = false,
    NoWindow = true,
    DisplayName = "processdeck-port-test",
});

try
{
    TcpEndpoint? endpoint = null;
    for (var i = 0; i < 40 && endpoint is null; i++)
    {
        Thread.Sleep(250);
        endpoint = PortScanner.FindListener(TestPort);
    }

    if (endpoint is null)
    {
        failures.Add($"没能在 {TestPort} 上观察到监听（python 可能未安装或未在 PATH 中）");
    }
    else
    {
        var members = sup2.Job.MemberProcessIds;
        Console.WriteLine($"  端口 {TestPort} 的占用 PID = {endpoint.ProcessId}");
        Console.WriteLine($"  该作业的成员 PID = [{string.Join(", ", members)}]");
        Console.WriteLine($"  占用者进程名 = {PortScanner.DescribeProcess(endpoint.ProcessId)}");

        if (members.Contains(endpoint.ProcessId))
        {
            Console.WriteLine("  → 端口成功归属到作业：占用者正是该应用的子孙进程，而非一个裸 PID");
            pass++;
        }
        else
        {
            failures.Add($"端口占用 PID {endpoint.ProcessId} 不在作业成员 [{string.Join(",", members)}] 中，归属失败");
        }
    }
}
finally
{
    sup2.Dispose();
}

Thread.Sleep(1000);
var stillHeld = PortScanner.IsPortInUse(TestPort);
Console.WriteLine($"  停止应用后 {TestPort} 是否仍被占用：{stillHeld}");

if (!stillHeld)
{
    Console.WriteLine("  → 停止后端口已自动释放，无需手工去找谁占着端口");
    pass++;
}
else
{
    failures.Add($"停止后端口 {TestPort} 仍被占用，说明没回收干净");
}

// ---------------- 4. ConPTY 伪控制台 ----------------
Console.WriteLine();
Console.WriteLine("=== 4. ConPTY：子进程是否看到真实终端 ===");

var ptyScript = Path.Combine(Path.GetTempPath(), "processdeck_pty_demo.py");
File.WriteAllText(ptyScript, """
    import sys, time
    print("hello from pty")
    print("\x1b[32mGREEN\x1b[0m and \x1b[31mRED\x1b[0m")
    sys.stdout.write("progress")
    for i in (25, 50, 75, 100):
        sys.stdout.write("\rprogress %d%%" % i)
        sys.stdout.flush()
        time.sleep(0.08)
    print()
    print("isatty=%s" % sys.stdout.isatty())
    """);

// 4a. 对照组：普通管道重定向
var pipePsi = new ProcessStartInfo("python", $"\"{ptyScript}\"")
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};

using (var pipeProcess = Process.Start(pipePsi)!)
{
    var pipeOutput = pipeProcess.StandardOutput.ReadToEnd();
    pipeProcess.WaitForExit(20000);

    var pipeIsatty = ExtractIsatty(pipeOutput);
    Console.WriteLine($"  4a 对照组（普通管道重定向）: isatty={pipeIsatty}");
    Console.WriteLine($"     捕获到的原始输出含 ESC 转义: {pipeOutput.Contains('\u001b')}");

    if (pipeIsatty == "False")
    {
        Console.WriteLine("  → 管道模式下子进程知道 stdout 不是终端，会去色/丢进度条/改为块缓冲");
        pass++;
    }
    else
    {
        failures.Add($"对照组 isatty 期望 False，实际 {pipeIsatty}");
    }
}

// 4b. 实验组：ConPTY
var sup4 = ProcessLauncher.Start(new LaunchOptions
{
    CommandLine = $"cmd.exe /c python \"{ptyScript}\"",
    UsePseudoConsole = true,
    DisplayName = "processdeck-pty-test",
});

var deadline = DateTime.UtcNow.AddSeconds(25);
while (sup4.ActiveProcessCount > 0 && DateTime.UtcNow < deadline)
{
    await Task.Delay(120);
}

await sup4.Output.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(6));

var ptyLines = sup4.Output.Snapshot();
var ptyText = string.Join("\n", ptyLines);

Console.WriteLine($"  4b 实验组（ConPTY）捕获 {ptyLines.Count} 行：");
foreach (var line in ptyLines)
{
    Console.WriteLine($"     | {line}");
}

sup4.Dispose();

if (ptyText.Contains("hello from pty"))
{
    Console.WriteLine("  → ConPTY 输出捕获成功");
    pass++;
}
else
{
    failures.Add("ConPTY 没有捕获到 hello from pty");
}

if (!ptyText.Contains('\u001b'))
{
    Console.WriteLine("  → ANSI/VT 转义序列已被剥离，不会污染日志文本");
    pass++;
}
else
{
    failures.Add("输出里仍残留 ESC 转义序列");
}

var progressLines = ptyLines.Count(l => l.Contains("progress", StringComparison.Ordinal));
if (progressLines == 1 && ptyText.Contains("progress 100%"))
{
    Console.WriteLine("  → 进度条的 \\r 原地覆写被合并成 1 行（而不是刷出 5 行日志）");
    pass++;
}
else
{
    failures.Add($"进度条处理异常：progress 相关行数 = {progressLines}");
}

var ptyIsatty = ExtractIsatty(ptyText);
if (ptyIsatty == "True")
{
    Console.WriteLine("  → isatty=true：子进程认为自己接在真实终端上，行为与手工敲命令一致");
    pass++;
}
else
{
    failures.Add($"ConPTY 下 isatty 期望 True，实际 {ptyIsatty}");
}

// ---------------- 结果 ----------------
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS：{pass}/{TotalChecks} 项通过。进程树回收、端口归属、ConPTY 终端语义均可用。");
    return 0;
}

Console.WriteLine($"FAIL：{failures.Count} 项失败（通过 {pass}/{TotalChecks}）");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}

return 1;

// ---------------- 辅助函数 ----------------
static string ExtractIsatty(string text)
{
    var match = Regex.Match(text, @"isatty=(\w+)");
    return match.Success ? match.Groups[1].Value : "(未见)";
}

static int[] PidsOf(string name)
{
    var list = Process.GetProcessesByName(name);
    try
    {
        return list.Select(p => p.Id).ToArray();
    }
    finally
    {
        foreach (var p in list)
        {
            p.Dispose();
        }
    }
}

static bool IsAlive(int pid)
{
    try
    {
        using var p = Process.GetProcessById(pid);
        return !p.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static void TryKill(int pid)
{
    try
    {
        using var p = Process.GetProcessById(pid);
        p.Kill();
        p.WaitForExit(3000);
    }
    catch
    {
        // 已经没了
    }
}
