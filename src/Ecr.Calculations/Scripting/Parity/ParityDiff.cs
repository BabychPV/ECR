using System.Globalization;

namespace Ecr.Calculations.Scripting.Parity;

/// <summary>
/// Чисте порівняння двох шляхів розрахунку (MS, план §8.4): множини рядків і, для рядків, відібраних обома, —
/// трійки <c>(OutputCode, SubstanceEntryId, Kind)</c>, ТОЧНА рівність <see cref="decimal"/>, одиниці, наявність і склад
/// <c>MissingArguments</c>, наявність <c>StepError</c>, відхилення рядка.
/// </summary>
/// <remarks>
/// ⛔ Порівняння не послаблюється допусками й округленням: розбіжність виправляють у конвертері, рушії чи тексті.
/// Тут немає ні бази, ні модулів — лише дані двох сторін.
/// </remarks>
public static class ParityDiff
{
    private delegate void AddMismatch(string? row, string aspect, string? oldText, string? newText);

    /// <summary>Порівнює дві сторони.</summary>
    /// <param name="target">Адреса порівняння (потрапляє в кожну розбіжність).</param>
    /// <param name="old">Старий шлях (правила + <c>GenericCalculationModule</c>).</param>
    /// <param name="script">Шлях скрипта.</param>
    public static ParityReport Compare(ParityTarget target, ParitySide old, ParitySide script)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(script);

        var mismatches = new List<ParityMismatch>();

        void Add(string? row, string aspect, string? oldText, string? newText)
            => mismatches.Add(new ParityMismatch(
                target.MethodologyCode, target.Version, target.VersionId, target.DocumentId, target.PeriodKey,
                target.TableInstanceId, row, aspect, oldText, newText));

        AddMismatch add = Add;
        SetDiff(old.Selected, script.Selected, ParityAspect.RowSelected, "selected", add);
        SetDiff(old.Excluded, script.Excluded, ParityAspect.RowExcluded, "excluded", add);
        SetDiff(old.AllRows ?? [], script.AllRows ?? [], ParityAspect.RowAll, "present", add);

        var rowsCompared = 0;
        var valuesCompared = 0;
        var common = old.Selected.Intersect(script.Selected, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var rowKey in common)
        {
            if (!old.Rows.TryGetValue(rowKey, out var left) || !script.Rows.TryGetValue(rowKey, out var right))
            {
                Add(
                    rowKey,
                    ParityAspect.Error,
                    old.Rows.ContainsKey(rowKey) ? "outcome" : "no outcome",
                    script.Rows.ContainsKey(rowKey) ? "outcome" : "no outcome");
                continue;
            }

            rowsCompared++;
            valuesCompared += CompareRow(rowKey, left, right, add);
        }

        return new ParityReport(mismatches, rowsCompared, valuesCompared);
    }

    private static int CompareRow(string rowKey, ParityRowOutcome old, ParityRowOutcome script, AddMismatch add)
    {
        if (!string.Equals(old.Error, script.Error, StringComparison.Ordinal))
        {
            add(rowKey, ParityAspect.Error, old.Error, script.Error);
        }

        if (old.Rejected != script.Rejected)
        {
            add(rowKey, ParityAspect.Rejected, Flag(old.Rejected), Flag(script.Rejected));
        }

        if (old.HasStepError != script.HasStepError)
        {
            add(rowKey, ParityAspect.StepErrorPresence, Flag(old.HasStepError), Flag(script.HasStepError));
        }

        var oldMissing = old.MissingArguments is { Count: > 0 } a ? a : null;
        var newMissing = script.MissingArguments is { Count: > 0 } b ? b : null;
        if ((oldMissing is null) != (newMissing is null))
        {
            add(rowKey, ParityAspect.MissingArgumentsPresence, Join(oldMissing), Join(newMissing));
        }
        else if (oldMissing is not null && newMissing is not null
                 && !oldMissing.ToHashSet(StringComparer.Ordinal).SetEquals(newMissing))
        {
            add(rowKey, ParityAspect.MissingArgumentsSet, Join(oldMissing), Join(newMissing));
        }

        return CompareValues(rowKey, old.Values, script.Values, add);
    }

    private static int CompareValues(
        string rowKey, IReadOnlyList<ParityValue> old, IReadOnlyList<ParityValue> script, AddMismatch add)
    {
        var oldByKey = Index(rowKey, old, "old", add);
        var newByKey = Index(rowKey, script, "new", add);
        var compared = 0;

        foreach (var key in oldByKey.Keys.Concat(newByKey.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasOld = oldByKey.TryGetValue(key, out var left);
            var hasNew = newByKey.TryGetValue(key, out var right);
            if (hasOld && !hasNew)
            {
                add(rowKey, ParityAspect.ValueMissing, Describe(left!), null);
                continue;
            }

            if (!hasOld && hasNew)
            {
                add(rowKey, ParityAspect.ValueExtra, null, Describe(right!));
                continue;
            }

            compared++;
            if (left!.Value != right!.Value)
            {
                add(rowKey, ParityAspect.ValueNumber, key + " = " + Number(left.Value), key + " = " + Number(right.Value));
            }

            if (left.UnitId != right.UnitId)
            {
                add(
                    rowKey,
                    ParityAspect.ValueUnit,
                    key + " unit " + left.UnitId.ToString(CultureInfo.InvariantCulture),
                    key + " unit " + right.UnitId.ToString(CultureInfo.InvariantCulture));
            }
        }

        return compared;
    }

    private static Dictionary<string, ParityValue> Index(
        string rowKey, IReadOnlyList<ParityValue> values, string side, AddMismatch add)
    {
        var map = new Dictionary<string, ParityValue>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!map.TryAdd(value.Key, value))
            {
                add(rowKey, ParityAspect.ValueDuplicate, side == "old" ? value.Key : null, side == "new" ? value.Key : null);
            }
        }

        return map;
    }

    private static void SetDiff(
        IReadOnlyList<string> old, IReadOnlyList<string> script, string aspect, string present, AddMismatch add)
    {
        var left = old.ToHashSet(StringComparer.Ordinal);
        var right = script.ToHashSet(StringComparer.Ordinal);
        foreach (var key in left.Except(right).Order(StringComparer.Ordinal))
        {
            add(key, aspect, present, "absent");
        }

        foreach (var key in right.Except(left).Order(StringComparer.Ordinal))
        {
            add(key, aspect, "absent", present);
        }
    }

    private static string Describe(ParityValue value)
        => value.Key + " = " + Number(value.Value) + " unit " + value.UnitId.ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "yes" : "no";

    private static string? Join(IReadOnlyList<string>? values)
        => values is null ? null : string.Join(",", values.Order(StringComparer.Ordinal));
}
