// src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Заведення методології **з нуля**, а не клонуванням наявної (директива №09,
/// <c>W6</c>).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>Methodology</c> не створював НІЩО в <c>src/</c>: конструктор
/// викликали лише тести і офлайновий <c>Ecr.DataGen</c>. Наслідок був не
/// «незручно», а «неможливо»: <c>POST …/{id}/versions</c> вимагає
/// ідентифікатора методології, брати який не було звідки, тож увесь
/// конфігуратор версій був досяжний лише для того, що завіз генератор.
///
/// ⛔ <see cref="MethodologyKind"/> задається ПРИ СТВОРЕННІ і разом із кодом.
/// Поле несуче: <c>Bespoke</c>-методології правила прив'язки не мають у
/// принципі, а <c>Library</c> не рахує ні для кого — і саме на цьому тримається
/// перевірка публікації <c>LibraryProblemsAsync</c>, яка доти була мертвою за
/// відсутністю живих методологій.
/// </remarks>
public sealed class CreateMethodologyHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на заведення методології (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ Те саме право, що й на чернетку версії, а не <c>Calculation.Publish</c>:
    /// порожня методологія без опублікованої версії не рахує нічого і нічого не
    /// змінює в поданих числах. Вимагати для неї небезпечного права означало б,
    /// що методолог не може почати роботу без старшого методолога.
    /// </remarks>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Заводить методологію-контейнер.</summary>
    /// <param name="code">Код, унікальний у системі.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="kind">Природа методології.</param>
    /// <param name="group">Група в переліку; <c>null</c> — поза групами.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Створену методологію — без жодної версії.</returns>
    /// <exception cref="BusinessRuleException">Код уже зайнято.</exception>
    public async Task<MethodologySummaryDto> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        MethodologyKind kind,
        string? group,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var methodologyCode = EcrCode.Create(code);

        // ⛔ Унікальність коду перевіряється ТУТ, бо унікального індексу на
        // `calc.Methodology.Code` у схемі немає. Два однакові коди роблять
        // неоднозначним `!Name` через `MethodologyImport`: `MethodologyReferenceResolver`
        // віддав би `Ambiguous`, і публікація ЧУЖОЇ методології відмовлялася б
        // із причиною, яка не вказує на справжнє джерело.
        var clash = await drafts.FindByCodeAsync(methodologyCode.Value, ct).ConfigureAwait(false);
        if (clash is not null)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Методологія «{methodologyCode.Value}» уже існує (ідентифікатор {clash.Id}): "
                + "код — те, чим на неї посилаються імпорти і прив'язки.");
        }

        var methodology = new Methodology(
            methodologyCode,
            new LocalizedText(new Dictionary<string, string>(name, StringComparer.OrdinalIgnoreCase)));

        methodology.SetKind(kind);
        methodology.SetGroup(group);

        drafts.AddMethodology(methodology);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return Map(methodology);
    }

    /// <summary>Складає DTO методології-контейнера.</summary>
    /// <param name="methodology">Методологія.</param>
    /// <returns>Методологія для конфігуратора.</returns>
    public static MethodologySummaryDto Map(Methodology methodology)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        return new MethodologySummaryDto(
            methodology.Id,
            methodology.Code,
            methodology.NameL10n.Values,
            methodology.Kind,
            methodology.Group,
            methodology.IsActive);
    }
}

/// <summary>Константи версії методології (ФВ-16.1).</summary>
public sealed class ListMethodologyConstantsHandler(
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає константи версії — усі, включно з мітками категорій.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Константи в порядку коду.</returns>
    public async Task<IReadOnlyList<MethodologyConstantDto>> HandleAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var constants = await methodologies
            .GetConstantsAsync(methodologyVersionId, ct)
            .ConfigureAwait(false);

        return [.. constants.Select(MethodologyAuthoringMap.Constant)];
    }
}

