using System.Text;
using ProcessDeck.Core.Configuration;
using ProcessDeck.Core.Net;
using ProcessDeck.Core.Supervision;

// ============================================================================
// SupervisorSmokeTest —— 验证「应用级」行为，而不只是「进程级」行为
//
//   A. 配置读写（中文、注释、往返一致）
//   B. 就绪探针：端口监听 → Starting 必须推进到 Running
//   C. 停止三态之一：应用自己的停止命令
//   D. 停止三态之二：往伪控制台送 Ctrl+C
//   E. 停止三态之三：前两段失效后的强制回收
//   F. 端口冲突预检：不能只报一个裸 PID
// ============================================================================

Console.OutputEncoding = Encoding.UTF8;

const int PortReady = 38430;
const int PortStoppable = 38431;
const int PortControl = 38432;
const int PortConflict = 38433;

var failures = new List<string>();
var pass = 0;
const int totalChecks = 8;

var workDir = Path.Combine(Path.GetTempPath(), "processdeck-supervisor-test");
Directory.CreateDirectory(workDir);

WritePythonScripts(workDir);

// ---------------- A. 配置读写 ----------------
Console.WriteLine("=== A. 配置读写（中文 / 注释 / 往返一致）===");
{
    var configPath = Path.Combine(workDir, "deck.json");
    File.Delete(configPath);

    var store = new ConfigurationStore(configPath);
    var configuration = new DeckConfiguration
    {
        Theme = "dark",
        Apps =
        {
            new AppDefinition
            {
                Id = "demo",
                Name = "示例：前端开发服务器",
                Description = "带中文描述与注释的配置",
                StartCommand = "cmd.exe /c pnpm dev",
                StopCommand = "pnpm dev --stop",
                Port = 5173,
                Environment = { ["NODE_ENV"] = "development" },
                Readiness = new ReadinessProbeDefinition
                {
                    Kind = ReadinessKind.Log,
                    Pattern = "ready in",
                },
            },
        },
    };

    store.Save(configuration);

    // 手工插入注释与尾逗号，验证宽松解析
    var raw = File.ReadAllText(configPath);
    File.WriteAllText(configPath, "// 这是用户手写的注释\n" + raw.Replace("\"theme\": \"dark\",", "\"theme\": \"light\","));

    var reloaded = store.Load(out var loadError);

    var ok = loadError is null
             && reloaded.Apps.Count == 1
             && reloaded.Apps[0].Name == "示例：前端开发服务器"
             && reloaded.Theme == "light"
             && reloaded.Apps[0].Environment["NODE_ENV"] == "development"
             && reloaded.Apps[0].Readiness.Kind == ReadinessKind.Log;

    if (ok)
    {
        Console.WriteLine("  → 中文未被转义、注释被跳过、枚举与字典往返正确");
        pass++;
    }
    else
    {
        failures.Add($"配置往返失败：loadError={loadError} apps={reloaded.Apps.Count} theme={reloaded.Theme}");
    }

    // 非法配置的校验
    var bad = new AppDefinition { Name = "", StartCommand = "" };
    var problems = bad.Validate();
    if (problems.Count >= 2)
    {
        Console.WriteLine($"  → 非法配置被拦下：{string.Join(" ", problems)}");
        pass++;
    }
    else
    {
        failures.Add("非法配置没有被校验拦下");
    }
}

// ---------------- B. 就绪探针 ----------------
Console.WriteLine();
Console.WriteLine("=== B. 就绪探针：端口监听才推进到 Running ===");
{
    var definition = new AppDefinition
    {
        Id = "port-ready",
        Name = "端口就绪演示",
        StartCommand = $"python \"{Path.Combine(workDir, "svc_port.py")}\" {PortReady}",
        Port = PortReady,
        Readiness = new ReadinessProbeDefinition
        {
            Kind = ReadinessKind.Port,
            TimeoutSeconds = 25,
            IntervalMs = 200,
        },
        StopTimeoutSeconds = 5,
    };

    using var supervisor = new AppSupervisor(definition);
    await supervisor.StartAsync();

    if (supervisor.State == AppState.Running && supervisor.OccupiedPorts().ContainsKey(PortReady))
    {
        Console.WriteLine($"  → 状态={supervisor.State}，端口 {PortReady} 归属到作业 " +
                          $"(PID {supervisor.OccupiedPorts()[PortReady]})，成员={supervisor.MemberProcessIds.Count} 个");
        pass++;
    }
    else
    {
        failures.Add($"就绪探针失败：state={supervisor.State} err={supervisor.LastError}");
    }

    await supervisor.StopAsync();
    Thread.Sleep(600);

    if (supervisor.State == AppState.Stopped && !PortScanner.IsPortInUse(PortReady))
    {
        Console.WriteLine($"  → 停止后状态={supervisor.State}，端口已释放，停止阶段={supervisor.LastStopStage}");
        pass++;
    }
    else
    {
        failures.Add($"停止失败：state={supervisor.State} portInUse={PortScanner.IsPortInUse(PortReady)}");
    }
}

