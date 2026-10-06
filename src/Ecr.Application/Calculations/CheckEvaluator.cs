// src/Ecr.Application/Calculations/CheckEvaluator.cs
using Ecr.Application.Templates;
using Ecr.Application.Validation;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>Результат порівняння Check.</summary>
/// <param name="Compared">Чи порівнювалось: порожнє значення з будь-якого боку — не порівнюється.</param>
/// <param name="Passed">Чи в межах допуску (для непорівняного — <c>true</c>).</param>
/// <param name="Deviation">|left − right|.</param>
/// <param name="Allowed">Допустиме відхилення (abs: tolerance; rel: tolerance × |right|).</param>
public sealed record CheckResult(bool Compared, bool Passed, decimal Deviation, decimal Allowed);

/// <summary>Знахідка Check для одного рядка.</summary>
/// <param name="TargetRowKey">Рядок приймача (right).</param>
/// <param name="SourceRowKey">Рядок джерела (left).</param>
/// <param name="Left">Значення left.</param>
/// <param name="Right">Значення right.</param>
/// <param name="Result">Результат.</param>
public sealed record CheckRowResult(string TargetRowKey, string SourceRowKey, decimal? Left, decimal? Right, CheckResult Result)
{
    /// <summary>|left − right|.</summary>
    public decimal Deviation => Result.Deviation;

    /// <summary>Допустиме відхилення.</summary>
    public decimal Allowed => Result.Allowed;
}

/// <summary>
/// Чиста функція Check (D-230; схема — ПРИПУЩЕННЯ, див. <c>RelationSpec.cs</c>):
/// Pass, якщо |left − right| ≤ допуск (abs) або ≤ допуск × |right| (rel). Рівність допуску — Pass.
/// </summary>
public static class CheckEvaluator
{
    /// <summary>Порівнює пару значень.</summary>
    /// <param name="spec">Налаштування Check.</param>
    /// <param name="left">Значення колонки джерела.</param>
    /// <param name="right">Значення колонки приймача.</param>
    /// <returns>Результат; з порожнім значенням — не порівняно.</returns>
    public static CheckResult Evaluate(CheckSpec spec, decimal? left, decimal? right)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (left is null || right is null)
        {
            return new CheckResult(Compared: false, Passed: true, 0m, 0m);
        }

        // ⚠ Переповнення decimal насичується до MaxValue, а не кидає (аудит L7-03): відхилення
        // понад decimal — провал, допуск понад decimal — пропускає будь-яке представне відхилення.
        var deviation = Saturated(() => Math.Abs(left.Value - right.Value));
        var allowed = spec.ToleranceKind == CheckToleranceKind.Rel
            ? Saturated(() => spec.Tolerance * Math.Abs(right.Value))
            : spec.Tolerance;

