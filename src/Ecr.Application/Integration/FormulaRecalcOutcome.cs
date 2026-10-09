// src/Ecr.Application/Integration/FormulaRecalcOutcome.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Скільки комірок записав перерахунок формул (<c>FormulaRecalculationJob</c>), прочитане із
/// СИРОГО конверта повідомлення задачі — <see cref="JobStatus.WrittenCount"/> (AN-108 / P2-02).
/// </summary>
/// <remarks>
/// ⛔ Окреме поле, а не новий <c>EffectiveState</c>: похідний стан читають бейджі, фільтр і підсумок
/// «Мої задачі», <c>outcomeOf</c> усіх екранів, що стежать за задачами, і перевірка здоров'я — нове
/// значення там стало б невідомим станом. Число нікого з них не зачіпає, а <c>null</c> («невідомо») клієнт
/// трактує як раніше: перечитати зрізи. Повідомлення клієнтові приходить уже перекладеним, тож
/// <c>written</c> з нього не дістати — звідси поле.
/// </remarks>
public static class FormulaRecalcOutcome
{
    /// <summary>Ключ підсумку «перераховано {written} комірок».</summary>
    public const string DoneKey = "jobs.formulaRecalcDone";

    /// <summary>Ключ підсумку «змінених комірок немає».</summary>
    public const string NoneKey = "jobs.formulaRecalcNone";

    /// <summary>Параметр <see cref="DoneKey"/>: скільки комірок записано.</summary>
    public const string WrittenParam = "written";

    /// <summary>
    /// Записані комірки для <c>Succeeded</c> перерахунку формул: <see cref="NoneKey"/> — <c>0</c>,
    /// <see cref="DoneKey"/> — його <c>written</c>; інакше (інша задача, не завершена, старий
    /// конверт без числа) — <c>null</c>.
    /// </summary>
    /// <param name="state">Збережений стан задачі.</param>
    /// <param name="rawMessage">Сире повідомлення (до резолвера каталогу).</param>
    public static int? WrittenCountOf(string state, string? rawMessage)
    {
        if (!string.Equals(state, "Succeeded", StringComparison.Ordinal)
            || !JobProgressMessageCodec.TryDecode(rawMessage, out var envelope))
        {
            return null;
        }

        if (string.Equals(envelope.Key, NoneKey, StringComparison.Ordinal))
        {
            return 0;
        }

        return string.Equals(envelope.Key, DoneKey, StringComparison.Ordinal)
               && envelope.Params is { } p
               && p.TryGetValue(WrittenParam, out var s)
               && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var written)
               && written >= 0
            ? written
            : null;
    }
}
