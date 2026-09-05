namespace Ecr.Api.Health;

/// <summary>
/// Звіт перевірок здоров'я — <b>справжній тип</b>, а не анонімний об'єкт.
/// </summary>
/// <remarks>
/// ⛔ Тут двічі жила <c>A7-04</c>/<c>A7-36</c>: писар серіалізував анонімний
/// об'єкт із полем <c>checks</c>, клієнт оголошував власний інтерфейс із полем
/// <c>entries</c>, і жодне з двох оголошень не знало про інше. Дашборд
/// відкривався порожнім і виглядав як здорова система без перевірок.
///
/// ⚠ Анонімний об'єкт неможливо описати в OpenAPI: у схемі його немає, отже
/// згенерувати клієнтський тип нема з чого, отже клієнт пише свій. Розрив
/// починався тут — тому тип названий.
///
/// ⚠ `/health/*` — middleware, а не контролер, тому шляхи додаються в документ
/// окремим трансформером (<see cref="Startup.HealthDocumentTransformer"/>).
/// </remarks>
/// <param name="Status">Зведений стан: <c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c>.</param>
/// <param name="TotalDurationMs">Скільки тривали всі перевірки, у мілісекундах.</param>
/// <param name="Checks">Перевірки. Порядок задає контейнер — шукати за іменем.</param>
public sealed record HealthReportDto(
    string Status,
    double TotalDurationMs,
    IReadOnlyList<HealthCheckDto> Checks);

/// <summary>Одна перевірка у звіті.</summary>
/// <param name="Name">Ім'я перевірки: <c>db</c>, <c>jobs</c>, <c>sources</c>.</param>
/// <param name="Status">Стан цієї перевірки.</param>
/// <param name="Description">Що саме вона з'ясувала; <c>null</c>, якщо нічого не сказала.</param>
/// <param name="DurationMs">Тривалість цієї перевірки.</param>
/// <param name="Data">
/// Подробиці: редакція SQL Server, RCSI, запас партицій, кількість джерел із
/// прогалинами (АРХ-7 п. 5).
///
/// ⚠ Текст винятку сюди НЕ потрапляє (ФВ-6.11): у ньому бувають імена об'єктів
/// БД і фрагменти запитів. Клієнту — сам факт, у журнал — подробиці.
/// </param>
public sealed record HealthCheckDto(
    string Name,
    string Status,
    string? Description,
    double DurationMs,
    IReadOnlyDictionary<string, object> Data);
