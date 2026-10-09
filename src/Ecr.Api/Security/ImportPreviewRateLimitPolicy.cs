// src/Ecr.Api/Security/ImportPreviewRateLimitPolicy.cs

using System.Threading.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Налаштування межі ОДНОЧАСНИХ розборів у перегляді імпорту книги <c>.xlsx</c>
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
/// ⛔ AN-123 (R1-04 = R2-02, AUDIT-2026-10-09c). Доти це була іменована політика
/// <c>[EnableRateLimiting]</c>: <c>RateLimitingMiddleware</c> бере оренду ДО прив'язки
/// моделі, а <c>IFormFile</c> читається саме під час прив'язки — тобто місце розбору
/// займало й мережеве вивантаження книги (до 30 МБ; на VPN 2 Мбіт/с — ~100 с; повільне
/// тіло на мінімальній швидкості Kestrel — години). Припущення «тіло — теж пам'ять»
/// хибне: <c>FormReader</c> кладе файл понад 64 КБ у тимчасовий файл на диску.
/// Тепер межу тримає <see cref="ImportPreviewGate"/>, і дозвіл береться в ДІЇ, коли тіло
/// вже прочитане: межа стоїть на розборі, а не на мережі.
/// </remarks>
public sealed class ImportPreviewRateLimitPolicy(IConfiguration configuration)
{
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

    private readonly int _permit = Math.Max(1, configuration.GetValue(PermitKey, DefaultConcurrency));

    private readonly int _queueLimit = Math.Max(0, configuration.GetValue(QueueLimitKey, DefaultQueueLimit));

    /// <summary>Налаштування обмежувача одночасних розборів (див. <see cref="ImportPreviewGate"/>).</summary>
    /// <returns>Межа, черга і порядок «хто раніше прийшов».</returns>
    public ConcurrencyLimiterOptions LimiterOptions() => new()
    {
        PermitLimit = _permit,
        QueueLimit = _queueLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };
}
