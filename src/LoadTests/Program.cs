namespace DaoHua.LoadTests;

/// <summary>
/// 压测工具入口。
/// </summary>
public static class Program
{
    /// <summary>
    /// 压测程序主入口。
    /// </summary>
    /// <param name="args">命令行参数（--key value 形式；--flag 无值时按 true 解析）。</param>
    /// <returns>进程退出码；门禁通过返回 0，否则返回 1，参数错误返回 2。</returns>
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
                "websocket" => await WebSocketScenario.RunAsync(options),
                "http-read" => await HttpReadScenario.RunAsync(options),
                "compute" => await ComputeLoadScenario.RunReportAsync(options),
                "postgres" => await PostgresCapacityProbe.RunReportAsync(options),
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
