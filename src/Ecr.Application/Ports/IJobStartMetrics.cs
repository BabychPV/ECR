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
/// `tz/08` числа не було, у гейті BR-07 критерію не було, а в базі немає
/// навіть мітки часу постановки — `itg.JobProgress` створюється вже зі
/// станом `Running`, тобто В МОМЕНТ СТАРТУ. Стан `Queued` фігурує у фільтрах
/// `JobProgressStore`, але його ніхто не пише.
///
/// ⚠ Саме тому мітка їде в `JobDataMap` самої задачі, а не в базу: писати
/// рядок при постановці означало б зайвий похід до СУБД на кожен
/// `EnqueueAsync` — на шляху, який і так стережуть за латентністю.
/// </remarks>
public interface IJobStartMetrics
{
    /// <summary>Фіксує затримку старту однієї задачі.</summary>
    /// <param name="milliseconds">Скільки минуло від постановки до першого рядка тіла.</param>
    /// <param name="jobCode">Код задачі — щоб відрізняти перерахунок від архівації.</param>
    public void RecordStartLatency(double milliseconds, string jobCode);
}
