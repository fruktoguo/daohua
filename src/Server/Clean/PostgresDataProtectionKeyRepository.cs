using System.Xml.Linq;
using System.Xml;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace Server.Clean;

/// <summary>
/// 将 ASP.NET Core Data Protection key ring 存入 PostgreSQL。
///
/// 该仓储只保存低频 key rotation 数据，不参与玩家/资产热路径；数据库账号必须限制
/// identity.data_protection_keys 的读写权限。若数据库不可用，直接抛错，不回退到本地文件，
/// 避免多实例生成互不兼容的 Cookie key ring。
/// </summary>
public sealed class PostgresDataProtectionKeyRepository(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDataProtectionKeyRepository> logger) : IXmlRepository
{
    private const int MaxXmlBytes = 1_048_576;

    /// <summary>
    /// 获取AllElements。
    /// </summary>
    /// <returns>返回 IReadOnlyCollection&lt;XElement&gt; 类型的结果。</returns>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            SELECT xml_payload
            FROM identity.data_protection_keys
            ORDER BY created_at, key_id
            """, connection);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            var xml = reader.GetString(0);
            if (Encoding.UTF8.GetByteCount(xml) > MaxXmlBytes)
                throw new InvalidDataException("Data Protection key XML 超过大小上限。");
            try
            {
                elements.Add(XElement.Parse(xml, LoadOptions.PreserveWhitespace));
            }
            catch (Exception ex) when (ex is XmlException or ArgumentException)
            {
                throw new InvalidDataException("Data Protection key XML 无效。", ex);
            }
        }
        return elements;
    }

    /// <summary>
    /// 执行StoreElement。
    /// </summary>
    /// <param name="element">参数 element。</param>
    /// <param name="friendlyName">参数 friendlyName。</param>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        var xml = element.ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(xml) > MaxXmlBytes)
            throw new InvalidDataException("Data Protection key XML 超过大小上限。");
        var keyId = ReadKeyId(element);
        var createdAt = ReadDate(element, "creationDate") ?? DateTimeOffset.UtcNow;
        var expiresAt = ReadDate(element, "expirationDate");

        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            INSERT INTO identity.data_protection_keys
                (key_id, friendly_name, xml_payload, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (key_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = keyId });
        command.Parameters.Add(new NpgsqlParameter { Value = NormalizeFriendlyName(friendlyName) });
        command.Parameters.Add(new NpgsqlParameter { Value = xml });
        command.Parameters.Add(new NpgsqlParameter { Value = createdAt });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)expiresAt ?? DBNull.Value });
        var affected = command.ExecuteNonQuery();
        if (affected == 0)
        {
            logger.LogDebug("Data Protection key 已存在，跳过重复写入 keyId={KeyId}", keyId);
        }
        else
        {
            logger.LogInformation("Data Protection key 已写入 PostgreSQL keyId={KeyId} expiresAt={ExpiresAt}", keyId, expiresAt);
        }
    }

    /// <summary>
    /// 读取KeyId。
    /// </summary>
    /// <param name="element">参数 element。</param>
    /// <returns>返回 Guid 类型的结果。</returns>
    public static Guid ReadKeyId(XElement element)
    {
        var value = element.Attribute("id")?.Value;
        if (!Guid.TryParse(value, out var keyId) || keyId == Guid.Empty)
            throw new InvalidDataException("Data Protection key 缺少有效 id。");
        return keyId;
    }

    /// <summary>
    /// 读取Date。
    /// </summary>
    /// <param name="element">参数 element。</param>
    /// <param name="name">参数 name。</param>
    /// <returns>返回 DateTimeOffset? 类型的结果。</returns>
    public static DateTimeOffset? ReadDate(XElement element, string name)
    {
        var value = element.Element(name)?.Value;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var date)
            ? date.ToUniversalTime()
            : null;
    }

    private static string? NormalizeFriendlyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(256, clean.Length)];
    }
}

/// <summary>
/// 提供 DataProtectionServiceCollectionExtensions 相关功能的静态容器。
/// </summary>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>配置 PostgreSQL key ring；禁止 ASP.NET 回退到本地文件仓储。</summary>
    public static IServiceCollection AddDaohuaPostgresDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("daohua");
        services.AddSingleton<PostgresDataProtectionKeyRepository>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
            new ConfigureNamedOptions<KeyManagementOptions>(Options.DefaultName,
                options => options.XmlRepository = provider.GetRequiredService<PostgresDataProtectionKeyRepository>()));
        return services;
    }
}
