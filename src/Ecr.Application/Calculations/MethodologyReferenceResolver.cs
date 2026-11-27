// src/Ecr.Application/Calculations/MethodologyReferenceResolver.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>
/// Резолвінг <c>!Name</c> **через межу методології** (директива ПК-1 №05,
/// поправка 10).
/// </summary>
/// <remarks>
/// ⛔ Методологія не замкнена, і це не рідкісний випадок: 149 посилань із
/// <c>HSE400</c> і 116 з <c>Flert</c> ведуть у <c>Common</c>, а
/// <c>ECW_C09_02_01</c> — одна формула — потрібна п'яти методологіям. Правило
/// «<c>!</c> — це формула ТІЄЇ САМОЇ версії» (02b §3.4) відхилило б усі 265 як
/// нерезолвлені посилання.
/// <para>
/// ⚠ <c>Kind = Library</c> тут не перевіряється навмисно. Спільність — це
/// оголошений імпорт, а не природа методології; ворота «посилатися можна лише
/// на бібліотеку» заборонили б наявні посилання на <c>ECW_C09_02_01</c>.
/// </para>
/// </remarks>
public static class MethodologyReferenceResolver
{
    /// <summary>
    /// Знаходить, куди веде <c>!Name</c>: спершу у власній версії, потім в
    /// оголошених імпортах.
    /// </summary>
    /// <param name="name">Ім'я після <c>!</c>.</param>
    /// <param name="localFormulas">Формули цієї версії: код → ідентифікатор.</param>
    /// <param name="imports">Оголошені імпорти, розв'язані на дату періоду.</param>
    /// <returns>Куди веде посилання, або чому не веде нікуди.</returns>
    /// <exception cref="ArgumentNullException">Будь-який аргумент — <c>null</c>.</exception>
    /// <remarks>
    /// ⛔ Своє виграє над імпортованим, і це **не** неоднозначність: формула з
    /// тим самим кодом у власній версії — навмисне перекриття бібліотечної, і
    /// саме так методологія відходить від спільної поведінки, не форкаючи
    /// <c>Common</c>.
    ///
    /// ⛔ А от збіг МІЖ ДВОМА імпортами — помилка публікації. Узяти «перший за
    /// списком» означало б, що число залежить від порядку рядків у
    /// <c>calc.MethodologyImport</c> і змінюється від переіндексації; саме тому
    /// в оголошення імпорту не додано порядку.
    ///
    /// ⚠ Порівняння без урахування регістру — узгоджено з рештою резолвінгу
    /// символів у публікації (<c>PublishMethodologyHandler</c>): два різні
    /// правила для <c>!Name</c> розійшлися б на першому ж імені у змішаному
    /// регістрі.
    /// </remarks>
    public static MethodologyReference Resolve(
        string name,
        IReadOnlyDictionary<string, int> localFormulas,
        IReadOnlyList<MethodologyLibrary> imports)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(localFormulas);
        ArgumentNullException.ThrowIfNull(imports);

        if (localFormulas.TryGetValue(name, out var formulaId))
        {
            return new MethodologyReference(
                MethodologyReferenceOutcome.Local, formulaId, null, []);
        }

        var matches = imports
            .Where(library => library.FormulaCodes.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new MethodologyReference(MethodologyReferenceOutcome.NotFound, null, null, []),
            1 => new MethodologyReference(
                MethodologyReferenceOutcome.Imported, null, matches[0].MethodologyId, []),
            _ => new MethodologyReference(
                MethodologyReferenceOutcome.Ambiguous,
                null,
                null,
                matches.Select(m => m.MethodologyCode).Order(StringComparer.Ordinal).ToList()),
        };
    }
}

/// <summary>Чим скінчився резолвінг <c>!Name</c>.</summary>
public enum MethodologyReferenceOutcome : byte
{
    /// <summary>Формула цієї самої версії.</summary>
    Local = 0,

    /// <summary>Формула однієї з оголошених бібліотек.</summary>
    Imported = 1,

    /// <summary>Ніде не знайдено.</summary>
    NotFound = 2,

    /// <summary>Знайдено у двох і більше бібліотеках — помилка публікації.</summary>
    Ambiguous = 3
}

