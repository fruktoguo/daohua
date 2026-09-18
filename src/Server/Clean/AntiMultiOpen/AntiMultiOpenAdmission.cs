namespace Server.Clean.AntiMultiOpen;

/// <summary>会话准入裁决结果。</summary>
/// <param name="Accepted">是否准入</param>
/// <param name="KickedSession">准入成功且顶替了同账号旧会话时的旧会话实例</param>
/// <param name="ErrorCode">拒绝时的协议错误码</param>
/// <param name="Message">拒绝时可展示给用户的提示</param>
/// <param name="CloseCode">拒绝时应使用的 WebSocket 关闭码</param>
public sealed record AntiMultiOpenAdmissionResult(
    bool Accepted,
    WebSocketSession? KickedSession,
    string? ErrorCode,
    string? Message,
    int? CloseCode)
{
    /// <summary>构造拒绝结果</summary>
    public static AntiMultiOpenAdmissionResult Reject(string errorCode, string message, int closeCode) =>
        new(false, null, errorCode, message, closeCode);
}

/// <summary>
/// 防多开会话准入仲裁器：在 Hello 握手时统一裁决「设备配额 + 单账号会话策略」。
/// 设计要点：
/// 1. 准入判定必须等到 Hello——设备指纹只随 Hello 载荷到达，accept 阶段无法裁决配额；
/// 2. 互踢开启时先做配额校验再顶替旧会话，配额失败的登录不会无谓踢掉在线连接；
/// 3. 互踢关闭时先做单会话登记再校验配额，配额失败时回滚登记，旧连接全程不受影响；
/// 4. 会话释放按实例配对，被顶替的旧会话拆除时不会误删新会话已加载的玩家 Actor。
/// </summary>
/// <param name="options">防多开开关与配额配置</param>
/// <param name="sessions">在线会话注册表</param>
/// <param name="devices">设备配额注册表</param>
public sealed class AntiMultiOpenAdmission(
    AntiMultiOpenOptions options,
    SessionRegistry sessions,
    DeviceQuotaRegistry devices)
{
    /// <summary>
    /// 对完成身份认证的会话执行准入裁决；仅在 Hello 处理流程中调用一次。
    /// </summary>
    /// <param name="session">待准入的会话</param>
    /// <param name="deviceFingerprint">客户端 Hello 上报的设备指纹（可为 null）</param>
    public AntiMultiOpenAdmissionResult Admit(WebSocketSession session, string? deviceFingerprint)
    {
        var fingerprint = NormalizeFingerprint(deviceFingerprint);

        if (options.SingleSessionKickEnabled)
        {
            // 配额先于顶替：配额不足直接拒绝，旧会话不受任何影响。
            if (options.DeviceQuotaEnabled)
            {
                if (fingerprint is null)
                    return AntiMultiOpenAdmissionResult.Reject(
                        AntiMultiOpenConstants.DeviceFingerprintRequiredErrorCode,
                        "客户端设备校验信息缺失。",
                        AntiMultiOpenConstants.DeviceFingerprintRequiredCloseCode);
                if (!devices.TryEnter(fingerprint, session.PlayerId, options.DeviceQuotaMaxAccounts))
                    return AntiMultiOpenAdmissionResult.Reject(
                        AntiMultiOpenConstants.DeviceQuotaExceededErrorCode,
                        "当前设备同时在线的账号数量已达上限。",
                        AntiMultiOpenConstants.DeviceQuotaExceededCloseCode);
                session.DeviceFingerprint = fingerprint;
            }
            sessions.AddOrKick(session, out var kicked);
            return new AntiMultiOpenAdmissionResult(true, kicked, null, null, null);
        }

        if (!sessions.TryAdd(session))
            return AntiMultiOpenAdmissionResult.Reject(
                AntiMultiOpenConstants.DuplicateSessionErrorCode,
                "该账号已有在线连接。",
                AntiMultiOpenConstants.DuplicateSessionCloseCode);
        if (options.DeviceQuotaEnabled)
        {
            var fingerprintRejected = fingerprint is null
                || !devices.TryEnter(fingerprint, session.PlayerId, options.DeviceQuotaMaxAccounts);
            if (fingerprintRejected)
            {
                // 回滚会话登记，保证拒绝路径不残留任何注册表状态。
                sessions.Remove(session.PlayerId, session);
                return fingerprint is null
                    ? AntiMultiOpenAdmissionResult.Reject(
                        AntiMultiOpenConstants.DeviceFingerprintRequiredErrorCode,
                        "客户端设备校验信息缺失。",
                        AntiMultiOpenConstants.DeviceFingerprintRequiredCloseCode)
                    : AntiMultiOpenAdmissionResult.Reject(
                        AntiMultiOpenConstants.DeviceQuotaExceededErrorCode,
                        "当前设备同时在线的账号数量已达上限。",
                        AntiMultiOpenConstants.DeviceQuotaExceededCloseCode);
            }
            session.DeviceFingerprint = fingerprint;
        }
        return new AntiMultiOpenAdmissionResult(true, null, null, null, null);
    }

    /// <summary>
    /// 会话生命周期结束时释放准入占用的登记位。
    /// </summary>
    /// <returns>该会话仍是注册表中的当前会话时返回 true，调用方据此决定是否卸载玩家 Actor</returns>
    public bool Release(WebSocketSession session)
    {
        if (session.DeviceFingerprint is not null)
        {
            devices.Leave(session.DeviceFingerprint, session.PlayerId);
            session.DeviceFingerprint = null;
        }
        return sessions.Remove(session.PlayerId, session);
    }

    private static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var trimmed = fingerprint.Trim();
        return trimmed.Length <= AntiMultiOpenConstants.MaxDeviceFingerprintLength ? trimmed : null;
    }
}
