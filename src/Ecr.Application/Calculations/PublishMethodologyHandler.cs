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

        await ApplyEvaluationOrderAsync(methodology, methodologyVersionId, from, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Перевіряє версію за типами й посиланнями і проставляє топологічний
    /// порядок формул (ФВ-9.4).
    /// </summary>
    /// <remarks>
    /// Порядок обчислюється саме тут, при публікації, а не в рантаймі: цикл
    /// має бути відмовою публікації, а не тихо неправильним числом. У рантаймі
    /// його вже пізно ловити — результат подано.
    /// <para>
    /// ⛔ Один прохід на всі три питання — порядок формул, типи констант і
    /// результатів, ребра між методологіями. Розділити означало б розібрати ті
    /// самі вирази тричі й дістати три відповіді на питання «що в них
    /// написано» (<c>H-3</c>).
    /// </para>
    /// </remarks>
    private async Task ApplyEvaluationOrderAsync(
        Methodology methodology, int methodologyVersionId, DateOnly effectiveFrom, CancellationToken ct)
    {
        var formulas = await methodologies.GetFormulasAsync(methodologyVersionId, ct).ConfigureAwait(false);

        // ⛔ Імпорти розв'язуються НА ДАТУ набуття чинності, а не «сьогодні»:
        // версія бібліотеки вибирається тим самим правилом, що й будь-яка інша
        // (ФВ-9.3, поправка 10 директиви ПК-1 №05).
        var imports = await methodologies
            .ResolveImportsAsync(methodologyVersionId, effectiveFrom, ct)
            .ConfigureAwait(false);

        var problems = new List<string>();
        problems.AddRange(await LibraryProblemsAsync(methodology, methodologyVersionId, ct).ConfigureAwait(false));
        problems.AddRange(imports
            .Where(library => library.MethodologyVersionId is null)
            .Select(library =>
                $"Імпортована методологія «{library.MethodologyCode}» не має версії, чинної на "
                + $"{effectiveFrom:yyyy-MM-dd}: її формули невидимі."));

        if (formulas.Count == 0)
        {
            Reject(problems);

            // ⚠ Ребра прибираються і тут: версія без формул не посилається ні
            // на кого, а ті, що лишилися від попередньої редакції, тягли б
            // методологію в чергу перерахунку без жодної причини.
            await methodologies.ReplaceDependenciesAsync(methodology.Id, [], ct).ConfigureAwait(false);
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

        var byCode = formulas.ToDictionary(f => f.Code, f => f.Id, StringComparer.OrdinalIgnoreCase);
        var parsed = new List<ParsedFormula>(formulas.Count);
        var nodes = new List<FormulaNode>(formulas.Count);
        var dependencies = new HashSet<int>();

        foreach (var formula in formulas)
        {
            var resolution = Resolve(formula, byCode, imports, dependencies, problems);
            parsed.Add(new ParsedFormula(formula.Code, formula.ResultType, resolution.Root));

            nodes.Add(new FormulaNode(
                formula.Id,
                TableDefId: 0,
                FormulaScope.Column,
                ColumnDefId: null,
                RowDefId: null,
                resolution.Edges));
        }

        var constants = await methodologies
            .GetConstantsAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var outputs = await methodologies
            .GetOutputsAsync(methodologyVersionId, ct).ConfigureAwait(false);

        problems.AddRange(MethodologyPublishChecks.Check(parsed, constants, outputs));
        Reject(problems);

        var ordering = formulaEngine.BuildEvaluationOrder(nodes);
        if (!ordering.IsSuccess)
        {
            var byId = formulas.ToDictionary(f => f.Id, f => f.Code);

            throw new BusinessRuleException(
                "ECR-TMPL-4221",
                Ecr.Expressions.Graph.CycleDescription.Describe(
                    ordering.CyclePath,
                    id => byId.TryGetValue(id, out var code) ? code : id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
        }

        var position = 0;
        foreach (var formulaId in ordering.Order)
        {
            formulas.Single(f => f.Id == formulaId).SetEvaluationOrder(++position);
        }

        // ⛔ Ребра пишуться навіть коли їх нуль: заміна порожньою множиною
        // прибирає ті, що лишилися від попередньої редакції. Інакше граф
        // накопичує залежності, яких у виразах уже немає.
        await methodologies
            .ReplaceDependenciesAsync(methodology.Id, dependencies, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Відхиляє публікацію переліком проблем, якщо він непорожній.</summary>
    /// <remarks>
    /// ⚠ Перелік, а не перша помилка (02b §12): методолог, який виправляє їх по
    /// одній за прогін, робить це стільки разів, скільки їх є.
    /// </remarks>
    private static void Reject(List<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new BusinessRuleException("ECR-CALC-0422", MethodologyPublishChecks.Describe(problems));
        }
    }

    /// <summary>Проблеми, що випливають із природи методології (поправка 6).</summary>
    /// <remarks>
    /// ⛔ Бібліотека не рахує ні для кого: правило прив'язки на ній означає, що
    /// планувальник запускатиме її окремо, з порожнім набором аргументів, і
    /// щоночі писатиме або нулі, або помилку — залежно від формул.
    /// </remarks>
    private async Task<IEnumerable<string>> LibraryProblemsAsync(
        Methodology methodology, int methodologyVersionId, CancellationToken ct)
    {
        if (methodology.Kind != MethodologyKind.Library)
        {
            return [];
        }

        var rules = await methodologies.GetRulesAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return rules.Count == 0
            ? []
            : [$"Методологія «{methodology.Code}» оголошена бібліотекою, але має {rules.Count} "
               + "активних правил прив'язки: бібліотека не рахує ні для кого, на її формули посилаються."];
    }

    /// <summary>
    /// Один прохід над виразом формули: ребра до своїх формул, ребра до чужих
    /// методологій і корінь дерева для перевірки типів.
    /// </summary>
    /// <remarks>
    /// ⛔ Посилання <c>!Name</c> резолвиться СПЕРШУ у своїй версії, потім у
    /// оголошених імпортах (поправка 10 директиви ПК-1 №05). Перше дає ребро
    /// між формулами, друге — ребро між МЕТОДОЛОГІЯМИ: формули бібліотеки
    /// рахує своя методологія, і змішати їх у одному топологічному порядку
    /// означало б зіставляти ідентифікатори з різних нумерацій.
    ///
    /// ⛔ Ребра будуються з РОЗІБРАНОГО виразу, а не пошуком підрядка. Пошук
    /// підрядка — це пастка 4 директиви ПК-1 №05 §7, і вона була в нашому
    /// коді: `Expression.Contains("!" + code)` вважає, що `!k1_GasComp`
    /// містить посилання на `k1`. На чинному корпусі це 786 хибних ребер із
    /// 8474 — 9,3 %: 14 збігів серед коротких імен констант (`k1 ⊂ k10`,
    /// `a ⊂ a0`, `LHV ⊂ LHV0`, `Vch ⊂ Vchmax`) і 75 у родині складу газу
    /// (`CmHnP_GCV_C12_1 ⊂ mn4CmHnP_GCV_C12_1`).
    ///
    /// ⚠ Наслідок вади — і, отже, наслідок виправлення — стосується
    /// ІНВАЛІДАЦІЇ, а не підстановки значень. Зайве ребро не робить число
    /// неправильним: воно лише додає формулу в чергу на перерахунок. Чинна
    /// система рахує більше, ніж потрібно, а не рахує неправильно. Після цієї
    /// зміни перерахунок торкається МЕНШОЇ множини формул — це очікуваний
    /// результат, а не втрата.
    ///
    /// ⚠ Обхід AST не свій, а рушієвий (<c>H-3</c>): друге визначення того, від
    /// чого залежить формула, розійшлося б із першим, і розбіжність була б
    /// видима лише як порядок обчислення, що раптом став іншим.
    ///
    /// ⚠ Вираз, який не розбирається, ребер не дає: синтаксис — не предмет
    /// цього методу. Опублікуватися така версія однаково не зможе, бо
    /// порожній або червоний золотий набір публікацію не пропускає
    /// (<see cref="GoldenSet.IsGreen"/>).
    /// </remarks>
    private FormulaResolution Resolve(
        MethodologyFormula formula,
        Dictionary<string, int> byCode,
        IReadOnlyList<MethodologyLibrary> imports,
        HashSet<int> dependencies,
        List<string> problems)
    {
        var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
        if (parsed.Expression is null)
        {
            return new FormulaResolution([], null);
        }

        // ⚠ Знімок структури не передається: діалект методологій не має
        // посилань на комірки документа за побудовою — парсер відхиляє їх
        // окремою помилкою. Резолвити тут нічого.
        var extraction = formulaEngine.ExtractDependencies(
            parsed.Expression,
            snapshot: null,
            new DependencyContext(CurrentTableDefId: 0, CurrentRowKey: null, CurrentColumnDefId: null));

        var edges = new List<int>();

        foreach (var code in extraction.Dependencies.Select(d => d.FormulaCode))
        {
            if (code is null)
            {
                continue;
            }

            var reference = MethodologyReferenceResolver.Resolve(code, byCode, imports);

            switch (reference.Outcome)
            {
                // ⛔ Самопосилання ребром СТАЄ, і фільтра тут немає навмисно
                // (`I.21`). Він стояв тут і суперечив власному рушію:
                // `BuildEvaluationOrder` документував, що формула, залежна від
                // себе, — це цикл і публікація має його побачити (`ФВ-9.4`), а
                // саморебро до графа не доходило ніколи, тож обіцянка була
                // недосяжна.
                //
                // ⚠ Застереження знятого фільтра було правдиве — «цикл: X → X»
                // справді нічого не пояснює. Знято воно не відкиданням ребра, а
                // `CycleDescription`, який називає саме цей випадок словами.
                case MethodologyReferenceOutcome.Local:
                    edges.Add(reference.FormulaId!.Value);
                    break;

                // ⛔ Перехресне посилання дає ребро МІЖ МЕТОДОЛОГІЯМИ, а не між
                // формулами: формули бібліотеки не входять у топологічний
                // порядок цієї версії — вони рахуються своєю. Без цього ребра
                // порядок перерахунку неповний, і `HSE400` читає торішній
                // результат `Common` без жодної помилки в журналі.
                case MethodologyReferenceOutcome.Imported:
                    dependencies.Add(reference.MethodologyId!.Value);
                    break;

                // ⛔ Неоднозначність між двома бібліотеками — відмова, а не
                // «перший за списком»: інакше число залежало б від порядку
                // рядків у `calc.MethodologyImport`.
                // ⚠ TODO: потрібен окремий код `ECR-CALC-0435`.
                case MethodologyReferenceOutcome.Ambiguous:
                    problems.Add(
                        $"Формула «{formula.Code}»: посилання «!{code}» знайдено у "
                        + $"{reference.Candidates.Count} імпортах ({string.Join(", ", reference.Candidates)}).");
                    break;

                default:
                    break;
            }
        }

        return new FormulaResolution(edges, parsed.Expression.Root);
    }

    /// <summary>Що дав один прохід над виразом формули.</summary>
    /// <param name="Edges">Формули цієї версії, від яких залежить ця.</param>
    /// <param name="Root">Корінь дерева для перевірки типів; <c>null</c> — не розібралося.</param>
    private sealed record FormulaResolution(List<int> Edges, Ecr.Expressions.Ast.AstNode? Root);

    /// <summary>Чи зійшовся золотий набір у межах допуску.</summary>
    private async Task<bool> IsGreenAsync(
        Methodology methodology,
        MethodologyVersion version,
        IReadOnlyList<MethodologyTestCase> testCases,
        CancellationToken ct)
    {
        // ⛔ Порівняння тут БІЛЬШЕ НЕ ЖИВЕ. Воно винесене в `GoldenSet`, бо
        // тими самими очікуваннями має міряти і прогін без запису
        // (`ФВ-13.5`): доти той проганяв ті самі тести і жодного разу не
        // звіряв їх з очікуваннями, тож людина бачила числа, але не бачила,
        // зійшлися вони чи ні — а публікація відмовляла саме тому.
        var verdicts = new List<TestCaseVerdict>();

        foreach (var testCase in testCases)
        {
            var output = await module
                .ExecuteAsync(WithDescriptor(testCase.Input, Descriptor(methodology, version)), ct)
                .ConfigureAwait(false);

            verdicts.Add(GoldenSet.Judge(testCase, output));
        }

        return GoldenSet.IsGreen(verdicts);
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
