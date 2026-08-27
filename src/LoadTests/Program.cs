namespace DaoHua.LoadTests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LoadTestOptions options;
        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            Console.Error.WriteLine($"参数错误: {exception.Message}");
            return 2;
        }

        LoadTestReport report;
        try
        {
            report = options.Scenario switch
            {
                "capacity-target" => await CapacityTargetScenario.RunAsync(options),
                "postgres" => await CapacityTargetScenario.RunPostgresOnlyAsync(options),
                "compute" => await ComputeLoadScenario.RunAsync(options),
                "websocket" => await WebSocketLoadScenario.RunAsync(options),
                "http-read" => await HttpReadLoadScenario.RunAsync(options),
                _ => throw new ArgumentException($"未知场景: {options.Scenario}")
            };
        }
        catch (OperationCanceledException)
        {
            report = LoadTestReport.Failure(options.Scenario, "压测被取消。");
        }
        catch (Exception exception)
        {
            report = LoadTestReport.Failure(options.Scenario, exception.Message);
        }

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
        return report.GatePassed && report.Errors == 0 ? 0 : 1;
    }
}
