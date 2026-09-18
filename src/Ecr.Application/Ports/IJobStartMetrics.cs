// src/Ecr.Application/Ports/IJobStartMetrics.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Затримка «поставили задачу в чергу → задача ПОЧАЛА виконуватись»
/// (<c>ФВ-12.2</c>, <c>tz/08</c> §8.3).
/// </summary>
/// <remarks>
/// ⛔ Порт, а не прямий виклик <c>Ecr.Api.Observability.EcrMetrics</c>: та
/// живе в <c>Ecr.Api</c>, а місток задач — у <c>Ecr.Infrastructure</c>, яка на
/// <c>Ecr.Api</c> НЕ посилається. Той самий прийом, що
/// <see cref="IConsistencyMetrics"/>.
///
/// ⛔ ЧОМУ ЦЕ ВЗАГАЛІ ПОТРІБНО. `ФВ-12.2` задає межу 10 с (авто) і 5 с
/// (ручний запуск), але до 2026-09-18 цієї величини не міряло НІЩО: у
/// `tz/08` числа не було, а серед критеріїв гейта BR-07 його теж не було.
///
/// ⚠ Момент постановки в базі Є: `QuartzJobScheduler` кличе
/// <c>IJobProgressStore.QueueAsync</c>, і рядок `itg.JobProgress` з'являється
/// зі станом `Queued` ще до старту — навмисно, щоб клієнт, який опитує стан
/// одразу після `202`, не отримав `404`. Але цей момент **не переживає
/// старту**: <c>JobProgress.Begin</c> перезаписує `UpdatedAt`, а `StartedAt`
/// ставить уже на момент запуску. Тобто з бази затримку можна порахувати
/// рівно в мить переходу — і ніколи після неї.
///
/// ⚠ Мітка їде в `JobDataMap` самої задачі — це СУДЖЕННЯ, а не безвихідь.
/// Читання з бази теж було можливе (в адаптері, перед `Begin`), але коштувало
/// б походу до СУБД на шляху, який сам і міряється, і зв'язало б метрику зі
/// сховищем прогресу, яке для адаптера необов'язкове.
///
/// ⛔ Тут стояло твердження, що стан `Queued` «не пише ніхто». Це була моя
/// помилка: `git grep` дивився у `JobProgressStore.cs` і
/// `QuartzJobScheduler.cs`, а пише його `JobProgress.Queue()` у
/// `Entities/Integration/IntegrationLogs.cs`. Завузький пошук дає таку саму
/// впевнену неправду, як і довірливе читання.
/// </remarks>
public interface IJobStartMetrics
{
    /// <summary>Фіксує затримку старту однієї задачі.</summary>
    /// <param name="milliseconds">Скільки минуло від постановки до першого рядка тіла.</param>
    /// <param name="jobCode">Код задачі — щоб відрізняти перерахунок від архівації.</param>
    public void RecordStartLatency(double milliseconds, string jobCode);
}
