// src/Ecr.Api/Security/ImportPreviewRateLimitPolicy.cs

using System.Threading.RateLimiting;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Межа ОДНОЧАСНИХ переглядів імпорту книги <c>.xlsx</c>
/// (<c>POST /api/v1/documents/{id}/import/preview</c>) на весь процес Api.
/// </summary>
/// <remarks>
/// ⛔ P1-04 = S1-02 (AUDIT-2026-10-09b). Перегляд розбирає книгу ClosedXML синхронно, у
/// запиті: <c>new XLWorkbook</c> будує повну об'єктну модель ДО перевірки карти
/// <c>_ecr</c>. Ворота <c>XlsxSafetyGate</c> пропускають до 256 МіБ розпакованого XML
/// (коментар воріт: ~6 байтів пам'яті на байт XML, тобто ~1,5 ГБ на запит на стелі).
/// Кілька одночасних переглядів великих книг давали <c>OutOfMemoryException</c> або
/// довгі паузи gen2 GC для всіх користувачів, а падіння процесу губило in-memory чергу
/// Quartz.
///
/// ⚠ Стелю воріт НЕ знижено (вердикт перевіряльника S1-02): реальна книга директиви
/// (91 таблиця) — 113 МіБ розпакованого XML, 48 МіБ зламали б законний імпорт.
/// Обмежується одночасність: <see cref="DefaultConcurrency"/> розборів одночасно, ще
/// <see cref="DefaultQueueLimit"/> чекають у черзі (оператори наприкінці періоду
/// імпортують хвилею, і короткому очікуванню краще за відмову), решта — 429
/// <c>ECR-REQ-0429</c> з ключем каталогу <see cref="DetailKey"/>.
///
/// ⚠ Розділ один на процес, а не на користувача: межа захищає пам'ять ПРОЦЕСУ, і сто
/// користувачів по одному перегляду з'їдають її так само, як один зі ста.
///
/// ⚠ Дозвіл тримається весь час обробки запиту (<c>RateLimitingMiddleware</c> звільняє
/// оренду після <c>next</c>), тобто й під час читання тіла — воно теж пам'ять.
/// </remarks>
public sealed class ImportPreviewRateLimitPolicy(IConfiguration configuration) : IRateLimiterPolicy<string>
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "import-preview";

    /// <summary>Скільки переглядів розбирається одночасно, якщо конфігурація мовчить.</summary>
    public const int DefaultConcurrency = 2;

    /// <summary>Скільки переглядів чекає в черзі, якщо конфігурація мовчить.</summary>
    public const int DefaultQueueLimit = 4;

    /// <summary>Ключ конфігурації: одночасних переглядів.</summary>
    public const string PermitKey = "Security:RateLimit:ImportPreviewConcurrency";

    /// <summary>Ключ конфігурації: довжина черги.</summary>
    public const string QueueLimitKey = "Security:RateLimit:ImportPreviewQueueLimit";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.importBusy";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "The server is busy reading other imported workbooks. Try again in a minute.";

    /// <summary>Єдиний розділ політики.</summary>
    public const string Partition = "import-preview";

    private readonly int _permit = Math.Max(1, configuration.GetValue(PermitKey, DefaultConcurrency));

    private readonly int _queueLimit = Math.Max(0, configuration.GetValue(QueueLimitKey, DefaultQueueLimit));

    /// <inheritdoc />
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        static (rejection, ct) => new ValueTask(LoginRateLimiting.RejectAsync(
            rejection, ErrorCodes.TooManyRequests, DetailKey, DetailFallback, ct));

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        => RateLimitPartition.GetConcurrencyLimiter(Partition, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = _permit,
            QueueLimit = _queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
}