/// <summary>
/// Заводить або змінює константу **чернетки** (ФВ-16.1, поправка 2-біс).
/// </summary>
/// <remarks>
/// ⛔ Доти константи потрапляли у версію рівно одним шляхом — копіюванням із
/// іншої версії при клонуванні. Первісну завести не було чим, тож методологія,
/// створена з нуля, рахувала виразами по порожніх коефіцієнтах: публікація її
/// не спиняла, бо зелений тест перевіряє результат, а результат теж
/// рахувався — просто інший.
///
/// ⚠ <c>PUT</c> за КОДОМ, як і формула: код — те, що стоїть у виразі після
/// <c>CST.</c>, і задає його викликач, тож створення й зміна — одна дія.
/// </remarks>
public sealed class SaveMethodologyConstantHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування констант (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ Саме <c>EditConstant</c>, а не <c>EditFormula</c>. Це різні люди:
    /// коефіцієнт емісії заводить той, хто відповідає за паспорт установки, а
    /// вираз — той, хто відповідає за методику. Право існувало в каталозі
    /// (`09-seed.sql`) і не перевірялося ніде — бо жодна дія його не вимагала.
    /// </remarks>
    public const string Permission = "Calculation.EditConstant";

    /// <summary>Записує константу; створює її, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="request">Значення, одиниця, вид, вікно чинності, джерело.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записану константу.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія опублікована, число без одиниці або текст порожній.
    /// </exception>
    public async Task<MethodologyConstantDto> HandleAsync(
        int methodologyVersionId,
        string code,
        SaveMethodologyConstant request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var constantCode = EcrCode.Create(code);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var existing = await drafts
            .FindConstantAsync(methodologyVersionId, constantCode.Value, ct)
            .ConfigureAwait(false);

        MethodologyConstant constant;

        if (existing is null)
        {
            constant = request.Kind == ConstantKind.Numeric
                ? version.AddNumericConstant(constantCode, Number(request), Unit(request))
                : version.AddTextConstant(constantCode, request.TextValue ?? string.Empty, request.Kind);

            drafts.Add(constant);
        }
        else
        {
            version.EditConstant(
                existing, request.Value, request.UnitId, request.TextValue, request.Kind);
            constant = existing;
        }

        // ⚠ Звуження і вікно чинності ставляться ЗАВЖДИ, зокрема в `null`:
        // повторний `PUT` без категорії має прибрати ту, що стояла, — інакше
        // «зняти звуження» неможливо взагалі, і константа лишається чинною
        // лише для однієї установки без жодного сліду в запиті.
        constant.SetScope(request.Category, request.SubstanceEntryId);
        constant.SetValidity(request.ValidFrom, request.ValidTo);
        constant.SetSource(request.Source);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyAuthoringMap.Constant(constant);
    }

    /// <summary>Число з запиту або відмова з названою причиною.</summary>
    /// <param name="request">Запит на запис константи.</param>
    /// <returns>Значення числової константи.</returns>
    /// <exception cref="BusinessRuleException">Число не подано.</exception>
    private static decimal Number(SaveMethodologyConstant request)
        => request.Value
           ?? throw new BusinessRuleException(
               "ECR-CALC-0422",
               "Числова константа без значення: порожнє число — не «нуль за замовчуванням», "
               + "а рішення, якого ніхто не ухвалив (поправка 2-біс директиви ПК-1 №05).");

    /// <summary>Одиниця з запиту або відмова з названою причиною.</summary>
    /// <param name="request">Запит на запис константи.</param>
    /// <returns>Одиницю числової константи.</returns>
    /// <exception cref="BusinessRuleException">Одиниця не подана.</exception>
    private static int Unit(SaveMethodologyConstant request)
        => request.UnitId
           ?? throw new BusinessRuleException(
               "ECR-CALC-0422",
               "Числова константа без одиниці: перевірка розмірностей без неї неможлива (ФВ-16.1).");
}

/// <summary>Правила відбору рядків версії (ФВ-13.3).</summary>
public sealed class ListMethodologyRulesHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає правила версії — включно з вимкненими.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Правила в порядку пріоритету.</returns>
    public async Task<IReadOnlyList<MethodologyRuleDto>> HandleAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var rules = await drafts.GetAllRulesAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return [.. rules.Select(MethodologyAuthoringMap.Rule)];
    }
}