// ---------------- C. 停止三态①：停止命令 ----------------
Console.WriteLine();
Console.WriteLine("=== C. 停止三态①：应用自己的停止命令 ===");
{
    var definition = new AppDefinition
    {
        Id = "stoppable",
        Name = "可优雅停止的服务",
        StartCommand = $"python \"{Path.Combine(workDir, "svc_stoppable.py")}\" {PortStoppable} {PortControl}",
        StopCommand = $"python \"{Path.Combine(workDir, "stop_client.py")}\" {PortControl}",
        Port = PortStoppable,
        Readiness = new ReadinessProbeDefinition { Kind = ReadinessKind.Port, TimeoutSeconds = 25 },
        StopTimeoutSeconds = 10,
    };

    using var supervisor = new AppSupervisor(definition);
    await supervisor.StartAsync();

    if (supervisor.State != AppState.Running)
    {
        failures.Add($"C 阶段启动失败：{supervisor.LastError}");
    }
    else
    {
        await supervisor.StopAsync();

        if (supervisor.LastStopStage == StopStage.StopCommand && supervisor.State == AppState.Stopped)
        {
            Console.WriteLine("  → 由应用自己的停止命令优雅退出（没有动到 Ctrl+C 或强杀）");
            pass++;
        }
        else
        {
            failures.Add($"C 阶段期望 StopCommand，实际 {supervisor.LastStopStage}（state={supervisor.State}）");
        }
    }
}

// ---------------- D. 停止三态②：Ctrl+C ----------------
Console.WriteLine();
Console.WriteLine("=== D. 停止三态②：向伪控制台发送 Ctrl+C ===");
{
    var definition = new AppDefinition
    {
        Id = "sigint",
        Name = "响应 Ctrl+C 的服务",
        StartCommand = $"python \"{Path.Combine(workDir, "svc_sigint.py")}\"",
        // 刻意不提供 StopCommand，逼它走信号路径
        Readiness = new ReadinessProbeDefinition { Kind = ReadinessKind.None },
        StartGraceSeconds = 1,
        StopTimeoutSeconds = 10,
    };

    using var supervisor = new AppSupervisor(definition);
    await supervisor.StartAsync();

    if (supervisor.State != AppState.Running)
    {
        failures.Add($"D 阶段启动失败：{supervisor.LastError}");
    }
    else
    {
        await supervisor.StopAsync();

        if (supervisor.LastStopStage == StopStage.Interrupt)
        {
            Console.WriteLine("  → 写 \\u0003 进伪控制台后进程自行退出，等价于用户在终端按 Ctrl+C");
            pass++;
        }
        else
        {
            failures.Add($"D 阶段期望 Interrupt，实际 {supervisor.LastStopStage}");
        }
    }
}

// ---------------- E. 停止三态③：强杀 ----------------
Console.WriteLine();
Console.WriteLine("=== E. 停止三态③：忽略 Ctrl+C 时强制回收 ===");
{
    var definition = new AppDefinition
    {
        Id = "stubborn",
        Name = "顽固服务（忽略 Ctrl+C）",
        StartCommand = $"python \"{Path.Combine(workDir, "svc_stubborn.py")}\"",
        Readiness = new ReadinessProbeDefinition { Kind = ReadinessKind.None },
        StartGraceSeconds = 1,
        // 压低超时让测试快一点；真实场景默认 15 秒
        StopTimeoutSeconds = 3,
    };

    using var supervisor = new AppSupervisor(definition);
    await supervisor.StartAsync();

    if (supervisor.State != AppState.Running)
    {
        failures.Add($"E 阶段启动失败：{supervisor.LastError}");
    }
    else
    {
        var startedAt = DateTime.UtcNow;
        await supervisor.StopAsync();
        var elapsed = DateTime.UtcNow - startedAt;

        if (supervisor.LastStopStage == StopStage.ForceKill
            && supervisor.State == AppState.Stopped
            && supervisor.ActiveProcessCount == 0)
        {
            Console.WriteLine($"  → 前两段无效后由内核回收整棵树，耗 {elapsed.TotalSeconds:F1} 秒，残留 0 个进程");
            pass++;
        }
        else
        {
            failures.Add($"E 阶段期望 ForceKill，实际 {supervisor.LastStopStage}，" +
                         $"state={supervisor.State} 残留={supervisor.ActiveProcessCount}");
        }
    }
}

