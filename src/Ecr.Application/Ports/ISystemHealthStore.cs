// src/Ecr.Application/Ports/ISystemHealthStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Агрегати для екрана «Здоров'я системи» (<c>GET /api/v1/health/facts</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт віддає ЛИШЕ числа й мітки часу. Жодних шляхів, імен серверів чи баз,
/// рядків підключення, версій СУБД/ОС, імен користувачів, назв джерел чи URL:
/// відповідь бачить адміністратор, але вона потрапляє в скриншоти й тікети.
/// Усе, чого СУБД не віддала (немає права на <c>msdb</c> чи
/// <c>sys.dm_os_volume_stats</c>), — <c>null</c>, а не виняток і не нуль.
/// </remarks>
public interface ISystemHealthStore
{
    /// <summary>Читає агрегати кількома запитами (без N+1).</summary>
    /// <param name="utcNow">Момент відліку вікон («за добу», «за тиждень»), UTC.</param>
    /// <param name="ct">Скасування.</param>
    public Task<SystemHealthSnapshot> ReadAsync(DateTime utcNow, CancellationToken ct);
}

/// <summary>Знімок агрегатів здоров'я.</summary>
/// <param name="Jobs">Лічильники черги фонових задач.</param>
/// <param name="Sources">Лічильники джерел збору.</param>
/// <param name="FreeSpaceDataDiskGb">
/// Вільне місце (ГБ, ціле, округлене вниз) на найповнішому диску файлів даних БД
/// застосунку; <c>null</c> — СУБД не віддала (немає права).
/// </param>
/// <param name="LastBackupAt">
/// Завершення останньої повної чи різницевої копії БД застосунку (UTC); <c>null</c> —
/// копій немає або немає права читати <c>msdb</c>.
/// </param>
/// <param name="LastErrorAt">Момент останнього провалу задачі чи збору (UTC); <c>null</c> — не було.</param>
/// <param name="Hse400DocumentInCurrentYear">
/// B105: чи є опублікована версія шаблону HSE400 і документ HSE400 (не золотого проєкту) з періодом поточного
/// року. Лише так/ні: із цим знанням і вимкненим прапором Land мовчки читає склад газу з довідника.
/// </param>
/// <param name="Hse400Source">
/// B80 (N1): зв'язка HSE400 → склад газу Land; <c>null</c> — зв'язки немає (стан-ворота не визначено).
/// </param>
public sealed record SystemHealthSnapshot(
    HealthJobCounts Jobs,
    HealthSourceCounts Sources,
    long? FreeSpaceDataDiskGb,
    DateTime? LastBackupAt,
    DateTime? LastErrorAt,
    bool Hse400DocumentInCurrentYear = false,
    HealthHse400SourceFacts? Hse400Source = null);

/// <summary>B80 (N1): факти зв'язки HSE400 → Land за поточний рік.</summary>
/// <param name="ApprovedOnly">Стан-ворота — «лише затверджене».</param>
/// <param name="ReadFromDocument">Біт зв'язки «читати з документа».</param>
/// <param name="ApprovedMonths">
/// Затверджених місяців обраного документа поточного року (0–12); <c>null</c> — документа поточного року немає.
/// </param>
/// <param name="UnapprovedMonthWithData">Є місяць із введеними даними джерела, не затверджений.</param>
public sealed record HealthHse400SourceFacts(
    bool ApprovedOnly, bool ReadFromDocument, int? ApprovedMonths, bool UnapprovedMonthWithData);

/// <summary>Лічильники черги фонових задач.</summary>
/// <param name="Running">Виконуються зараз.</param>
/// <param name="Queued">Стоять у черзі.</param>
/// <param name="Failed24h">Завершились провалом за останню добу.</param>
public sealed record HealthJobCounts(int Running, int Queued, int Failed24h);

/// <summary>Лічильники джерел збору (без назв і адрес).</summary>
/// <param name="Active">Налаштованих активних джерел.</param>
/// <param name="Failed">Активних джерел, у яких останній запуск збору завершився провалом.</param>
/// <param name="Gaps">
/// Подій покриття за тиждень, де дані за інтервал НЕ перенесено (пропуск, відмова
/// джерела, конфлікт запису).
/// </param>
public sealed record HealthSourceCounts(int Active, int Failed, int Gaps);
