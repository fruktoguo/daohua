using System.Reflection;
using System.Text.RegularExpressions;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 双侧手写契约副本对齐门禁：Server.Clean.ComputeLinkContracts / ComputeDispatchContract
/// 与 src/ComputeNode/ComputeLinkContracts.cs / ComputeContract.cs 是同一协议的私有副本。
/// ComputeNode 无项目引用无法共享程序集（链接同一 .cs 需统一命名空间与序列化口径，代价大于收益），
/// 因此直接比对节点源文件中记录的字段名（camelCase 线字段）与关键常量，任一侧漂移即红灯。
/// </summary>
public sealed class ComputeLinkContractAlignmentTests
{
    /// <summary>compute-link 链路信封七帧：两侧记录字段集必须一致，版本/帧上限/type 判别逐字对齐。</summary>
    [Fact]
    public void LinkEnvelopeRecordsMatchNodeSide()
    {
        var source = NodeSource("ComputeLinkContracts.cs");
        AssertRecordFieldsMatch<ComputeLinkHello>(source, "LinkHello");
        AssertRecordFieldsMatch<ComputeLinkWelcome>(source, "LinkWelcome");
        AssertRecordFieldsMatch<ComputeLinkBatch>(source, "LinkBatch");
        AssertRecordFieldsMatch<ComputeLinkResult>(source, "LinkResult");
        AssertRecordFieldsMatch<ComputeLinkError>(source, "LinkError");
        AssertRecordFieldsMatch<ComputeLinkDrain>(source, "LinkDrain");
        AssertRecordFieldsMatch<ComputeLinkDraining>(source, "LinkDraining");

        Assert.Equal("compute-link.v1.3", ComputeLinkContract.Version);
        Assert.Contains("public const string Version = \"compute-link.v1.3\";", source);
        Assert.Contains("public const string TokenHeader = ComputeContract.TokenHeader;", source);
        Assert.Contains("public const int MaxMessageBytes = 20 * 1024 * 1024;", source);
        Assert.Equal(20 * 1024 * 1024, ComputeLinkContract.MaxMessageBytes);
        foreach (var discriminator in new[] { "\"hello\"", "\"welcome\"", "\"batch\"", "\"result\"", "\"error\"", "\"drain\"", "\"draining\"" })
            Assert.Contains(discriminator, source);
    }

    /// <summary>compute.v2 批次契约全部记录：两侧字段集与硬上限常量必须一致。</summary>
    [Fact]
    public void ComputeBatchContractRecordsMatchNodeSide()
    {
        var source = NodeSource("ComputeContract.cs");
        AssertRecordFieldsMatch<ComputeAdvanceBatchRequest>(source, "AdvanceBatchRequest");
        AssertRecordFieldsMatch<ComputeAdvanceJobRequest>(source, "AdvanceJobRequest");
        AssertRecordFieldsMatch<ComputeSectionPayload>(source, "SectionPayload");
        AssertRecordFieldsMatch<ComputeAssetBalance>(source, "AssetBalanceRow");
        AssertRecordFieldsMatch<ComputeAssetDelta>(source, "AssetDeltaRow");
        AssertRecordFieldsMatch<ComputeAdvanceBatchResponse>(source, "AdvanceBatchResponse");
        AssertRecordFieldsMatch<ComputeAdvanceJobResult>(source, "AdvanceJobResult");
        AssertRecordFieldsMatch<ComputeAdvanceMetrics>(source, "ComputeAdvanceMetrics");

        Assert.Equal("compute.v2", ComputeDispatchContract.Version);
        Assert.Contains("public const string Version = \"compute.v2\";", source);
        Assert.Equal(ComputeDispatchContract.TokenHeader, "X-Compute-Token");
        Assert.Contains("public const string TokenHeader = \"X-Compute-Token\";", source);
        Assert.Equal(256, ComputeDispatchContract.HardMaxJobsPerBatch);
        Assert.Contains("public const int HardMaxJobs = 256;", source);
        Assert.Equal(16L * 1024 * 1024, ComputeDispatchContract.MaxBatchBytes);
        Assert.Contains("public const long MaxBatchBytes = 16L * 1024 * 1024;", source);
        Assert.Equal(32, ComputeDispatchContract.MaxSectionsPerJob);
        Assert.Contains("public const int MaxSectionsPerJob = 32;", source);
    }

    /// <summary>节点侧批次字节估算必须是解码后口径（字符数 ×3/4），防回归卡回 ~1/4 上限。</summary>
    [Fact]
    public void NodeBatchByteEstimateIsDecodedSize()
    {
        var source = NodeSource("ComputeContract.cs");
        Assert.Contains("* 3 / 4", source);
        Assert.DoesNotContain("* 3 > MaxBatchBytes", source);
        Assert.Contains("totalBytes > MaxBatchBytes", source);
    }

    /// <summary>比较服务端记录属性名（camelCase 线字段）与节点源文件中同名记录的参数名集合。</summary>
    private static void AssertRecordFieldsMatch<TServer>(string nodeSource, string nodeRecordName)
    {
        var server = typeof(TServer)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => CamelCase(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var node = ParseRecordParamNames(nodeSource, nodeRecordName)
            .Select(CamelCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(server, node);
    }

    /// <summary>从节点源文件提取指定 record 的位置参数名列表。</summary>
    private static string[] ParseRecordParamNames(string source, string recordName)
    {
        var match = Regex.Match(source, $@"record\s+{recordName}\s*\((.*?)\)", RegexOptions.Singleline);
        Assert.True(match.Success, $"节点源文件缺少 record {recordName}。");
        return match.Groups[1].Value
            .Split(',')
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment =>
            {
                var name = Regex.Match(segment, @"(\w+)\s*(?:=.*)?$");
                Assert.True(name.Success, $"无法解析 {recordName} 参数：{segment}。");
                return name.Groups[1].Value;
            })
            .ToArray();
    }

    private static string CamelCase(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private static string NodeSource(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "ComputeNode", file));

    /// <summary>从测试输出目录向上定位仓库根（以 Game.slnx 为标志）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Game.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