        return new CheckResult(Compared: true, deviation <= allowed, deviation, allowed);
    }

    /// <summary>Порівнює всі пари рядків за зіставленням (див. <see cref="RollupEvaluator"/>: ті самі правила ключів).</summary>
    /// <param name="match">Зіставлення.</param>
    /// <param name="spec">Налаштування Check.</param>
    /// <param name="source">Рядки джерела.</param>
    /// <param name="target">Рядки приймача.</param>
    /// <returns>Лише НЕуспішні порівняння; порожньо — усе гаразд або порівнювати нема чого.</returns>
    /// <remarks>
    /// Порожні keys: єдиний рядок приймача проти кожного рядка джерела; приймач не з одного рядка — нічого
    /// не порівнюється. Рядок без пари мовчки пропускається (відсутність пари — не відхилення).
    /// </remarks>
    public static IReadOnlyList<CheckRowResult> Failures(
        RelationMatchSpec match, CheckSpec spec, IReadOnlyList<RelationRow> source, IReadOnlyList<RelationRow> target)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var failures = new List<CheckRowResult>();

        void Compare(RelationRow s, RelationRow t)
        {
            var left = s.Numbers.TryGetValue(spec.Left, out var l) ? l : null;
            var right = t.Numbers.TryGetValue(spec.Right, out var r) ? r : null;
            var result = Evaluate(spec, left, right);
            if (result.Compared && !result.Passed)
            {
                failures.Add(new CheckRowResult(t.RowKey, s.RowKey, left, right, result));
            }
        }

        if (match.Keys.Count == 0)
        {
            if (target.Count == 1)
            {
                foreach (var s in source)
                {
                    Compare(s, target[0]);
                }
            }

            return failures;
        }

        foreach (var s in source)
        {
            var sk = match.Keys.Select(k => s.KeyOf(k.Source)).ToList();
            if (sk.Any(k => k is null))
            {
                continue;
            }

            foreach (var t in target)
            {
                if (match.Keys.Select(k => t.KeyOf(k.Target)).SequenceEqual(sk, StringComparer.Ordinal))
                {
                    Compare(s, t);
                }
            }
        }

        return failures;
    }

    /// <summary>Перекладає знахідки в повідомлення панелі валідації (рівні: Info/Warn→Warning/Block→Error).</summary>
    /// <param name="relationCode">Код зв'язку.</param>
    /// <param name="targetTableDefId">Таблиця приймача (адреса знахідки).</param>
    /// <param name="spec">Налаштування Check.</param>
    /// <param name="failures">Знахідки.</param>
    /// <param name="language">Мова запиту (<c>en</c>/<c>ru</c>/<c>kz</c>).</param>
    /// <param name="sourceTableDefId">Таблиця джерела: текст містить її значення, тож читач без права на неї повідомлення не бачить (T1-01).</param>
    /// <returns>Повідомлення; <c>BlocksSave = false</c> (блокує лише подання, R-B3).</returns>
    public static IReadOnlyList<ValidationMessage> ToMessages(
        string relationCode, int targetTableDefId, CheckSpec spec, IReadOnlyList<CheckRowResult> failures, string language, int? sourceTableDefId = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(failures);

        var severity = spec.Severity switch
        {
            CheckSeverity.Block => ValidationSeverity.Error,
            CheckSeverity.Warn => ValidationSeverity.Warning,
            _ => ValidationSeverity.Info,
        };

        // A2-04: без ключів зіставлення ОДИН рядок приймача порівнюється з КОЖНИМ рядком джерела, тож кілька
        // знахідок мають ту саму адресу (рядок/колонка приймача). Текст несе рядок джерела ({sourceRow}), інакше
        // панель показувала N однакових рядків; повністю однакові знахідки (той самий рядок джерела, напр. з двох
        // екземплярів таблиці) зводяться в одну.
        return [.. failures
            .DistinctBy(f => (f.TargetRowKey, f.SourceRowKey, f.Left, f.Right))
            .Select(f =>
            {
                // T2-07: ключ + підстановки зберігаються, текст — запасний (мова автора запуску); читання збирає
                // його мовою читача. Значення джерела (Left) лежать лише в Params, а їх назовні не віддають.
                var parameters = Params(spec, f);
                return new ValidationMessage(
                    severity, "REL-" + relationCode, ValidationMessageTemplates.Render(ValidationMessageTemplates.CheckMismatchRow, language, parameters),
                    targetTableDefId, f.TargetRowKey, spec.Right, BlocksSave: false,
                    SourceTableDefId: sourceTableDefId, SourceColumnCode: sourceTableDefId is null ? null : spec.Left,
                    MessageKey: ValidationMessageTemplates.CheckMismatchRow, Params: parameters);
            })];
    }

    /// <summary>
    /// A2-03: інформаційна підказка автору шаблону — Check без ключів порівнює лише тоді, коли в приймачі рівно
    /// один рядок; інакше (і коли невідоме поле зіставлення дало порожній <c>keys</c>) нічого не порівнюється.
    /// Політику D-230 не змінює: це <c>Info</c>, не блокує ні збереження, ні подання.
    /// </summary>
    /// <param name="relationCode">Код зв'язку.</param>
    /// <param name="targetTableDefId">Таблиця приймача.</param>
    /// <param name="match">Зіставлення.</param>
    /// <param name="source">Рядки джерела.</param>
    /// <param name="target">Рядки приймача.</param>
    /// <param name="language">Мова запиту.</param>
    /// <returns>Повідомлення або <c>null</c>, коли підказка не потрібна.</returns>
    public static ValidationMessage? NoKeysNotice(
        string relationCode, int targetTableDefId, RelationMatchSpec match,
        IReadOnlyList<RelationRow> source, IReadOnlyList<RelationRow> target, string language)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (match.Keys.Count != 0 || source.Count == 0 || target.Count == 1)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["targetRows"] = target.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return new ValidationMessage(
            ValidationSeverity.Info, "REL-" + relationCode,
            ValidationMessageTemplates.Render(ValidationMessageTemplates.CheckNoKeys, language, parameters),
            targetTableDefId, RowKey: null, ColumnCode: null, BlocksSave: false,
            MessageKey: ValidationMessageTemplates.CheckNoKeys, Params: parameters);
    }

    private static decimal Saturated(Func<decimal> nonNegative)
    {
        try
        {
            return nonNegative();
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    private static Dictionary<string, string> Params(CheckSpec s, CheckRowResult f)
    {
        return new(StringComparer.Ordinal)
        {
            ["left"] = s.Left,
            ["leftValue"] = ValidationMessageTemplates.Num(f.Left),
            ["sourceRow"] = f.SourceRowKey,
            ["right"] = s.Right,
            ["rightValue"] = ValidationMessageTemplates.Num(f.Right),
            ["deviation"] = ValidationMessageTemplates.Num(f.Deviation),
            ["allowed"] = ValidationMessageTemplates.Num(f.Allowed),
            ["kind"] = s.ToleranceKind == CheckToleranceKind.Rel ? "rel" : "abs",
        };
    }
}
