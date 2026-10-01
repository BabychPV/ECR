// src/Ecr.Application/Sources/IntegrationAuditReason.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Application.Sources;

/// <summary>
/// Кодування причини зміни налаштувань збору конвертом <c>{"k":…,"p":{…}}</c>
/// (кодек <see cref="JobProgressMessageCodec"/>); клієнт розгортає ключ мовою інтерфейсу.
/// </summary>
public static class IntegrationAuditReason
{
    /// <summary>Конверт із ключем каталогу й параметрами (числа — інваріантною культурою).</summary>
    /// <param name="key">Ключ каталогу <c>integrationAudit.*</c>.</param>
    /// <param name="parameters">Підстановки <c>{name}</c>; порожньо — без <c>p</c>.</param>
    /// <returns>Компактний JSON конверта.</returns>
    public static string Encode(string key, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            key,
            parameters.Length == 0
                ? null
                : parameters.ToDictionary(
                    p => p.Name,
                    p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparer.Ordinal)));
    }
}