/// <summary>Куди веде <c>!Name</c>.</summary>
/// <param name="Outcome">Результат резолвінгу.</param>
/// <param name="FormulaId">Формула цієї версії; заповнено лише для <c>Local</c>.</param>
/// <param name="MethodologyId">
/// Методологія-джерело; заповнено лише для <c>Imported</c> — це і є ребро
/// <c>calc.MethodologyDependency</c>.
/// </param>
/// <param name="Candidates">Коди методологій-претендентів; непорожньо лише для <c>Ambiguous</c>.</param>
public sealed record MethodologyReference(
    MethodologyReferenceOutcome Outcome,
    int? FormulaId,
    int? MethodologyId,
    IReadOnlyList<string> Candidates);

/// <summary>
/// Транзитивне замикання бібліотечних формул версії (HSE301 L): які формули
/// імпортованих методологій справді потрібні її виразам і куди веде кожне ім'я.
/// </summary>
/// <remarks>
/// ⛔ Одне визначення на прогін і публікацію. Модуль рахує саме те, що тут зібрано, а
/// публікація перевіряє саме це (режими, області, аргументи); два окремі обходи
/// розійшлися б на першому ж ланцюгу «бібліотека → бібліотека».
/// <para>
/// ⚠ Правило резолвінгу — те саме, що в <see cref="MethodologyReferenceResolver"/>:
/// спершу формула своєї версії (своя перекриває бібліотечну), потім рівно один імпорт.
/// Ім'я, знайдене у двох імпортах або ніде, у замикання не йде — прогін дасть
/// <c>#REF</c>, а публікація таку версію не пропускає (<c>ambiguousReference</c>,
/// <c>formulaNotFound</c>).
/// </para>
/// <para>
/// ⛔ Цикл ловиться ТУТ, під час завантаження: імпорт, що веде в методологію, яка вже є
/// на шляху від викликача, стає посиланням <see cref="LibraryLink.IsCycle"/>, і далі
/// обхід не йде. Бібліотеку можуть перевидати з посиланням назад уже ПІСЛЯ публікації
/// викликача — без цієї межі завантаження ходило б по колу.
/// </para>
/// <para>
/// ⚠ За імпортами бібліотеки обхід іде лише тоді, коли ПОТРІБНА формула посилається за
/// її межу: версія, що нічого не імпортує, не коштує жодного звернення до сховища.
/// </para>
/// </remarks>
public static class MethodologyLibraryClosure
{
    /// <summary>Будує замикання для формул версії на бізнес-дату.</summary>
    /// <param name="store">Сховище методологій.</param>
    /// <param name="engine">Рушій — розбір і обхід залежностей виразу.</param>
    /// <param name="methodologyId">Методологія викликача — корінь шляху для циклу.</param>
    /// <param name="methodologyVersionId">Версія викликача — чиї імпорти читати.</param>
    /// <param name="formulas">Формули версії викликача.</param>
    /// <param name="onDate">Бізнес-дата: версія бібліотеки — чинна на неї.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Замикання; <c>null</c> — жодне ім'я за межу версії не веде.</returns>
    public static async Task<CalculationLibraries?> LoadAsync(
        IMethodologyStore store,
        IFormulaEngine engine,
        int methodologyId,
        int methodologyVersionId,
        IReadOnlyList<MethodologyFormula> formulas,
        DateOnly onDate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(formulas);

        var root = new Scope(methodologyId, string.Empty, methodologyVersionId, null, null, formulas, [], [methodologyId]);
        var scopes = new Dictionary<int, Scope>();
        var found = new List<Scope>();
        var pending = new Queue<(Scope Scope, MethodologyFormula Formula)>();

        foreach (var formula in formulas)
        {
            pending.Enqueue((root, formula));
        }

        while (pending.TryDequeue(out var item))
        {
            var (scope, formula) = item;

            foreach (var name in References(engine, formula.Expression))
            {
                if (scope.FormulasByCode.ContainsKey(name))
                {
                    // Своя формула кореня рахується модулем як завжди; своя формула
                    // бібліотеки — частина замикання.
                    if (!ReferenceEquals(scope, root) && scope.Need(name) is { } local)
                    {
                        pending.Enqueue((scope, local));
                    }

                    continue;
                }

                if (scope.Links.ContainsKey(name)
                    || await LinkAsync(store, scope, name, onDate, scopes, found, ct).ConfigureAwait(false) is not { } link)
                {
                    continue;
                }

                scope.Links[name] = link;

                if (!link.IsCycle && scopes[link.MethodologyVersionId].Need(name) is { } imported)
                {
                    pending.Enqueue((scopes[link.MethodologyVersionId], imported));
                }
            }
        }

        if (root.Links.Count == 0)
        {
            return null;
        }

        return new CalculationLibraries(
            root.Links,
            [.. found.Select(s => new CalculationLibrary(
                s.MethodologyId,
                s.Code,
                s.VersionId,
                s.NumericMode ?? NumericMode.Strict,
                s.CalendarMode ?? CalendarMode.Actual,
                [.. s.Needed.OrderBy(f => f.EvaluationOrder).ThenBy(f => f.Id)],
                s.Constants
                    .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (IReadOnlyList<MethodologyConstant>)[.. g.OrderBy(c => c.Id)],
                        StringComparer.OrdinalIgnoreCase),
                s.Links))]);
    }

    /// <summary>Куди веде ім'я, якого немає серед формул області; <c>null</c> — нікуди або в кілька.</summary>
    private static async Task<LibraryLink?> LinkAsync(
        IMethodologyStore store,
        Scope scope,
        string name,
        DateOnly onDate,
        Dictionary<int, Scope> scopes,
        List<Scope> found,
        CancellationToken ct)
    {
        scope.Imports ??= await store
            .GetLibraryContentsAsync(scope.VersionId, onDate, ct)
            .ConfigureAwait(false);

        var matches = scope.Imports
            .Where(c => c.Library.MethodologyVersionId is not null
                        && c.Formulas.Any(f => string.Equals(f.Code, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count != 1)
        {
            return null;
        }

        var content = matches[0];
        var library = content.Library;
        var versionId = library.MethodologyVersionId!.Value;

        if (scope.Path.Contains(library.MethodologyId))
        {
            return new LibraryLink(library.MethodologyId, library.MethodologyCode, versionId, IsCycle: true);
        }

        if (!scopes.ContainsKey(versionId))
        {
            var next = new Scope(
                library.MethodologyId,
                library.MethodologyCode,
                versionId,
                content.NumericMode,
                content.CalendarMode,
                content.Formulas,
                content.Constants,
                [.. scope.Path, library.MethodologyId]);

            scopes[versionId] = next;
            found.Add(next);
        }

        return new LibraryLink(library.MethodologyId, library.MethodologyCode, versionId, IsCycle: false);
    }

    /// <summary>Імена <c>!Name</c> виразу — обходом рушія (<c>H-3</c>), як у публікації.</summary>
    private static List<string> References(IFormulaEngine engine, string expression)
    {
        var parsed = engine.Parse(expression, ExpressionDialect.Methodology);
        if (parsed.Expression is null)
        {
            return [];
        }

        return engine
            .ExtractDependencies(
                parsed.Expression,
                snapshot: null,
                new DependencyContext(CurrentTableDefId: 0, CurrentRowKey: null, CurrentColumnDefId: null))
            .Dependencies
            .Select(d => d.FormulaCode)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Область імен: версія викликача (корінь) або версія бібліотеки.</summary>
    private sealed class Scope
    {
        public Scope(
            int methodologyId,
            string code,
            int versionId,
            NumericMode? numericMode,
            CalendarMode? calendarMode,
            IReadOnlyList<MethodologyFormula> formulas,
            IReadOnlyList<MethodologyConstant> constants,
            HashSet<int> path)
        {
            MethodologyId = methodologyId;
            Code = code;
            VersionId = versionId;
            NumericMode = numericMode;
            CalendarMode = calendarMode;
            Constants = constants;
            Path = path;

            foreach (var formula in formulas)
            {
                FormulasByCode.TryAdd(formula.Code, formula);
            }
        }

        public int MethodologyId { get; }

        public string Code { get; }

        public int VersionId { get; }

        public NumericMode? NumericMode { get; }

        public CalendarMode? CalendarMode { get; }

        public IReadOnlyList<MethodologyConstant> Constants { get; }

        /// <summary>Методології від викликача до цієї області включно.</summary>
        public HashSet<int> Path { get; }

        public Dictionary<string, MethodologyFormula> FormulasByCode { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, LibraryLink> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<MethodologyFormula> Needed { get; } = [];

        /// <summary>Імпорти цієї версії — читаються лише тоді, коли знадобилися.</summary>
        public IReadOnlyList<MethodologyLibraryContent>? Imports { get; set; }

        private readonly HashSet<string> _needed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Позначає формулу потрібною; <c>null</c> — вже була або такої немає.</summary>
        public MethodologyFormula? Need(string code)
        {
            if (!FormulasByCode.TryGetValue(code, out var formula) || !_needed.Add(formula.Code))
            {
                return null;
            }

            Needed.Add(formula);
            return formula;
        }
    }
}
