// src/Ecr.Api/Security/ImportPreviewGate.cs

using System.Threading.RateLimiting;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Api.Security;

/// <summary>
/// Межа одночасних РОЗБОРІВ книги в перегляді імпорту — один обмежувач на процес Api.
/// </summary>
/// <remarks>
/// ⛔ AN-123 (R1-04 = R2-02). Дозвіл береться в дії контролера ПІСЛЯ прив'язки
/// <c>IFormFile</c>, тобто коли тіло вже прийняте (файл понад 64 КБ лежить у тимчасовому
/// файлі на диску), а не в <c>RateLimitingMiddleware</c> до прив'язки. Інакше повільне
/// вивантаження (VPN, навмисно повільне тіло) займало місце розбору, якого не розбирало,
/// і решта операторів діставала 429, хоча сервер нічого не розбирав.
///
/// ⚠ Налаштування — ті самі ключі й та сама форма (межа + черга + «хто раніше»), що й у
/// попередньої політики (<see cref="ImportPreviewRateLimitPolicy"/>); відмова — та сама
/// <c>429 ECR-REQ-0429</c> з ключем <see cref="ImportPreviewRateLimitPolicy.DetailKey"/>.
/// </remarks>
public sealed class ImportPreviewGate : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;

    /// <summary>Будує обмежувач з конфігурації.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public ImportPreviewGate(IConfiguration configuration)
        => _limiter = new ConcurrencyLimiter(new ImportPreviewRateLimitPolicy(configuration).LimiterOptions());

    /// <summary>
    /// Чекає місця розбору (у черзі, якщо вона не повна); понад чергу — відмова 429.
    /// </summary>
    /// <param name="ct">Скасування — звільняє місце в черзі.</param>
    /// <returns>Оренда; звільнити (<c>Dispose</c>) після розбору.</returns>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0429</c>, ключ <c>importBusy</c>.</exception>
    public async Task<RateLimitLease> EnterAsync(CancellationToken ct)
    {
        var lease = await _limiter.AcquireAsync(1, ct).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();

        throw new BusinessRuleException(
            ErrorCodes.TooManyRequests,
            ImportPreviewRateLimitPolicy.DetailFallback,
            new Dictionary<string, object?> { ["messageKey"] = ImportPreviewRateLimitPolicy.DetailKey });
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
