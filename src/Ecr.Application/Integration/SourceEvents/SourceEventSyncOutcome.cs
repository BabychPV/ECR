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
/// <param name="PerMap">
/// ✎ Аудит 31.10 (G3): заблоковані мапінги поіменно — саме їх (і не більше кандидатів) підтверджує людина
/// (<c>POST …/source-events/sync</c> з <c>confirmRemovals</c>); <c>null</c> — конверт до цієї правки.
/// </param>
public sealed record SourceEventRemovalBlock(
    int Maps, int Candidates, int Limit, int Linked, IReadOnlyList<SourceEventRemovalConfirmation>? PerMap = null);

/// <summary>
/// ✎ Аудит 31.10 (G3): підтвердження масового видалення, прив'язане до того, що людина бачила: мапінг і скільки
/// кандидатів на видалення в ньому було заблоковано. Задача видаляє понад ліміт лише в цьому мапінгу й лише якщо
/// кандидатів зараз не більше; інакше — звичайні межі й новий банер.
/// </summary>
/// <param name="MapId">Мапінг подій.</param>
/// <param name="Candidates">Скільки рядків людина погодилась видалити (кандидатів у заблокованому прогоні).</param>
public sealed record SourceEventRemovalConfirmation(int MapId, int Candidates);

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

    /// <summary>✎ Аудит 31.10 (G3): параметр «мапінг:кандидати» заблокованих мапінгів через кому.</summary>
    public const string RemovalMapsParam = "removalMaps";

    /// <summary>Стеля підтверджень в одному запиті (мапінгів однієї сутності).</summary>
    public const int MaxConfirmations = 200;

    /// <summary>Значення <see cref="RemovalMapsParam"/>: <c>12:37,15:20</c>.</summary>
    /// <param name="perMap">Заблоковані мапінги.</param>
    public static string FormatMaps(IEnumerable<SourceEventRemovalConfirmation> perMap)
        => string.Join(
            ',',
            perMap.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.MapId}:{m.Candidates}")));

    /// <summary>Розбирає <see cref="RemovalMapsParam"/>; нерозбірне — пропускається.</summary>
    /// <param name="text">Значення параметра.</param>
    public static IReadOnlyList<SourceEventRemovalConfirmation> ParseMaps(string? text)
    {
        var result = new List<SourceEventRemovalConfirmation>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':');
            if (pair.Length == 2
                && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var map) && map > 0
                && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
            {
                result.Add(new SourceEventRemovalConfirmation(map, count));
            }
        }

        return result;
    }

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
            Number(p, RemovalLinkedParam) ?? 0,
            p.TryGetValue(RemovalMapsParam, out var perMap) ? ParseMaps(perMap) : null);
    }

    private static int? Number(IReadOnlyDictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var text)
           && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value >= 0
            ? value
            : null;
}
