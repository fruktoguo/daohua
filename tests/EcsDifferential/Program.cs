using DaoHua.EcsDifferential;
using YuoCore.Ecs;

// EcsDifferential：双内核差分测试台。
//   --seed <n> --ops <m> --out <file>   随机操作序列台账
//   --directed --out <file>             定向边界用例台账
//   --variant <name>                    仅写入 meta，便于排查台账来源

int? seed = null;
var ops = 6000;
string outPath = null;
var directed = false;
var variant = "unknown";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--ops": ops = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--directed": directed = true; break;
        case "--variant": variant = args[++i]; break;
        default:
            Console.Error.WriteLine($"未知参数: {args[i]}");
            return 2;
    }
}

if (outPath == null)
{
    Console.Error.WriteLine("缺少 --out <file>");
    return 2;
}

// 让系统回调异常直接抛出到操作层记录，而不是被内核静默吞掉跳过实体。
YuoWorld.CloseSystemTry = true;

try
{
    if (directed)
    {
        using var ledger = new Ledger(outPath);
        ledger.Meta($"\"variant\":\"{variant}\",\"mode\":\"directed\"");
        DirectedCases.RunAll(ledger);
        ledger.Final(Runner.CountsPayload());
    }
    else
    {
        if (seed == null)
        {
            Console.Error.WriteLine("缺少 --seed <n>");
            return 2;
        }

        using var ledger = new Ledger(outPath);
        ledger.Meta($"\"variant\":\"{variant}\",\"mode\":\"random\",\"seed\":{seed},\"ops\":{ops}");
        var runner = new Runner(seed.Value, ops, ledger);
        runner.Run();
    }
}
catch (Exception e)
{
    // 兜底：进程级崩溃也要在台账里留痕，保证 diff 能定位崩溃点。
    try
    {
        File.AppendAllText(outPath,
            $"{{\"t\":\"crash\",\"et\":\"{e.GetType().Name}\",\"m\":\"{Ledger.Esc(e.Message)}\"}}\n");
    }
    catch
    {
        // ignored
    }

    Console.Error.WriteLine(e);
    return 1;
}

return 0;
