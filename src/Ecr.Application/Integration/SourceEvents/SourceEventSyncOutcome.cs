// src/Ecr.Application/Integration/SourceEvents/SourceEventSyncOutcome.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>
/// D-464: масове видалення зниклих подій, заблоковане лімітом «повної звірки» за прогін синку
/// (<c>JobStatus.eventRemovalBlocked</c>): скільки подій чекають підтвердження і яка межа.
/// </summary>
/// <param name="Maps">Скільки мапінгів прогону заблоковано.</param>
/// <param name="Candidates">Скільки рядків було б видалено (сума за заблокованими мапінгами).</param>
/// <param name="Limit">Дозволено без підтвердження (сума меж заблокованих мапінгів).</param>
/// <param name="Linked">Зв'язків-з-рядками у вікні (сума за заблокованими мапінгами) — база відсотка.</param>
public sealed record SourceEventRemovalBlock(int Maps, int Candidates, int Limit, int Linked);

/// <summary>
/// Підсумок синку подій (<c>SourceEventSyncJob</c>) у СИРОМУ конверті повідомлення задачі: ключ і параметри
/// <c>jobs.sourceEventsDone</c>, а з D-464 — і блок ліміту видалення для кнопки «Підтвердити видалення».
/// </summary>
/// <remarks>
/// ⛔ Окреме поле <see cref="JobStatus.EventRemovalBlocked"/>, а не розбір тексту на клієнті: повідомлення приходить
/// клієнтові вже перекладеним (<c>JobProgressMessageResolver</c>), лічильників із нього не дістати — той самий прийом,
/// що <see cref="FormulaRecalcOutcome"/>.
/// </remarks>
public static class SourceEventSyncOutcome
{
    /// <summary>Ключ підсумку прогону.</summary>
    public const string DoneKey = "jobs.sourceEventsDone";

    /// <summary>Параметр: скільки мапінгів заблоковано лімітом видалення (0 — не заблоковано).</summary>
    public const string RemovalBlockedParam = "removalBlocked";

    /// <summary>Параметр: кандидати на видалення в заблокованих мапінгах.</summary>
    public const string RemovalCountParam = "removalCount";

    /// <summary>Параметр: межа без підтвердження.</summary>
    public const string RemovalLimitParam = "removalLimit";

    /// <summary>Параметр: зв'язків-з-рядками у вікні.</summary>
    public const string RemovalLinkedParam = "removalLinked";

    /// <summary>Параметр: скільки подій не записано через поданий/затверджений аркуш (D-464).</summary>
    public const string SheetLockedParam = "sheetLocked";

    /// <summary>
    /// Блок ліміту видалення з сирого конверта <c>Succeeded</c> синку; <c>null</c> — інша задача, не завершена,
    /// старий конверт чи видалення не блокувалось.
    /// </summary>
    /// <param name="state">Збережений стан задачі.</param>
    /// <param name="rawMessage">Сире повідомлення (до резолвера каталогу).</param>
    public static SourceEventRemovalBlock? RemovalBlockOf(string state, string? rawMessage)
    {
        if (!string.Equals(state, "Succeeded", StringComparison.Ordinal)
            || !JobProgressMessageCodec.TryDecode(rawMessage, out var envelope)
            || !string.Equals(envelope.Key, DoneKey, StringComparison.Ordinal)
            || envelope.Params is not { } p
            || Number(p, RemovalBlockedParam) is not { } maps
            || maps == 0)
        {
            return null;
        }

        return new SourceEventRemovalBlock(
            maps,
            Number(p, RemovalCountParam) ?? 0,
            Number(p, RemovalLimitParam) ?? 0,
            Number(p, RemovalLinkedParam) ?? 0);
    }

    private static int? Number(IReadOnlyDictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var text)
           && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value >= 0
            ? value
            : null;
}
