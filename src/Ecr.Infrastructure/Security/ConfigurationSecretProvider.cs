using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Секрети зі змінних середовища й провайдерів конфігурації.
/// </summary>
/// <remarks>
/// Ім'я <c>PiAf.Primary</c> читається як <c>Secrets:PiAf.Primary</c>, тобто
/// зі змінної середовища <c>ECR_Secrets__PiAf.Primary</c> — тим самим
/// префіксом, що й решта налаштувань (<c>ECR_ConnectionStrings__Ecr</c>,
/// <c>ECR_Bootstrap__Password</c>).
///
/// ⛔ Значення секрету не логується — ні на успіху, ні на невдачі. Лог із
/// паролем гірший за відсутній лог: він створює хибне відчуття, що система
/// закрита, і при цьому лежить у файлі, який читають усі, хто дивиться
/// причину збою.
/// </remarks>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    /// <summary>Секція конфігурації, у якій живуть секрети джерел.</summary>
    public const string Section = "Secrets";

    /// <inheritdoc />
    public string? Find(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var value = configuration[$"{Section}:{secretName}"];

        // Порожній рядок — це «не налаштовано», а не «порожній пароль».
        // Змінна середовища, задана порожньою, інакше пройшла б перевірку
        // наявності й впала аж на автентифікації до джерела.
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
