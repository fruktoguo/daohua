using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 传书邮件协议与信封编解码测试：
/// 覆盖 MailSummary 帧二进制往返及列表/详情/领取/删除 DTO 序列化契约。
/// </summary>
public sealed class MailEnvelopeTests
{
    [Fact]
    public void MailSummaryEnvelope_RoundTripsWithForceRedDot()
    {
        var requestId = Guid.NewGuid();
        var summary = new MailSummaryDto(UnreadCount: 3, ClaimableCount: 2, ForceRedDot: true);
        var envelope = WireEnvelope.Json(WireMessageType.MailSummary, requestId, 42, summary);

        var encoded = WireEnvelopeCodec.Encode(envelope);
        Assert.True(WireEnvelopeCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(WireMessageType.MailSummary, decoded.MessageType);
        Assert.Equal(29, (byte)decoded.MessageType);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.Equal(42, decoded.Sequence);

        Assert.True(WireEnvelopeCodec.TryDeserializeJson<MailSummaryDto>(decoded, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.UnreadCount);
        Assert.Equal(2, parsed.ClaimableCount);
        Assert.True(parsed.ForceRedDot);

        using var doc = JsonDocument.Parse(decoded.Payload);
        Assert.Equal(3, doc.RootElement.GetProperty("unreadCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("claimableCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("forceRedDot").GetBoolean());
    }

    [Fact]
    public void MailRestDtos_SerializeWithExpectedCamelCaseKeys()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var listItem = new MailListItemDto(
            MailKey: "b:17",
            Kind: "broadcast",
            Title: "开宗大礼",
            SenderName: "道化司",
            CreatedAt: now,
            ExpiresAt: now.AddDays(30),
            HasAttachments: true,
            IsRead: false,
            IsClaimed: false,
            OnlyExistingPlayers: true,
            Attachments: [new MailAttachmentDto("currency.spirit-stone", 500)]);

        var json = JsonSerializer.Serialize(new MailListResponseDto(
            Mails: [listItem],
            Summary: new MailSummaryDto(1, 1)), ProtocolJson.Options);

        using var doc = JsonDocument.Parse(json);
        var mail = doc.RootElement.GetProperty("mails")[0];
        Assert.Equal("b:17", mail.GetProperty("mailKey").GetString());
        Assert.Equal("broadcast", mail.GetProperty("kind").GetString());
        Assert.False(mail.TryGetProperty("body", out _));
        Assert.Equal("currency.spirit-stone", mail.GetProperty("attachments")[0].GetProperty("itemId").GetString());
        Assert.Equal(500d, mail.GetProperty("attachments")[0].GetProperty("quantity").GetDouble());

        var detail = new MailDetailDto(
            listItem.MailKey,
            listItem.Kind,
            listItem.Title,
            "正文中包含 {{item:currency.spirit-stone|灵石}} 引用。",
            listItem.SenderName,
            listItem.CreatedAt,
            listItem.ExpiresAt,
            listItem.HasAttachments,
            IsRead: true,
            IsClaimed: false,
            listItem.OnlyExistingPlayers,
            listItem.Attachments);

        var restored = JsonSerializer.Deserialize<MailDetailResponseDto>(
            JsonSerializer.Serialize(new MailDetailResponseDto(detail, new MailSummaryDto(0, 1)), ProtocolJson.Options),
            ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(detail.Body, restored!.Mail.Body);
        Assert.Equal(1, restored.Summary.ClaimableCount);

        var permanentItem = listItem with { ExpiresAt = null, OnlyExistingPlayers = false };
        var permJson = JsonSerializer.Serialize(permanentItem, ProtocolJson.Options);
        var permRestored = JsonSerializer.Deserialize<MailListItemDto>(permJson, ProtocolJson.Options);
        Assert.NotNull(permRestored);
        Assert.Null(permRestored!.ExpiresAt);
        Assert.False(permRestored.OnlyExistingPlayers);
    }
}
