using Ecr.Domain.Entities.Calculations;
using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>Вид відповіді зіставлення результату методології з коміркою.</summary>
public enum CalculatedMatchKind : byte
{
    /// <summary>Значення для комірки немає: результатів немає або предикат прив'язки не збігся.</summary>
    NoValue,

    /// <summary>Є одне узгоджене значення.</summary>
    Value,

    /// <summary>Результати розходяться (D-401): комірку формувати не можна.</summary>
    Ambiguous,
}

/// <summary>Відповідь <see cref="CalculatedResultMatching.Resolve"/>.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Total">Сума по речовинах; лише для <see cref="CalculatedMatchKind.Value"/>.</param>
/// <param name="UnitId">Одиниця виходу; лише для <see cref="CalculatedMatchKind.Value"/>.</param>
public readonly record struct CalculatedMatch(CalculatedMatchKind Kind, decimal Total = 0m, int UnitId = 0);

/// <summary>
/// Чисте ядро зіставлення «результат методології → значення колонки <c>Calculated</c>» (MS, план §13.9).
/// </summary>
/// <remarks>
/// ⛔ Це РІВНО те правило, яким <see cref="CalculatedCellOverlay"/> показує число в сітці; винесене без зміни
/// поведінки, щоб другий споживач (рушій скрипта: колонка <c>Calculated</c> як поле рядка) не мав власної копії,
/// яка розійшлася б із сіткою на першому ж виправленні. Правило відбору (D-401): A — результат ЦЬОГО екземпляра
/// таблиці; B — без екземпляра (прогони до D-401); C — чужий екземпляр ігнорується; є хоч одне A — B не береться.
/// Далі предикат прив'язки (<c>MatchJson</c>) над текстом комірок рядка; далі сума по речовинах: для однієї
/// речовини законне значення рівно одне, розбіжність — <see cref="CalculatedMatchKind.Ambiguous"/>.
/// </remarks>
public static class CalculatedResultMatching
{
    /// <summary>Зіставляє результати одного рядка й виходу з прив'язкою.</summary>
    /// <param name="matchJson">Предикат прив'язки (<c>cfg.CalculationBinding.MatchJson</c>).</param>
    /// <param name="candidates">
    /// Результати цього рядка й виходу, уже звужені до версій методології прив'язки.
    /// </param>
    /// <param name="ownInstance">Екземпляр таблиці комірки; <c>null</c> — «свого» екземпляра немає.</param>
    /// <param name="matchValues">Текст комірок рядка за ключем <c>ColumnDefId</c>; викликається ліниво, лише для предиката.</param>
    /// <returns>Значення, його відсутність або неоднозначність.</returns>
    public static CalculatedMatch Resolve(
        string matchJson,
        IReadOnlyList<CalculationResultRow> candidates,
        long? ownInstance,
        Func<IReadOnlyDictionary<string, string?>> matchValues)
    {
        ArgumentNullException.ThrowIfNull(matchJson);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(matchValues);

        var values = candidates.Where(r => ownInstance is not null && r.TableInstanceId == ownInstance).ToList();
        if (values.Count == 0)
        {
            values = [.. candidates.Where(r => r.TableInstanceId is null)];
        }

        if (values.Count == 0)
        {
            return new CalculatedMatch(CalculatedMatchKind.NoValue);
        }

        if (!IsCatchAll(matchJson) && !MethodologyRuleMatcher.Matches(matchJson, matchValues()))
        {
            return new CalculatedMatch(CalculatedMatchKind.NoValue);
        }

        return TryCombine(values, out var total, out var unitId)
            ? new CalculatedMatch(CalculatedMatchKind.Value, total, unitId)
            : new CalculatedMatch(CalculatedMatchKind.Ambiguous);
    }

    /// <summary>Предикат <c>{}</c> — прив'язка на всі рядки.</summary>
    /// <param name="matchJson">Предикат прив'язки.</param>
    public static bool IsCatchAll(string matchJson)
    {
        ArgumentNullException.ThrowIfNull(matchJson);
        return string.Equals(matchJson.Replace(" ", string.Empty, StringComparison.Ordinal), "{}", StringComparison.Ordinal);
    }

    private static bool TryCombine(List<CalculationResultRow> values, out decimal total, out int unitId)
    {
        total = 0m;
        unitId = values[0].UnitId;

        foreach (var group in values.GroupBy(v => v.SubstanceEntryId))
        {
            var first = group.First();
            if (group.Any(v => v.Value != first.Value || v.UnitId != first.UnitId))
            {
                return false;
            }

            total += first.Value;
        }

        return true;
    }
}
