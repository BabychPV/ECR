// src/Ecr.Api/Security/TableDiscussionPostRateLimitPolicy.cs

namespace Ecr.Api.Security;

/// <summary>
/// Межа <c>POST /api/v1/documents/{id}/tables/{tableCode}/messages</c> (S-6, рішення PO 11.11): запис у чат таблиці
/// на КОРИСТУВАЧА — типово 20 повідомлень за хвилину; понад межу — 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Кожне повідомлення — транзакція з блокуванням зведення чату, до 10 згадок (кожна — рішення доступу) і рядок
/// <c>aud.SecurityEvent</c> (<c>TableDiscussionCommands.PostAsync</c>). 20/хв — одне повідомлення на 3 с тривало:
/// людина в чаті межі не бачить, скрипт не заповнює журнал безпеки й чужі «непрочитані». Повтор із тим самим
/// <c>clientMessageId</c> теж рахується: обмежувач стоїть до обробника (<c>Program.cs</c>, <c>UseRateLimiter</c>).
/// </remarks>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class TableDiscussionPostRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback, DefaultPermit)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "discussion-post";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:DiscussionPostPermitPerMinute";

    /// <summary>Межа за хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultPermit = 20;

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyChatMessages";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many chat messages in a short time. Wait a moment and try again.";
}