/// <summary>
/// Заводить або змінює правило відбору рядків **чернетки** (ФВ-13.3, ФВ-13.4).
/// </summary>
/// <remarks>
/// ⛔ Без жодного правила <c>MethodologyResolver.MatchRowsAsync</c> повертає
/// порожній перелік, і методологія не рахує ЖОДНОГО рядка документа —
/// перерахунок при цьому завершується успіхом. Правило й прив'язка
/// (<c>cfg.CalculationBinding</c>) — різні речі й обидві обов'язкові: прив'язка
/// каже, ЯКА таблиця й куди лягає число, правило — ЯКІ її рядки рахувати.
/// </remarks>
public sealed class SaveMethodologyRuleHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування правил (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditRule";

    /// <summary>Записує правило; створює його, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код правила.</param>
    /// <param name="matchJson">Структурований предикат; <c>{}</c> — уся таблиця.</param>
    /// <param name="priority">Пріоритет; менше значення — вищий.</param>
    /// <param name="isActive">Чи бере правило участь у зіставленні.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записане правило.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія опублікована або предикат порожній.</exception>
    public async Task<MethodologyRuleDto> HandleAsync(
        int methodologyVersionId,
        string code,
        string matchJson,
        int priority,
        bool isActive,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var ruleCode = EcrCode.Create(code);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var existing = await drafts
            .FindRuleAsync(methodologyVersionId, ruleCode.Value, ct)
            .ConfigureAwait(false);

        MethodologyRule rule;

        if (existing is null)
        {
            rule = version.AddRule(ruleCode, matchJson, priority);
            rule.SetActive(isActive);
            drafts.Add(rule);
        }
        else
        {
            version.EditRule(existing, matchJson, priority, isActive);
            rule = existing;
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyAuthoringMap.Rule(rule);
    }
}

/// <summary>Оголошені виходи версії (ФВ-16.6).</summary>
public sealed class ListMethodologyOutputsHandler(
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає виходи версії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Виходи в порядку <c>Ordinal</c>.</returns>
    public async Task<IReadOnlyList<MethodologyOutputDto>> HandleAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var outputs = await methodologies
            .GetOutputsAsync(methodologyVersionId, ct)
            .ConfigureAwait(false);

        return [.. outputs.Select(MethodologyAuthoringMap.Output)];
    }
}

/// <summary>
/// Оголошує вихід версії — те, що методологія віддає назовні (ФВ-16.6).
/// </summary>
/// <remarks>
/// ⛔ Без жодного виходу <c>GenericCalculationModule</c> рахує всі формули і не
/// записує НІЧОГО: цикл запису йде по <c>MethodologyOutput</c>, а не по
/// формулах. Методологія з правильними виразами при цьому дає порожній
/// результат, і жодна перевірка публікації цього не називає — вона перевіряє
/// вирази, а не те, чи оголошено бодай один вихід.
///
/// ⚠ Одиниця обов'язкова (<c>MethodologyOutput</c> її не має нульовною): на ній
/// тримається перевірка розмірностей при публікації.
/// </remarks>
public sealed class SaveMethodologyOutputHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ <c>EditFormula</c>, а не <c>EditConstant</c>: вихід зв'язується з
    /// формулою за кодом і є частиною того, ЩО методологія обчислює, а не тим,
    /// ЯКИМИ числами вона це робить.
    /// </remarks>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Записує вихід; створює його, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код виходу.</param>
    /// <param name="unitId">Одиниця результату.</param>
    /// <param name="ordinal">Порядок у переліку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записаний вихід.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія опублікована.</exception>
    public async Task<MethodologyOutputDto> HandleAsync(
        int methodologyVersionId,
        string code,
        int unitId,
        int ordinal,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var outputCode = EcrCode.Create(code);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var existing = await drafts
            .FindOutputAsync(methodologyVersionId, outputCode.Value, ct)
            .ConfigureAwait(false);

        MethodologyOutput output;

        if (existing is null)
        {
            output = version.AddOutput(outputCode, unitId, ordinal);
            drafts.Add(output);
        }
        else
        {
            version.EditOutput(existing, unitId, ordinal);
            output = existing;
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyAuthoringMap.Output(output);
    }
}

