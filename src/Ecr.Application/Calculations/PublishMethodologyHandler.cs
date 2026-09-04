// src/Ecr.Application/Calculations/PublishMethodologyHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона змінює числа, які вже подані.
/// </summary>
/// <remarks>
/// Тому тут diff **результатів** на золотому наборі, а не diff коду (ФВ-9.6):
/// змінений рядок формули нічого не каже, а змінена на 4 % емісія — каже все.
/// </remarks>
public sealed class PublishMethodologyHandler(
    ICalculationModule module,
    IMethodologyStore methodologies,
    IFormulaEngine formulaEngine,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на публікацію методології (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ Право **небезпечне** (`sec.Permission.IsDangerous = 1`): вбудованим
    /// ролям воно seed-ом не видається взагалі (ФВ-6.12, D-40). Публікація
    /// методології змінює вже подані числа, і «випадково мати» таке право не
    /// має ніхто.
    /// </remarks>
    public const string Permission = "Calculation.Publish";

    /// <summary>Публікує версію.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="changeReason">Причина зміни; обов'язкова (ФВ-14.7).</param>
    /// <param name="effectiveFrom">Дата набуття чинності; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Diff результатів на золотому наборі.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0409</c> — публікує автор або дата зайнята;
    /// <c>ECR-CALC-0422</c> — немає причини, дати, зеленого тесту або є цикл.
    /// </exception>
    public async Task<MethodologyPublicationDiff> HandleAsync(
        int methodologyVersionId, string changeReason, DateOnly? effectiveFrom, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не публікує методології.");

        var methodology = await methodologies.FindByVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var version = methodology.Versions.Single(v => v.Id == methodologyVersionId);

        // ⛔ Дата набуття чинності обов'язкова і перевіряється ПЕРШОЮ. Без неї
        // версія не має місця в часі: незрозуміло, які періоди рахувати нею, а
        // які — попередньою, і `VersionOn` не має відповіді. У схемі це
        // `CK_MV_Published` (`EffectiveFrom IS NOT NULL` для опублікованої).
        if (effectiveFrom is not { } from)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Публікація версії {version.Version} без дати набуття чинності неможлива: "
                + "без неї невідомо, які періоди рахувати цією версією.");
        }

        // Diff рахується ДО публікації: після неї попередня версія вже не та,
        // що була чинною, і порівнювати стало б нема з чим.
        var previous = methodology.VersionOn(from.AddDays(-1));
        var diff = await BuildDiffAsync(methodology, version, previous, ct).ConfigureAwait(false);

        // Зелений тест — не прапорець, а факт: усі випадки золотого набору
        // зійшлися в межах допуску (ФВ-9.12, ФВ-13.7).
        var testCases = await methodologies.GetTestCasesAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var greenTest = await IsGreenAsync(methodology, version, testCases, ct).ConfigureAwait(false);

        await ApplyEvaluationOrderAsync(methodologyVersionId, ct).ConfigureAwait(false);

        // Чотири очі, причина, зелений тест і незайнята дата — усе в домені:
        // правило, розкидане по обробниках, забудеться на другому виклику.
        methodology.PublishVersion(version, userId, changeReason, from, greenTest, clock.UtcNow);

        // ⚠ У журнал іде diff РЕЗУЛЬТАТІВ, а не тексту формул (ФВ-9.6).
        // Змінений рядок виразу не каже нічого; змінена на 4 % емісія каже все.
        await audit.WritePublicationEventAsync(
            new PublicationEventRecord(
                ChangedAt: clock.UtcNow,
                EntityType: "calc.MethodologyVersion",
                EntityId: methodologyVersionId,
                ResultDiffJson: JsonSerializer.Serialize(diff),
                ChangeReason: changeReason,
                ChangedByUserId: userId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Жодного перерахунку тут не планується. Закриті періоди не
        // перераховуються автоматично НІКОЛИ (ФВ-9.7): інакше публікація
        // методології заднім числом мовчки змінює подану звітність.
        return diff;
    }

    /// <summary>Проставляє топологічний порядок формул (ФВ-9.4).</summary>
    /// <remarks>
    /// Порядок обчислюється саме тут, при публікації, а не в рантаймі: цикл
    /// має бути відмовою публікації, а не тихо неправильним числом. У рантаймі
    /// його вже пізно ловити — результат подано.
    /// </remarks>
    private async Task ApplyEvaluationOrderAsync(int methodologyVersionId, CancellationToken ct)
    {
        var formulas = await methodologies.GetFormulasAsync(methodologyVersionId, ct).ConfigureAwait(false);
        if (formulas.Count == 0)
        {
            return;
        }

        // ⚠ Незбережена формула має Id = 0, і граф зіставляється саме за Id.
        // Дві такі формули дали б «Sequence contains more than one matching
        // element» глибоко в сортуванні; тут причина названа одразу.
        if (formulas.Any(f => !f.IsPersisted))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Формули версії мають бути збережені до публікації: "
                + "топологічний порядок зіставляється за ідентифікаторами.");
        }

        var byCode = formulas.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

        var nodes = formulas
            .Select(f => new FormulaNode(
                f.Id,
                TableDefId: 0,
                FormulaScope.Column,
                ColumnDefId: null,
                RowDefId: null,
                DependsOn(f, byCode)))
            .ToList();

        var ordering = formulaEngine.BuildEvaluationOrder(nodes);
        if (!ordering.IsSuccess)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-4221",
                "Формули версії утворюють цикл: " + string.Join(" → ", ordering.CyclePath ?? []));
        }

        var position = 0;
        foreach (var formulaId in ordering.Order)
        {
            formulas.Single(f => f.Id == formulaId).SetEvaluationOrder(++position);
        }
    }

    /// <summary>Формули, на які посилається ця через <c>!Code</c>.</summary>
    private static List<int> DependsOn(
        MethodologyFormula formula, Dictionary<string, MethodologyFormula> byCode)
        => byCode
            .Where(pair => !string.Equals(pair.Key, formula.Code, StringComparison.OrdinalIgnoreCase))
            .Where(pair => formula.Expression.Contains(
                "!" + pair.Key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value.Id)
            .ToList();

    /// <summary>Чи зійшовся золотий набір у межах допуску.</summary>
    private async Task<bool> IsGreenAsync(
        Methodology methodology,
        MethodologyVersion version,
        IReadOnlyList<MethodologyTestCase> testCases,
        CancellationToken ct)
    {
        // ⛔ Порожній набір — НЕ зелений. «Тестів немає, отже все гаразд» —
        // саме та підміна, через яку публікація без перевірки виглядає як
        // публікація з перевіркою.
        if (testCases.Count == 0)
        {
            return false;
        }

        foreach (var testCase in testCases)
        {
            var output = await module
                .ExecuteAsync(WithDescriptor(testCase.Input, Descriptor(methodology, version)), ct)
                .ConfigureAwait(false);

            foreach (var (code, expected) in testCase.Expected)
            {
                var actual = output.Values.FirstOrDefault(v => v.OutputCode == code);
                if (actual is null || Math.Abs(actual.Value - expected) > testCase.Tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Diff результатів між новою версією і попередньою чинною.</summary>
    private async Task<MethodologyPublicationDiff> BuildDiffAsync(
        Methodology methodology,
        MethodologyVersion version,
        MethodologyVersion? previous,
        CancellationToken ct)
    {
        var changes = new List<MethodologyResultDelta>();

        var testCases = await methodologies.GetTestCasesAsync(version.Id, ct).ConfigureAwait(false);

        if (previous is not null)
        {
            foreach (var testCase in testCases)
            {
                var after = await module
                    .ExecuteAsync(WithDescriptor(testCase.Input, Descriptor(methodology, version)), ct)
                    .ConfigureAwait(false);
                var before = await module
                    .ExecuteAsync(WithDescriptor(testCase.Input, Descriptor(methodology, previous)), ct)
                    .ConfigureAwait(false);

                foreach (var value in after.Values)
                {
                    var old = before.Values.FirstOrDefault(
                        v => v.OutputCode == value.OutputCode
                             && v.SubstanceEntryId == value.SubstanceEntryId);

                    if (old is null || old.Value != value.Value)
                    {
                        changes.Add(new MethodologyResultDelta(
                            testCase.Code, value.OutputCode, value.SubstanceEntryId,
                            old?.Value, value.Value));
                    }
                }
            }
        }

        // ⚠ Обидва режими — ОБОВ'ЯЗКОВО в diff (ФВ-7.8, D-78). Їх зміна не
        // видно в жодному рядку формули, а числа змінюються всі: 3.3 % між
        // Actual і Fixed360 на тих самих даних.
        return new MethodologyPublicationDiff(
            version.Id,
            previous?.Id,
            new MethodologyModeChange(
                previous?.NumericMode ?? version.NumericMode, version.NumericMode),
            new MethodologyCalendarChange(
                previous?.CalendarMode ?? version.CalendarMode, version.CalendarMode),
            changes);
    }

    private static MethodologyDescriptor Descriptor(Methodology methodology, MethodologyVersion version)
        => new(
            methodology.Id,
            version.Id,
            methodology.Code,
            version.Version,
            version.Level,
            version.NumericMode,
            version.CalendarMode,

            // Симуляція для diff завжди без трейсу: він тут нікому не потрібен,
            // а на золотому наборі коштує пам'яті на кожен випадок.
            TraceLevel.Off);

    private static CalculationInput WithDescriptor(CalculationInput input, MethodologyDescriptor descriptor)
        => input with { Methodology = descriptor };
}

/// <summary>
/// Diff публікації: що саме зміниться в числах.
/// </summary>
/// <param name="MethodologyVersionId">Версія, яку публікують.</param>
/// <param name="PreviousVersionId">Попередня чинна; <c>null</c> — перша версія.</param>
/// <param name="Numeric">Зміна арифметичного режиму.</param>
/// <param name="Calendar">Зміна календарної конвенції.</param>
/// <param name="Changes">Розбіжності результатів на золотому наборі.</param>
public sealed record MethodologyPublicationDiff(
    int MethodologyVersionId,
    int? PreviousVersionId,
    MethodologyModeChange Numeric,
    MethodologyCalendarChange Calendar,
    IReadOnlyList<MethodologyResultDelta> Changes)
{
    /// <summary>Чи змінює публікація хоч одне число або режим.</summary>
    public bool IsSignificant
        => Changes.Count > 0 || Numeric.IsChanged || Calendar.IsChanged;
}

/// <summary>Зміна арифметичного режиму (ФВ-9.9).</summary>
/// <param name="Before">Режим попередньої версії.</param>
/// <param name="After">Режим нової.</param>
public sealed record MethodologyModeChange(NumericMode Before, NumericMode After)
{
    /// <summary>Чи змінився режим.</summary>
    public bool IsChanged => Before != After;
}

/// <summary>Зміна календарної конвенції (ФВ-16.11).</summary>
/// <param name="Before">Конвенція попередньої версії.</param>
/// <param name="After">Конвенція нової.</param>
public sealed record MethodologyCalendarChange(CalendarMode Before, CalendarMode After)
{
    /// <summary>Чи змінилася конвенція.</summary>
    public bool IsChanged => Before != After;
}

/// <summary>Одна розбіжність результату на золотому наборі.</summary>
/// <param name="TestCode">Випадок золотого набору.</param>
/// <param name="OutputCode">Вихід методології.</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
/// <param name="Before">Значення попередньої версії; <c>null</c> — виходу не було.</param>
/// <param name="After">Значення нової версії.</param>
public sealed record MethodologyResultDelta(
    string TestCode,
    string OutputCode,
    int? SubstanceEntryId,
    decimal? Before,
    decimal After)
{
    /// <summary>Відносна зміна; <c>null</c>, якщо порівнювати нема з чим або було нуль.</summary>
    public decimal? RelativeChange
        => Before is { } before && before != 0m ? (After - before) / before : null;

    /// <summary>Текст для журналу — стабільний і не залежний від локалі.</summary>
    public override string ToString()
        => $"{TestCode}.{OutputCode}: "
           + $"{Before?.ToString(CultureInfo.InvariantCulture) ?? "—"} → "
           + After.ToString(CultureInfo.InvariantCulture);
}
