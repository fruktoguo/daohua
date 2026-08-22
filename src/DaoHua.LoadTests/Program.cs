using System.Text.Json;

namespace DaoHua.LoadTests;

/// <summary>
/// 负载与容量压测控制台入口程序，负责解析命令行参数、分发压测场景并输出标准化 JSON 压测报告。
/// </summary>
public static class Program
{
    /// <summary>
    /// 压测命令行主执行入口：
    /// 1. 解析命令行选项并匹配目标压测场景（在线长连接、离线挂机、多怪战斗、灵市撮合/查询/过期、2c4g容量门禁）；
    /// 2. 调度执行场景并捕获运行耗时、网络收发、内存与 CPU 指标；
    /// 3. 输出格式化 JSON 报告，发生错误时返回非零退出码。
    /// </summary>
    public static async Task Main(string[] args)
    {
        var options = LoadTestOptions.Parse(args);
        var report = options.Scenario switch
        {
            "online" => await OnlineLoadScenario.RunAsync(options),
            "idle-offline" => OfflineSimulationScenario.Run(options, fullyOffline: false),
            "combat-offline" => OfflineSimulationScenario.Run(options, fullyOffline: false, includeCombat: true),
            "fully-offline" => OfflineSimulationScenario.Run(options, fullyOffline: true),
            "market" => MarketLoadScenario.Run(options),
            "market-read" => await MarketReadLoadScenario.RunAsync(options),
            "market-expiry" => MarketExpiryLoadScenario.Run(options),
            "capacity-target" => CapacityTargetScenario.Run(options),
            _ => throw new ArgumentException($"未知场景: {options.Scenario}")
        };

        if (report.Errors > 0)
        {
            Environment.ExitCode = 1;
        }

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
    }
}
