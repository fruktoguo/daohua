using System.Text.Json;

namespace Xiuxian.LoadTests;

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
            _ => throw new ArgumentException($"未知场景: {options.Scenario}")
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
    }
}
