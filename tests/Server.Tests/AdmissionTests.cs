using Protocol;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Server.Clean.Admission;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 会话准入中性门禁：Hello 协议模块声明字段信封往返、默认开放语义
/// （多连接并存、首会话登记、按实例配对释放）与模块启动器发现（开源构建为零）。
/// 反多开私有裁决语义的门禁位于 DaoHua-Private/tests。
/// </summary>
public sealed class AdmissionTests
{
    /// <summary>验证 Hello 载荷的模块声明与准入配置字段经二进制信封 JSON 编解码完整往返（协议对齐门禁）。</summary>
    [Fact]
    public void Hello_Envelope_RoundTrips_ModuleDeclaration_And_Config()
    {
        var requestId = Guid.NewGuid();
        var requestEnvelope = WireEnvelope.Json(WireMessageType.Hello, requestId, 1,
            new HelloRequest("dev", "fp-data", "fp-device", "fp-config", "id-device", "daohua.private.admission.v1"));
        Assert.True(WireEnvelopeCodec.TryDecode(WireEnvelopeCodec.Encode(requestEnvelope), out var decodedRequest));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<HelloRequest>(decodedRequest, out var hello));
        Assert.Equal("fp-device", hello!.DeviceFingerprint);
        Assert.Equal("fp-data", hello.GameDataFingerprint);
        Assert.Equal("fp-config", hello.DeviceConfigHash);
        Assert.Equal("id-device", hello.DeviceId);
        Assert.Equal("daohua.private.admission.v1", hello.ModuleDeclaration);

        // 未携带声明的开源构建：字段缺省为 null，编解码不丢失其余载荷。
        var bareEnvelope = WireEnvelope.Json(WireMessageType.Hello, requestId, 2, new HelloRequest("dev"));
        Assert.True(WireEnvelopeCodec.TryDecode(WireEnvelopeCodec.Encode(bareEnvelope), out var decodedBare));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<HelloRequest>(decodedBare, out var bareHello));
        Assert.Null(bareHello!.ModuleDeclaration);

        var responseEnvelope = WireEnvelope.Json(WireMessageType.HelloAck, requestId, 3,
            new HelloResponse("1.0.0", ProtocolConstants.CurrentVersion, "fp-data", 42, new AntiMultiOpenConfigDto(true)));
        Assert.True(WireEnvelopeCodec.TryDecode(WireEnvelopeCodec.Encode(responseEnvelope), out var decodedResponse));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<HelloResponse>(decodedResponse, out var ack));
        Assert.NotNull(ack!.AntiMultiOpen);
        Assert.True(ack.AntiMultiOpen!.TabGuardEnabled);
    }

    /// <summary>开放语义：同账号多连接并存放行，首会话登记供广播与运维可见。</summary>
    [Fact]
    public void OpenHook_AdmitsCoexistingSessions_And_RegistersFirst()
    {
        var sessions = new SessionRegistry();
        var hook = new OpenHelloAdmissionHook(sessions);
        var playerId = Guid.NewGuid();
        var first = new WebSocketSession(playerId, 8);
        var second = new WebSocketSession(playerId, 8);

        Assert.True(hook.Admit(first, null, null, AccountRiskPolicy.Default).Accepted);
        Assert.True(hook.Admit(second, null, null, AccountRiskPolicy.Default).Accepted);
        // 只有首个会话进入注册表；第二个连接静默共存（可见性归属首个会话）。
        Assert.Equal(1, sessions.Count);
        Assert.True(sessions.TryGet(playerId, out var registered) && ReferenceEquals(first, registered));

        // 后续会话释放不摘除登记中的首会话；首会话释放才真正移除。
        Assert.False(hook.Release(second));
        Assert.Equal(1, sessions.Count);
        Assert.True(hook.Release(first));
        Assert.Equal(0, sessions.Count);
    }

    /// <summary>开放语义：不下发标签页互斥开关。</summary>
    [Fact]
    public void OpenHook_NeverPublishesTabGuard()
    {
        Assert.False(new OpenHelloAdmissionHook(new SessionRegistry()).TabGuardEnabled(AccountRiskPolicy.Default));
    }
}