// ---------------- F. 端口冲突预检 ----------------
Console.WriteLine();
Console.WriteLine("=== F. 端口冲突预检 ===");
{
    var first = new AppDefinition
    {
        Id = "occupier",
        Name = "先占用端口的服务",
        StartCommand = $"python \"{Path.Combine(workDir, "svc_port.py")}\" {PortConflict}",
        Port = PortConflict,
        Readiness = new ReadinessProbeDefinition { Kind = ReadinessKind.Port, TimeoutSeconds = 25 },
        StopTimeoutSeconds = 5,
    };

    using var occupier = new AppSupervisor(first);
    await occupier.StartAsync();

    if (occupier.State != AppState.Running)
    {
        failures.Add($"F 阶段第一个应用没起来：{occupier.LastError}");
    }
    else
    {
        var second = new AppDefinition
        {
            Id = "conflicted",
            Name = "端口被占的服务",
            StartCommand = $"python \"{Path.Combine(workDir, "svc_port.py")}\" {PortConflict}",
            Port = PortConflict,
            Readiness = new ReadinessProbeDefinition { Kind = ReadinessKind.Port, TimeoutSeconds = 5 },
        };

        using var conflicted = new AppSupervisor(second);
        await conflicted.StartAsync();

        var message = conflicted.LastError ?? string.Empty;

        if (conflicted.State == AppState.Failed
            && message.Contains("占用", StringComparison.Ordinal)
            && message.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  → 启动被拒绝，且报出的是「谁占了端口」而不是一个裸 PID：");
            Console.WriteLine($"     {message}");
            pass++;
        }
        else
        {
            failures.Add($"F 阶段期望 Failed 且带占用者信息，实际 state={conflicted.State} err={message}");
        }
    }

    await occupier.StopAsync();
    Thread.Sleep(600);
}

// ---------------- 结果 ----------------
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS：{pass}/{totalChecks} 项通过。配置、就绪探针、停止三态、端口预检均可用。");
    return 0;
}

Console.WriteLine($"FAIL：{failures.Count} 项失败（通过 {pass}/{totalChecks}）");
foreach (var failure in failures)
{
    Console.WriteLine($"  - {failure}");
}

return 1;

// ============================================================================
// 辅助
// ============================================================================

static void WritePythonScripts(string directory)
{
    File.WriteAllText(Path.Combine(directory, "svc_port.py"), """
        import socket, sys, time
        port = int(sys.argv[1])
        s = socket.socket()
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind(("127.0.0.1", port))
        s.listen(5)
        print("listening on %d" % port, flush=True)
        while True:
            time.sleep(0.5)
        """);

    File.WriteAllText(Path.Combine(directory, "svc_stoppable.py"), """
        import socket, sys
        service_port = int(sys.argv[1])
        control_port = int(sys.argv[2])

        svc = socket.socket()
        svc.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        svc.bind(("127.0.0.1", service_port))
        svc.listen(5)

        ctl = socket.socket()
        ctl.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        ctl.bind(("127.0.0.1", control_port))
        ctl.listen(1)

        print("service ready on %d" % service_port, flush=True)
        ctl.accept()
        print("stop requested, exiting cleanly", flush=True)
        sys.exit(0)
        """);

    File.WriteAllText(Path.Combine(directory, "stop_client.py"), """
        import socket, sys
        s = socket.socket()
        s.connect(("127.0.0.1", int(sys.argv[1])))
        s.close()
        """);

    File.WriteAllText(Path.Combine(directory, "svc_sigint.py"), """
        import time
        print("sigint service ready", flush=True)
        while True:
            time.sleep(0.5)
        """);

    File.WriteAllText(Path.Combine(directory, "svc_stubborn.py"), """
        import signal, time
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        print("stubborn service ready (ignoring Ctrl+C)", flush=True)
        while True:
            time.sleep(0.5)
        """);
}
