using System.Text.Json;

namespace Daoxuan.LoadTests;

public static class Program
{
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