/// <summary>Золотий набір версії (ФВ-13.7).</summary>
public sealed class ListMethodologyTestCasesHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає тести версії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Тести в порядку коду.</returns>
    public async Task<IReadOnlyList<MethodologyTestCaseDto>> HandleAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var tests = await drafts
            .GetTestCaseEntitiesAsync(methodologyVersionId, ct)
            .ConfigureAwait(false);

        return [.. tests.Select(MethodologyAuthoringMap.TestCase)];
    }
}

/// <summary>
/// Заводить або змінює тест золотого набору **чернетки** (ФВ-13.7, ФВ-9.12).
/// </summary>
/// <remarks>
/// ⛔ Це не «тести заради тестів», а умова публікації: порожній набір НЕ
/// зелений (<c>GoldenSet.IsGreen</c>), і <c>MethodologyVersion.Publish</c>
/// відхиляє версію без нього (<c>ECR-CALC-0422</c>). Доки набір не було чим
/// заповнити, опублікувати заведену з нуля методологію було неможливо в
/// принципі — правило працювало, і користі з нього не було жодної.
///
/// ⚠ Вхід і очікування приходять JSON-ом і зберігаються як є: форма входу — це
/// <c>CalculationInput</c>, тип застосунку, і розбирати його тут означало б
/// завести другу правду про те, що таке вхід розрахунку. Зіпсований JSON робить
/// тест ЧЕРВОНИМ при публікації (<c>MethodologyStore.GetTestCasesAsync</c>), а
/// не мовчки відсутнім.
/// </remarks>
public sealed class SaveMethodologyTestCaseHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Записує тест; створює його, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код тесту.</param>
    /// <param name="inputJson">Вхід прогону.</param>
    /// <param name="expectedJson">Очікувані виходи.</param>
    /// <param name="tolerance">Допуск; нуль — точна рівність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записаний тест.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія опублікована.</exception>
    public async Task<MethodologyTestCaseDto> HandleAsync(
        int methodologyVersionId,
        string code,
        string inputJson,
        string expectedJson,
        decimal tolerance,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var testCode = EcrCode.Create(code);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var existing = await drafts
            .FindTestCaseAsync(methodologyVersionId, testCode.Value, ct)
            .ConfigureAwait(false);

        MethodologyTestCaseEntity testCase;

        if (existing is null)
        {
            testCase = version.AddTestCase(testCode.Value, inputJson, expectedJson, tolerance);
            drafts.Add(testCase);
        }
        else
        {
            version.EditTestCase(existing, inputJson, expectedJson, tolerance);
            testCase = existing;
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyAuthoringMap.TestCase(testCase);
    }
}

/// <summary>
/// Задає режими обчислення версії-чернетки (ФВ-9.9, ФВ-16.11, ФВ-9.13).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>MethodologyVersion.SetModes</c> мав рівно одного викликача —
/// <c>CloneAsDraft</c>, який ПЕРЕНОСИТЬ режими джерела. Тобто змінити їх було
/// неможливо в принципі: конструктор ставить <c>Legacy</c> і <c>Actual</c>,
/// клон переносить, а третього шляху не існувало. <c>NumericMode.Strict</c>
/// був недосяжним станом — при тому, що саме він вимкнено маскує ділення на
/// нуль у <c>null</c> замість тихого нуля (ФВ-9.14).
///
/// ⛔ Дія окрема від створення версії, а не поле в ньому. Режими змінюють УСІ
/// числа версії, не змінивши жодної формули; окрема дія робить цю зміну
/// подією, яку видно в журналі й у diff публікації (обидва режими там
/// обов'язкові — <c>D-78</c>).
/// </remarks>
public sealed class SetMethodologyModesHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Записує режими чернетки.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="numericMode">Арифметичний режим.</param>
    /// <param name="calendarMode">Календарна конвенція.</param>
    /// <param name="traceLevel">Обсяг журналу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версію з новими режимами.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія опублікована.</exception>
    public async Task<MethodologyDraftVersionDto> HandleAsync(
        int methodologyVersionId,
        NumericMode numericMode,
        CalendarMode calendarMode,
        TraceLevel traceLevel,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        // ⛔ Заборону «опублікована незмінна» тримає домен, а не ця дія:
        // `SetModes` сам вимагає чернетки (`ECR-CALC-0409`).
        version.SetModes(numericMode, calendarMode, traceLevel);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return ListMethodologyVersionsHandler.Map(version);
    }
}

