// src/Ecr.Api/Security/DocumentExportRateLimitPolicy.cs

namespace Ecr.Api.Security;

/// <summary>
/// PT-6 (пентест 2026-11): межа постановки важких задач вивантаження в чергу на КОРИСТУВАЧА — типово 6 за хвилину;
/// понад межу — 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Один кошик на три дії: <c>POST /api/v1/documents/{id}/export</c>,
/// <c>POST /api/v1/documents/{id}/calculation-results/year/export</c> і <c>POST /api/v1/reports/{code}/build</c>.
/// Кожна лише ставить задачу й відповідає <c>202</c>; без межі один користувач за 20 с ставив 300 експортів
/// (черга Excel — по дві задачі, книга годину лежить у кеші) або 60 однакових зрізів, які самі не зникають.
/// </remarks>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class DocumentExportRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback, DefaultExportPermit)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "document-export";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:ExportPermitPerMinute";

    /// <summary>Межа за хвилину на користувача, якщо конфігурація мовчить.</summary>
    /// <remarks>
    /// ⚠ Ім'я унікальне навмисно: сторож <c>ConfigurationKeysTests</c> шукає константи за КОРОТКИМ іменем (перша
    /// виграє), і <c>DefaultPermit</c> тут перекрив би однойменну константу <c>SearchRateLimitPolicy</c> (30).
    /// </remarks>
    public const int DefaultExportPermit = 6;

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyExports";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many export requests in a short time. Wait a moment and try again.";
}
