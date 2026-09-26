using Domain.Common;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>
/// 把两套 YuoCore 基座的观测出口接到 DaoHua 现有 Meter。
/// </summary>
internal static class EcsCoreTraceBridge
{
    private static readonly object Sync = new();
    private static int _leaseCount;

    public static void Install()
    {
        lock (Sync)
        {
            if (_leaseCount == 0 && RuntimeInstrumentation.Enabled)
                YuoCoreTrace.Configure(RuntimeInstrumentation.BeginCurrent,
                    RuntimeInstrumentation.EndCurrent, RuntimeInstrumentation.Count);
            _leaseCount++;
        }
    }

    public static void Uninstall()
    {
        lock (Sync)
        {
            if (_leaseCount == 0) return;
            if (--_leaseCount == 0)
                YuoCoreTrace.Configure(null, null, null);
        }
    }
}