/// <summary>Прив'язки методології до колонок документів (<c>D-69</c>).</summary>
public sealed class ListCalculationBindingsHandler(
    ICalculationBindingStore bindings,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає прив'язки методології.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки, включно з вимкненими.</returns>
    public async Task<IReadOnlyList<CalculationBindingDto>> HandleAsync(
        int methodologyId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var found = await bindings.ListAsync(methodologyId, ct).ConfigureAwait(false);

        return [.. found.Select(MethodologyAuthoringMap.Binding)];
    }
}

/// <summary>
/// Прив'язує вихід методології до колонки документа — **головний блокер
/// розрахунку** (директива №09, <c>W6</c> §3).
/// </summary>
/// <remarks>
/// ⛔ <c>cfg.CalculationBinding</c> не створювало НІЩО: ні обробник, ні
/// контролер, ні навіть тест. Наслідок мовчазний і повний:
/// <c>RecalculationJob.BindingsAsync</c> віддавав порожній перелік,
/// <c>CalculationOrchestrator</c> одразу повертав порожній профіль, задача
/// завершувалася <c>Succeeded</c> — і не рахувала нічого. Ані стан задачі, ані
/// журнал про це не казали: порожній набір прив'язок не є помилкою.
///
/// ⛔ Адреса прив'язки — трійка <c>(ColumnDefId, MethodologyId, OutputCode)</c>
/// (<c>UQ_CalculationBinding</c>), тому дія одна: <c>PUT</c> створює і змінює.
/// <c>TableDefId</c> у запиті НЕМАЄ — він виводиться з колонки: два поля, що
/// описують те саме, розходяться мовчки, а прив'язка з чужою таблицею просто не
/// спрацьовує.
/// </remarks>
public sealed class SaveCalculationBindingHandler(
    ICalculationBindingStore bindings,
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування правил прив'язки (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ <c>EditRule</c>, а не <c>EditFormula</c>: прив'язка не змінює жодного
    /// виразу — вона вирішує, ДЕ і ДЛЯ ЧОГО методологію запускають. Це те саме
    /// рішення, що й правило відбору рядків, лише з боку шаблону.
    /// </remarks>
    public const string Permission = "Calculation.EditRule";

    /// <summary>Записує прив'язку; створює її, якщо такої трійки ще немає.</summary>
    /// <param name="methodologyId">Методологія-джерело.</param>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="outputCode">Який вихід методології лягає в колонку.</param>
    /// <param name="matchJson">Як звузити рядки таблиці; <c>{}</c> — усі.</param>
    /// <param name="isActive">Чи бере прив'язка участь у прогоні.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записану прив'язку.</returns>
    /// <exception cref="NotFoundException">Методології або колонки немає.</exception>
    /// <exception cref="BusinessRuleException">Порожній предикат.</exception>
    public async Task<CalculationBindingDto> HandleAsync(
        int methodologyId,
        int columnDefId,
        string outputCode,
        string matchJson,
        bool isActive,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var code = EcrCode.Create(outputCode);

        _ = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Методології {methodologyId} не існує.");

        var tableDefId = await bindings.FindTableOfColumnAsync(columnDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Колонки {columnDefId} не існує або її видалено: прив'язати вихід нема до чого.");

        var existing = await bindings
            .FindAsync(columnDefId, methodologyId, code.Value, ct)
            .ConfigureAwait(false);

        CalculationBinding binding;

        binding = existing
                  ?? new CalculationBinding(tableDefId, columnDefId, methodologyId, code.Value, matchJson);

        // ⚠ Виклик і для нової прив'язки теж: конструктор вмикає її беззастережно
        // і предиката не перевіряє, а вимкнена одразу — законний стан (прив'язку
        // заводять наперед, поки методологію ще правлять). Порожній предикат
        // відхиляє домен звідси, а не форма.
        binding.Update(matchJson, isActive);

        if (existing is null)
        {
            bindings.Add(binding);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyAuthoringMap.Binding(binding);
    }
}

/// <summary>Складання DTO конфігуратора методологій — в одному місці.</summary>
/// <remarks>
/// ⚠ Проєкції зібрані тут, бо ті самі сутності віддають і читання, і запис:
/// повторена в обох місцях проєкція розходиться на першому ж доданому полі, і
/// клієнт бачить різні форми того самого об'єкта залежно від того, звідки він
/// прийшов.
/// </remarks>
public static class MethodologyAuthoringMap
{
    /// <summary>Складає DTO константи.</summary>
    /// <param name="constant">Константа версії.</param>
    /// <returns>Константа для конфігуратора.</returns>
    public static MethodologyConstantDto Constant(MethodologyConstant constant)
    {
        ArgumentNullException.ThrowIfNull(constant);

        return new MethodologyConstantDto(
            constant.Id,
            constant.Code,
            constant.Kind,
            constant.Value,
            constant.TextValue,
            constant.UnitId,
            constant.ValidFrom,
            constant.ValidTo,
            constant.Category,
            constant.Source,
            constant.IsResolved);
    }

    /// <summary>Складає DTO правила.</summary>
    /// <param name="rule">Правило версії.</param>
    /// <returns>Правило для конфігуратора.</returns>
    public static MethodologyRuleDto Rule(MethodologyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new MethodologyRuleDto(rule.Id, rule.Code, rule.MatchJson, rule.Priority, rule.IsActive);
    }

    /// <summary>Складає DTO виходу.</summary>
    /// <param name="output">Вихід версії.</param>
    /// <returns>Вихід для конфігуратора.</returns>
    public static MethodologyOutputDto Output(MethodologyOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return new MethodologyOutputDto(output.Id, output.Code, output.UnitId, output.Ordinal);
    }

    /// <summary>Складає DTO тесту золотого набору.</summary>
    /// <param name="testCase">Тест версії.</param>
    /// <returns>Тест для конфігуратора.</returns>
    public static MethodologyTestCaseDto TestCase(MethodologyTestCaseEntity testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        return new MethodologyTestCaseDto(
            testCase.Id, testCase.Code, testCase.InputJson, testCase.ExpectedJson, testCase.Tolerance);
    }

    /// <summary>Складає DTO прив'язки.</summary>
    /// <param name="binding">Прив'язка методології до колонки.</param>
    /// <returns>Прив'язка для конфігуратора.</returns>
    public static CalculationBindingDto Binding(CalculationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new CalculationBindingDto(
            binding.Id,
            binding.TableDefId,
            binding.ColumnDefId,
            binding.MethodologyId,
            binding.OutputCode,
            binding.MatchJson,
            binding.IsActive);
    }
}

/// <summary>
/// Значення константи, яке записує <see cref="SaveMethodologyConstantHandler"/>.
/// </summary>
/// <remarks>
/// ⛔ Один тип на число і на текст, а не два обробники. Вид константи —
/// властивість, яка МІНЯЄТЬСЯ (<c>'-'</c>, яке методолог виправляє на число, —
/// один із трьох відомих дефектів корпусу), і два маршрути означали б, що
/// виправлення робиться видаленням і створенням, тобто зміною <c>Id</c>, на
/// який посилається аудит.
/// </remarks>
/// <param name="Kind">Число, текст або мітка категорії.</param>
/// <param name="Value">Число; обов'язкове для <see cref="ConstantKind.Numeric"/>.</param>
/// <param name="UnitId">Одиниця; обов'язкова для числа, знімається для тексту (ФВ-16.1).</param>
/// <param name="TextValue">Текст; обов'язковий для нечислових видів.</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> — від початку.</param>
/// <param name="ValidTo">
/// Перший НЕчинний день, **виключно** (директива ПК-1 №05 §7, пастка 5):
/// коефіцієнт, чинний увесь 2024 рік, має тут <c>2025-01-01</c>.
/// </param>
/// <param name="Category">Категорія звуження; <c>null</c> — константа спільна.</param>
/// <param name="SubstanceEntryId">Речовина звуження; <c>null</c> — спільна.</param>
/// <param name="Source">Звідки взято значення: наказ, паспорт установки, вимірювання.</param>
public sealed record SaveMethodologyConstant(
    ConstantKind Kind,
    decimal? Value,
    int? UnitId,
    string? TextValue,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Category,
    long? SubstanceEntryId,
    string? Source);
