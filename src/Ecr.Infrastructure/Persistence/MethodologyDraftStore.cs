// src/Ecr.Infrastructure/Persistence/MethodologyDraftStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IMethodologyDraftStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class MethodologyDraftStore(EcrDbContext db) : IMethodologyDraftStore
{
    /// <summary>
    /// Стеля вибірки дочірніх записів версії.
    /// </summary>
    /// <remarks>
    /// Та сама межа, що й у <see cref="MethodologyStore"/>, і з тієї ж
    /// причини: помилка в даних без неї виглядала б як повільність.
    /// </remarks>
    private const int MaxChildren = 10_000;

    /// <inheritdoc />
    public async Task<Methodology?> FindByCodeAsync(string code, CancellationToken ct)
        => await db.Methodologies
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Code == code, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void AddMethodology(Methodology methodology) => db.Methodologies.Add(methodology);

    /// <inheritdoc />
    public async Task<Methodology?> FindAsync(int methodologyId, CancellationToken ct)
        => await db.Methodologies
            .Include(m => m.Versions)
            .FirstOrDefaultAsync(m => m.Id == methodologyId, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Фільтра за станом тут НЕМАЄ — і саме цим порт відрізняється від
    /// <see cref="MethodologyStore.GetPublishedVersionsAsync"/>. Конфігуратор
    /// показує те, що можна правити, а правити можна тільки чернетку.
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyVersion>> GetAllVersionsAsync(
        int methodologyId, CancellationToken ct)
        => await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.MethodologyId == methodologyId)
            .OrderBy(v => v.Version)
            .ThenBy(v => v.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Відстежувана навмисно: саме через цю версію домен дозволяє правку
    /// формул, і <c>AsNoTracking</c> зробив би зміну формули нікуди не
    /// збереженою.
    /// </remarks>
    public async Task<MethodologyVersion?> FindVersionAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyVersions
            .FirstOrDefaultAsync(v => v.Id == methodologyVersionId, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MethodologyFormula?> FindFormulaAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyFormulas
            .FirstOrDefaultAsync(
                f => f.MethodologyVersionId == methodologyVersionId && f.Code == code, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Шукається константа БЕЗ звуження — без категорії й речовини. Кандидатів
    /// на один код у версії буває кілька (ФВ-16.5), і «перша з кількох» означала
    /// б, що повторний запис базового значення мовчки переписує варіант,
    /// заведений для однієї установки.
    /// </remarks>
    public async Task<MethodologyConstant?> FindConstantAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyConstants
            .FirstOrDefaultAsync(
                c => c.MethodologyVersionId == methodologyVersionId
                     && c.Code == code
                     && c.Category == null
                     && c.SubstanceEntryId == null,
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MethodologyRule?> FindRuleAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyRules
            .FirstOrDefaultAsync(
                r => r.MethodologyVersionId == methodologyVersionId && r.Code == code, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MethodologyOutput?> FindOutputAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyOutputs
            .FirstOrDefaultAsync(
                o => o.MethodologyVersionId == methodologyVersionId && o.Code == code, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MethodologyTestCaseEntity?> FindTestCaseAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyTestCases
            .FirstOrDefaultAsync(
                t => t.MethodologyVersionId == methodologyVersionId && t.Code == code, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Без фільтра <c>IsActive</c> — на відміну від
    /// <see cref="MethodologyStore.GetRulesAsync"/>. Той обслуговує ПРОГІН і
    /// показує лише те, чим зіставляють; конфігуратор показує те, що правлять, а
    /// вимкнене правило, невидиме в редакторі, неможливо ні ввімкнути, ні
    /// назвати причиною порожнього розрахунку.
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyRule>> GetAllRulesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyRules
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == methodologyVersionId)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyTestCaseEntity>> GetTestCaseEntitiesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyTestCases
            .AsNoTracking()
            .Where(t => t.MethodologyVersionId == methodologyVersionId)
            .OrderBy(t => t.Code)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(MethodologyFormula formula) => db.MethodologyFormulas.Add(formula);

    /// <inheritdoc />
    public void Add(MethodologyConstant constant) => db.MethodologyConstants.Add(constant);

    /// <inheritdoc />
    public void Add(MethodologyRule rule) => db.MethodologyRules.Add(rule);

    /// <inheritdoc />
    public void Add(MethodologyOutput output) => db.MethodologyOutputs.Add(output);

    /// <inheritdoc />
    public void Add(MethodologyTestCaseEntity testCase) => db.MethodologyTestCases.Add(testCase);

    /// <inheritdoc />
    public void Remove(MethodologyFormula formula) => db.MethodologyFormulas.Remove(formula);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Два <c>SaveChanges</c>, а не один, і це не недогляд: дочірні записи
    /// посилаються на версію числом, тож їхній зовнішній ключ можна проставити
    /// лише після того, як база призначила ключ чернетці. Так само влаштований
    /// <see cref="TemplateVersionStore.CloneAsync"/>.
    /// </remarks>
    public async Task<int> SaveDraftAsync(
        MethodologyVersion draft, int? copyFromVersionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        db.MethodologyVersions.Add(draft);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (copyFromVersionId is { } sourceId)
        {
            await CopyChildrenAsync(sourceId, draft.Id, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return draft.Id;
    }

    /// <summary>
    /// Переносить у чернетку **кожен** набір дочірніх записів версії-джерела.
    /// </summary>
    /// <param name="sourceVersionId">Версія-джерело.</param>
    /// <param name="targetVersionId">Чернетка, яка щойно дістала ключ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Перелік наборів має бути **повним**, і забутий набір не має симптому:
    /// клон без констант рахує тими самими виразами по порожніх коефіцієнтах,
    /// клон без тестів неможливо опублікувати взагалі (ФВ-9.12), клон без
    /// імпортів мовчки втрачає доступ до чужих формул. За повнотою стежить
    /// архітектурний сторож
    /// <c>Клон_версії_методології_переносить_кожен_набір_дочірніх_записів</c>:
    /// він звіряє цей метод із переліком сутностей, що мають
    /// <c>MethodologyVersionId</c>.
    ///
    /// ⚠ <c>calc.MethodologyDependency</c> сюди не входить і входити не може:
    /// ребро графа належить МЕТОДОЛОГІЇ, а не версії, і перебудовує його
    /// публікація за фактичними посиланнями виразів.
    ///
    /// ⚠ <c>AsNoTracking</c> на кожному читанні: джерело зараз розмножать у
    /// нові сутності, і відстежуваний оригінал EF сприйняв би за зміну — тобто
    /// клонування псувало б те, з чого клонує.
    /// </remarks>
    private async Task CopyChildrenAsync(int sourceVersionId, int targetVersionId, CancellationToken ct)
    {
        var ownerMethodologyId = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == targetVersionId)
            .Select(v => v.MethodologyId)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        foreach (var source in await ChildrenAsync(
                     db.MethodologyFormulas.Where(f => f.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            var clone = new MethodologyFormula(
                targetVersionId, EcrCode.Create(source.Code), source.Expression);

            // ⛔ Тип ставиться до одиниці: `SetOutputUnit` відхиляє одиницю на
            // текстовому результаті, а типом за замовчуванням є число.
            clone.SetResultType(source.ResultType);

            if (source.OutputUnitId is { } unit)
            {
                clone.SetOutputUnit(unit);
            }

            // ⛔ Оголошений список аргументів переноситься теж, і <c>null</c>
            // переноситься як <c>null</c>. Клон без нього означав би, що звірка
            // пастки 2 (`ECR-CALC-0432`) мовчить у КОЖНІЙ новій редакції: список
            // є в опублікованій версії й зникає в тій, яку зараз правлять, —
            // тобто саме там, де описку в імені токена ще можна виправити.
            clone.SetArguments(source.ArgumentsCsv);

            // ⚠ `EvaluationOrder` НЕ переноситься: він топологічний і
            // рахується при публікації (ФВ-9.4). Скопійований, він виглядав би
            // як уже порахований для складу формул, якого ще ніхто не перевіряв.
            db.MethodologyFormulas.Add(clone);
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologyConstants.Where(c => c.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            db.MethodologyConstants.Add(CopyConstant(source, targetVersionId));
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologyRules.Where(r => r.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            var clone = new MethodologyRule(
                targetVersionId, EcrCode.Create(source.Code), source.MatchJson, source.Priority);

            // ⛔ Вимкнене правило лишається вимкненим. Конструктор вмикає
            // правило, і без цього рядка клон почав би зіставляти рядки, які
            // джерело свідомо не рахувало (ФВ-13.4).
            clone.SetActive(source.IsActive);

            db.MethodologyRules.Add(clone);
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologyImports.Where(i => i.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            db.MethodologyImports.Add(new MethodologyImport(
                targetVersionId, source.ImportedMethodologyId, ownerMethodologyId));
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologySubstances.Where(s => s.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            db.MethodologySubstances.Add(new MethodologySubstance(
                targetVersionId, source.SubstanceEntryId, source.Ordinal));
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologyOutputs.Where(o => o.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            db.MethodologyOutputs.Add(new MethodologyOutput(
                targetVersionId, EcrCode.Create(source.Code), source.UnitId, source.Ordinal));
        }

        foreach (var source in await ChildrenAsync(
                     db.MethodologyTestCases.Where(t => t.MethodologyVersionId == sourceVersionId), ct)
                     .ConfigureAwait(false))
        {
            db.MethodologyTestCases.Add(new MethodologyTestCaseEntity(
                targetVersionId, source.Code, source.InputJson, source.ExpectedJson, source.Tolerance));
        }
    }

    /// <summary>Читає дочірні записи версії-джерела.</summary>
    /// <typeparam name="TChild">Тип дочірньої сутності.</typeparam>
    /// <param name="source">Запит, уже звужений до версії-джерела.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записи джерела.</returns>
    /// <remarks>
    /// ⚠ Звуження робить ВИКЛИК, а не помічник. Спільний інтерфейс «дочірній
    /// запис версії» довелося б оголосити в домені заради читання в
    /// інфраструктурі — тобто вписати в модель предметної області подробицю
    /// однієї вибірки.
    /// </remarks>
    private static async Task<List<TChild>> ChildrenAsync<TChild>(
        IQueryable<TChild> source, CancellationToken ct)
        where TChild : class
        => await source
            .AsNoTracking()
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Копіює константу, не втрачаючи ні виду, ні нерозібраного рядка.</summary>
    /// <param name="source">Константа джерела.</param>
    /// <param name="targetVersionId">Чернетка.</param>
    /// <returns>Копію константи.</returns>
    /// <remarks>
    /// ⛔ Розібране число копіюється **числом**, а не через рядок. Дорога
    /// «decimal → текст → decimal» виглядає рівноцінною і не є нею: значення
    /// коефіцієнта емісії — те, за що система відповідає перед регулятором, і
    /// проводити його через розбір заради однаковості коду означало б поставити
    /// числа звіту в залежність від форматування.
    ///
    /// ⚠ Нерозібраний рядок при <c>Kind = Numeric</c> (<c>'-'</c>, <c>''</c>)
    /// переноситься як є: він і в джерелі лишався таким, щоб публікація його
    /// назвала, а не щоб хтось підставив нуль.
    /// </remarks>
    private static MethodologyConstant CopyConstant(MethodologyConstant source, int targetVersionId)
    {
        var code = EcrCode.Create(source.Code);

        var clone = source.Kind == ConstantKind.Numeric
            ? Numeric(source, targetVersionId, code)
            : MethodologyConstant.OfText(
                targetVersionId, code, source.TextValue ?? string.Empty, source.Kind);

        clone.SetScope(source.Category, source.SubstanceEntryId);
        clone.SetValidity(source.ValidFrom, source.ValidTo);
        clone.SetSource(source.Source);

        return clone;
    }

    /// <summary>Копія числової константи — розібраної або ні.</summary>
    /// <param name="source">Константа джерела.</param>
    /// <param name="targetVersionId">Чернетка.</param>
    /// <param name="code">Код константи.</param>
    /// <returns>Копію.</returns>
    private static MethodologyConstant Numeric(
        MethodologyConstant source, int targetVersionId, EcrCode code)
        => source is { Value: { } value, UnitId: { } unit }
            ? new MethodologyConstant(targetVersionId, code, value, unit)
            : MethodologyConstant.FromImport(
                targetVersionId, code, source.TextValue, ConstantKind.Numeric, source.UnitId);
}

