// src/Ecr.Application/Registries/Rules/RegistryRuleViolation.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Rules;

/// <summary>
/// Порушення правила довідника одним записом (RT-17a, FEATURE-REGISTRY-TABLES §6, §7.1).
/// </summary>
/// <param name="EntryId">Запис, на якому правило не виконалося.</param>
/// <param name="EntryCode">Його код — для людини.</param>
/// <param name="Rule">Код правила.</param>
/// <param name="Severity">Рівень: <c>Info</c>, <c>Warning</c> або <c>Error</c>.</param>
/// <param name="MessageKey">Ключ тексту в каталозі.</param>
/// <param name="Params">
/// Параметри тексту: <c>rule</c>, <c>entryCode</c>, <c>message</c> (текст правила мовою
/// користувача); за потреби <c>value</c> (значення Σ шаблону «Сума дочірніх»),
/// <c>field</c>, <c>errorCode</c> (вираз дав помилку-значення).
/// </param>
public sealed record RegistryRuleViolationDto(
    long EntryId,
    string EntryCode,
    string Rule,
    string Severity,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Params);

/// <summary>Результат перевірки правил після запису.</summary>
/// <param name="Violations">Усі порушення, в порядку правил і записів.</param>
public sealed record RegistryRuleCheck(IReadOnlyList<RegistryRuleViolationDto> Violations)
{
    /// <summary>Порожній результат: правил немає або всі виконані.</summary>
    public static RegistryRuleCheck None { get; } = new([]);

    /// <summary>Порушення, що блокують запис: рівень <c>Error</c>.</summary>
    public IReadOnlyList<RegistryRuleViolationDto> Errors => [.. Violations.Where(v => IsBlocking(v.Severity))];

    /// <summary>
    /// Порушення, що запис НЕ блокують (<c>Info</c>, <c>Warning</c>): запис зберігається, а вони
    /// їдуть у <c>warnings[]</c> відповіді (§6, «Рівні»).
    /// </summary>
    public IReadOnlyList<RegistryRuleViolationDto> Warnings => [.. Violations.Where(v => !IsBlocking(v.Severity))];

    /// <summary>Чи блокує рівень запис.</summary>
    /// <param name="severity">Рівень правила.</param>
    /// <remarks>
    /// ⛔ Блокує ЛИШЕ <c>Error</c> — рівень обирає адміністратор (G-6). <c>Warning</c> як блок
    /// зробив би попередження забороною, а <c>Error</c> як попередження — правило декоративним.
    /// </remarks>
    public static bool IsBlocking(string severity)
        => string.Equals(severity, nameof(ValidationSeverity.Error), StringComparison.Ordinal);

    /// <summary>Відмовляє, якщо є хоч одне порушення рівня <c>Error</c>.</summary>
    /// <exception cref="BusinessRuleException">
    /// <c>422 ECR-REG-4221</c>, <c>ruleViolated</c>: перше порушення в <c>rule</c>/<c>entryCode</c>/<c>message</c>,
    /// повний перелік — у <c>violations</c>.
    /// </exception>
    public void ThrowIfErrors()
    {
        var errors = Errors;
        if (errors.Count == 0)
        {
            return;
        }

        var first = errors[0];
        var message = first.Params.GetValueOrDefault("message") ?? first.Rule;
        throw new BusinessRuleException(
            ErrorCodes.RegistryRuleViolation,
            $"Порушено правило довідника «{first.Rule}» на записі «{first.EntryCode}»: {message}. "
            + $"Усього порушень рівня Error: {errors.Count.ToString(CultureInfo.InvariantCulture)}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RegistryRuleEngine.RuleViolatedErrorKey,
                ["rule"] = first.Rule,
                ["entryCode"] = first.EntryCode,
                ["message"] = message,
                ["violations"] = errors,
            });
    }
}
