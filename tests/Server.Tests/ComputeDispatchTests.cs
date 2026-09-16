using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 主服务侧 compute.v1 派发边界单测：令牌头、端点路径、响应分类与 section 线编码校验。
/// 通过 fake HttpMessageHandler 验证，不依赖真实 ComputeNode 进程或数据库。
/// </summary>
public sealed class ComputeDispatchTests
{
    /// <summary>请求必须携带令牌头、正确的端点路径与可解析的 compute.v1 批次体。</summary>
    [Fact]
    public async Task AdvanceBatchSendsTokenHeaderAndParsesResponse()
    {
        HttpRequestMessage? captured = null;
        byte[]? capturedBody = null;
        var response = new ComputeAdvanceBatchResponse(
            ComputeDispatchContract.Version, "fp-node", 1_700_000_000_000,
            [new ComputeAdvanceJobResult(Guid.NewGuid(), Guid.NewGuid(), true, 7, 8, [], null, null, null, false)]);
        var responseBody = JsonSerializer.Serialize(response, ServerJson.Options);
        var client = NewClient(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, responseBody);

        var outcome = await client.AdvanceBatchAsync("http://node:8090/", "secret-token",
            new ComputeAdvanceBatchRequest(ComputeDispatchContract.Version, "fp-server",
                [NewJobRequest()]), CancellationToken.None);

        Assert.Equal(ComputeDispatchStatus.Ok, outcome.Status);
        Assert.Single(outcome.Response!.Jobs);
        Assert.NotNull(captured);
        Assert.Equal("/internal/v1/compute/advance-batch", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("secret-token",
            captured.Headers.GetValues(ComputeDispatchContract.TokenHeader).Single());
        var sent = JsonSerializer.Deserialize<ComputeAdvanceBatchRequest>(capturedBody, ServerJson.Options);
        Assert.NotNull(sent);
        Assert.Equal("fp-server", sent!.GameDataFingerprint);
        Assert.Single(sent.Jobs);
    }

    /// <summary>非 2xx 响应映射为 HttpError，并保留状态码供退避分类。</summary>
    [Fact]
    public async Task AdvanceBatchMapsHttpError()
    {
        var client = NewClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var outcome = await client.AdvanceBatchAsync("http://node:8090", "t",
            new ComputeAdvanceBatchRequest(ComputeDispatchContract.Version, null, []), CancellationToken.None);
        Assert.Equal(ComputeDispatchStatus.HttpError, outcome.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, outcome.HttpStatus);
    }

    /// <summary>契约版本不匹配的响应一律拒绝，不得按成功批次处理。</summary>
    [Fact]
    public async Task AdvanceBatchRejectsForeignContractVersion()
    {
        var client = NewClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            """{"contractVersion":"compute.v0","gameDataFingerprint":"x","serverTimeUnixMilliseconds":0,"jobs":[]}""");
        var outcome = await client.AdvanceBatchAsync("http://node:8090", "t",
            new ComputeAdvanceBatchRequest(ComputeDispatchContract.Version, null, []), CancellationToken.None);
        Assert.Equal(ComputeDispatchStatus.InvalidResponse, outcome.Status);
    }

    /// <summary>网络异常归入 NetworkError，由派发器按租约整批退回。</summary>
    [Fact]
    public async Task AdvanceBatchClassifiesNetworkError()
    {
        var client = new ComputeNodeClient(new HttpClient(new ThrowingHandler()));
        var outcome = await client.AdvanceBatchAsync("http://node:8090", "t",
            new ComputeAdvanceBatchRequest(ComputeDispatchContract.Version, null, []), CancellationToken.None);
        Assert.Equal(ComputeDispatchStatus.NetworkError, outcome.Status);
    }

    /// <summary>存储 codec 到线 codec 的映射必须一致，未知存储编码拒绝外传。</summary>
    [Fact]
    public void ToWireMapsStorageCodecs()
    {
        var raw = Encoding.UTF8.GetBytes("{}");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        var stored = new StoredSection(Guid.NewGuid(), PlayerSectionKinds.Settings, 3,
            SectionCodec.CurrentSchemaVersion, SectionCodec.RawJson, raw, raw.Length, checksum, DateTimeOffset.UtcNow);
        var wire = ComputeSectionCodec.ToWire(stored);
        Assert.Equal("identity", wire.Codec);
        Assert.Equal(checksum, wire.Checksum);

        var unknown = stored with { Codec = "lz4" };
        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToWire(unknown));
    }

    /// <summary>节点回传 section 必须通过编码字节校验和才能还原为 SectionWrite。</summary>
    [Fact]
    public void ToSectionWriteValidatesEncodedChecksum()
    {
        var player = Guid.NewGuid();
        var raw = Encoding.UTF8.GetBytes("{\"presence\":0}");
        var encoded = Convert.ToBase64String(raw);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));

        var write = ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload(PlayerSectionKinds.Settings, 8, "identity", encoded, checksum),
            DateTimeOffset.UtcNow);
        Assert.Equal(SectionCodec.RawJson, write.Codec);
        Assert.Equal(raw.Length, write.RawBytes);
        Assert.Equal(9, write.StateVersion);

        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload(PlayerSectionKinds.Settings, 8, "identity", encoded,
                new string('0', 64)), DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload("unknown.kind", 8, "identity", encoded, checksum),
            DateTimeOffset.UtcNow));
    }

    private static ComputeAdvanceJobRequest NewJobRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 7, 1_000, 2_000,
            [new ComputeSectionPayload(PlayerSectionKinds.Settings, 1, "identity",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")), new string('a', 64))]);

    private static ComputeNodeClient NewClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, string responseBody = "{}") =>
        new(new HttpClient(new StubHandler(async request =>
        {
            var response = await respond(request);
            if (response.StatusCode == HttpStatusCode.OK)
                response.Content = new StringContent(responseBody, Encoding.UTF8, "application/json");
            return response;
        })));

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            await respond(request);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("连接被拒");
    }
}
