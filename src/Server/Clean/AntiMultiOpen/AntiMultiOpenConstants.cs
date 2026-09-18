namespace Server.Clean.AntiMultiOpen;

/// <summary>
/// 防多开模块的协议常量：自定义 WebSocket 关闭码（4000-4999 为私有用途段）与错误码。
/// 前端按关闭码区分「被顶替」「配额拒绝」「重复连接」并禁止自动重连，任何取值变更必须与
/// frontend/src/antiMulti/codes.ts 同步。
/// </summary>
public static class AntiMultiOpenConstants
{
    /// <summary>旧连接被同账号的新会话顶替下线</summary>
    public const int SessionKickedCloseCode = 4005;
    /// <summary>设备指纹并发账号配额已满，新登录被拒绝</summary>
    public const int DeviceQuotaExceededCloseCode = 4006;
    /// <summary>设备配额开启但客户端未上报合法指纹</summary>
    public const int DeviceFingerprintRequiredCloseCode = 4007;
    /// <summary>互踢关闭时同账号第二个连接被拒绝</summary>
    public const int DuplicateSessionCloseCode = 4008;

    /// <summary>协议错误码：会话被顶替</summary>
    public const string SessionKickedErrorCode = "SESSION_KICKED";
    /// <summary>协议错误码：设备配额超限</summary>
    public const string DeviceQuotaExceededErrorCode = "DEVICE_QUOTA_EXCEEDED";
    /// <summary>协议错误码：缺少设备指纹</summary>
    public const string DeviceFingerprintRequiredErrorCode = "DEVICE_FINGERPRINT_REQUIRED";
    /// <summary>协议错误码：重复会话被拒绝</summary>
    public const string DuplicateSessionErrorCode = "DUPLICATE_SESSION";

    /// <summary>设备指纹载荷最大允许长度（SHA-256 hex 为 64 字符，预留冗余）</summary>
    public const int MaxDeviceFingerprintLength = 128;
}
