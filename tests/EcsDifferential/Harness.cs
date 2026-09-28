using System.Text;
using YuoCore.Ecs;

namespace DaoHua.EcsDifferential;

public static class WorldFactory
{
    /// <summary>创建一个注入确定性时钟与 id 生成器的世界；系统/组件由 OnInit 反射扫描装载。</summary>
    public static YuoWorld Create()
    {
        var world = new YuoWorld
        {
            Time = new FixedClock(),
            IdGenerate = new SequentialIdGenerate()
        };
        world.OnInit();
        return world;
    }
}

/// <summary>
/// 世界归一化快照：实体按 id 排序、组件按声明类型名排序、系统成员按实体 id 排序。
/// 容器天然无序的字段一律排序；快照同时保留 BaseComponents 键位与 Children 集合，
/// 把容器残留语义（RemoveItem 后空 key 仍 ContainsKey）也纳入比对。
/// </summary>
public static class WorldDump
{
    public static string Build(YuoWorld world)
    {
        var sb = new StringBuilder(4096);
        var ids = world.Entities.Keys.ToList();
        ids.Sort();
        foreach (var id in ids)
        {
            var e = world.Entities[id];
            sb.Append("E").Append(id)
                .Append(e.IsDisposed ? "d" : "")
                .Append(e.IsDestroy ? "x" : "")
                .Append('#').Append(Esc(e.EntityName ?? ""))
                .Append('<');
            // 按 Components 字典键（声明类型）排序展示；value 的 Type 可能因
            // SetComponentType/ReplaceComponent 被改写，与键不一致时也要看得见。
            var comps = e.Components.ToList();
            comps.Sort(static (a, b) => string.CompareOrdinal(a.Key.FullName, b.Key.FullName));
            foreach (var kv in comps)
            {
                var c = kv.Value;
                sb.Append(ComponentPool.Short(kv.Key))
                    .Append(c.Type == kv.Key ? '=' : '~' + ComponentPool.Short(c.Type) + '=')
                    .Append(ComponentPool.Snap(c))
                    .Append(c.IsDestroy ? 'x' : '.')
                    .Append(c.IsDisposed ? 'd' : '.')
                    .Append(c.BaseComponentType == null ? "" : "^" + ComponentPool.Short(c.BaseComponentType))
                    .Append(';');
            }

            var baseKeys = new List<string>();
            foreach (var k in e.BaseComponents.Keys) baseKeys.Add(ComponentPool.Short(k));
            baseKeys.Sort(StringComparer.Ordinal);
            sb.Append("B").Append(string.Join(',', baseKeys));

            var childIds = e.Children.Select(c => c.ID).ToList();
            childIds.Sort();
            sb.Append("C").Append(string.Join(',', childIds));
            sb.Append('>');
        }

        var systems = world.GetAllSystem.ToList();
        systems.Sort(static (a, b) => string.CompareOrdinal(a.Type.FullName, b.Type.FullName));
        sb.Append("|S|");
        foreach (var s in systems)
        {
            sb.Append(s.SystemName).Append(s.Enabled ? '+' : '-');
            var member = s.Entities.Select(en => en.ID).ToList();
            member.Sort();
            sb.Append('[').Append(string.Join(',', member)).Append(']');
        }

        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace('|', '/').Replace('>', '_').Replace('<', '_');
}

/// <summary>xorshift64* 确定性伪随机数，与运行时 Random 实现无关。</summary>
public struct XorShift64
{
    private ulong _s;

    public XorShift64(ulong seed)
    {
        _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong Next()
    {
        var x = _s;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _s = x;
        return x * 2685821657736338717UL;
    }

    public int Range(int maxExclusive) => (int)(Next() % (ulong)maxExclusive);

    public bool Chance(int percent) => Range(100) < percent;
}
