// src/Ecr.Application/Calculations/CheckEvaluator.cs
using System.Globalization;
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
public sealed record CheckRowResult(string TargetRowKey, string SourceRowKey, decimal? Left, decimal? Right, CheckResult Result);

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

        var deviation = Math.Abs(left.Value - right.Value);
        var allowed = spec.ToleranceKind == CheckToleranceKind.Rel
            ? spec.Tolerance * Math.Abs(right.Value)
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
    /// <returns>Повідомлення; <c>BlocksSave = false</c> (блокує лише подання, R-B3).</returns>
    public static IReadOnlyList<ValidationMessage> ToMessages(
        string relationCode, int targetTableDefId, CheckSpec spec, IReadOnlyList<CheckRowResult> failures, string language)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(failures);

        var severity = spec.Severity switch
        {
            CheckSeverity.Block => ValidationSeverity.Error,
            CheckSeverity.Warn => ValidationSeverity.Warning,
            _ => ValidationSeverity.Info,
        };

        return [.. failures.Select(f => new ValidationMessage(
            severity, "REL-" + relationCode, Text(spec, f, language), targetTableDefId, f.TargetRowKey, spec.Right, BlocksSave: false))];
    }

    private static string N(decimal? v) => v?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;

    // ⚠ Не через каталог UiString: ValidationMessage.Message — готовий текст у нормальній (200) відповіді,
    // три мови — закритий список (D-95); та сама форма, що в ValidationEngine.L.
    private static string Text(CheckSpec s, CheckRowResult f, string language)
    {
        var kind = s.ToleranceKind == CheckToleranceKind.Rel ? "rel" : "abs";
        return language switch
        {
            "ru" => $"Сверка: {s.Left} = {N(f.Left)} не сходится с {s.Right} = {N(f.Right)}: отклонение {N(f.Result.Deviation)}, допустимо {N(f.Result.Allowed)} ({kind}).",
            "kz" => $"Салыстыру: {s.Left} = {N(f.Left)} мәні {s.Right} = {N(f.Right)} мәніне сәйкес келмейді: ауытқу {N(f.Result.Deviation)}, рұқсат етілгені {N(f.Result.Allowed)} ({kind}).",
            _ => $"Check: {s.Left} = {N(f.Left)} does not match {s.Right} = {N(f.Right)}: deviation {N(f.Result.Deviation)}, allowed {N(f.Result.Allowed)} ({kind}).",
        };
    }
}
