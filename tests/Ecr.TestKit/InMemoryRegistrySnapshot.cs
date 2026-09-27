using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.TestKit;

/// <summary>
/// Знімок довідників у пам'яті — для тестів функцій <c>REG*</c> і полів
/// <c>ROW.</c> (FEATURE-REGISTRY-TABLES §5.7).
/// </summary>
/// <remarks>
/// ⚠ Виконує контракт <see cref="IRegistrySnapshot"/> дослівно, бо тести
/// рушія доводять саме його: порядок <c>(Ordinal, Id)</c>; <c>null</c> на
/// невідомий довідник чи невідповідну кількість частин ключа; незаповнене поле
/// → <c>null</c>, неоголошене поле → <c>#REF</c>; коди довідників і полів — без
/// урахування регістру.
///
/// ⚠ **Видимість.** Бойовий завантажувач (RT-22) невидимих на дату записів у
/// знімок не кладе взагалі. Тут їх можна ОГОЛОСИТИ (<c>visible: false</c>) —
/// лише щоб тест читався як «запис закрито», а не «id 99 узявся нізвідки»; для
/// рушія вони поводяться рівно як відсутні: у перегляд і пошук не входять,
/// поле дає <c>#REF</c>.
///
/// ⛔ **Нормалізації ключа (§4.2) тут НЕМАЄ**, і це не пропуск: за контрактом
/// її робить реалізація знімка одним правилом із записом (<c>RegistryKeyNormalizer</c>,
/// RT-02), тобто бойовий завантажувач. Семантика <c>REGFIND</c> у рушії —
/// <c>#N/A</c>/<c>#MULTI</c>/<c>null</c> — від нормалізації не залежить. Частини
/// тут порівнюються точно: число — як <see cref="decimal"/>, текст — ординально.
/// </remarks>
public sealed class InMemoryRegistrySnapshot : IRegistrySnapshot
{
    private readonly Dictionary<string, RegistryDef> _registries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, Entry> _entries = [];

    /// <summary>Оголошує довідник.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="fields">Коди полів (усі, включно з ключовими й <c>Lookup</c>).</param>
    /// <param name="primaryKey">
    /// Поля первинного ключа в порядку <c>Ordinal</c>; порожньо — пошук за <c>Code</c>.
    /// </param>
    /// <param name="lookupFields">Які з полів — <c>Lookup</c> (для індексу <see cref="FindReferencing"/>).</param>
    /// <returns>Цей самий знімок — для ланцюжка.</returns>
    public InMemoryRegistrySnapshot AddRegistry(
        string code,
        IEnumerable<string> fields,
        IEnumerable<string>? primaryKey = null,
        IEnumerable<string>? lookupFields = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(fields);

        var declared = fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = (primaryKey ?? []).ToList();
        var lookups = (lookupFields ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (key.Concat(lookups).FirstOrDefault(f => !declared.Contains(f)) is { } stray)
        {
            throw new ArgumentException($"Поле '{stray}' не оголошене в довіднику '{code}'.", nameof(fields));
        }

        _registries[code] = new RegistryDef(declared, key, lookups);
        return this;
    }

    /// <summary>Додає запис довідника.</summary>
    /// <param name="registryCode">Код оголошеного довідника.</param>
    /// <param name="id">Id запису — він же значення <c>EntryRef</c>.</param>
    /// <param name="code">Код запису (пошук без первинного ключа).</param>
    /// <param name="values">Значення полів; відсутнє оголошене поле — незаповнене.</param>
    /// <param name="ordinal">Порядковий номер (перший ключ порядку перегляду).</param>
    /// <param name="visible">Чи видимий запис на дату знімка.</param>
    /// <returns>Цей самий знімок — для ланцюжка.</returns>
    public InMemoryRegistrySnapshot AddEntry(
        string registryCode,
        long id,
        string code,
        Dictionary<string, ExpressionValue>? values = null,
        int ordinal = 0,
        bool visible = true)
    {
        ArgumentNullException.ThrowIfNull(registryCode);
        ArgumentNullException.ThrowIfNull(code);

        if (!_registries.TryGetValue(registryCode, out var registry))
        {
            throw new ArgumentException($"Довідник '{registryCode}' не оголошено.", nameof(registryCode));
        }

        var own = new Dictionary<string, ExpressionValue>(values ?? [], StringComparer.OrdinalIgnoreCase);
        if (own.Keys.FirstOrDefault(f => !registry.Fields.Contains(f)) is { } stray)
        {
            throw new ArgumentException($"Поле '{stray}' не оголошене в довіднику '{registryCode}'.", nameof(values));
        }

        _entries.Add(id, new Entry(id, registryCode, code, ordinal, visible, own));
        return this;
    }

    /// <summary>
    /// Контекст обчислення, що бачить цей знімок, — поверх наявного.
    /// </summary>
    /// <param name="inner">Контекст із комірками, аргументами тощо.</param>
    /// <returns>Той самий контекст, але з <see cref="IEvaluationContext.Registries"/> = цей знімок.</returns>
    /// <remarks>
    /// ⚠ Делегує ВСЕ, включно з членами з типовою реалізацією
    /// (<see cref="IEvaluationContext.GetRegistryField"/>): інакше старий шлях
    /// <c>REGFIELD</c> через контекст у тестах мовчки ламався б.
    /// </remarks>
    public IEvaluationContext Attach(IEvaluationContext inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new SnapshotContext(inner, this);
    }

    /// <inheritdoc />
    public IReadOnlyList<long>? GetEntries(string registryCode)
    {
        ArgumentNullException.ThrowIfNull(registryCode);

        return _registries.ContainsKey(registryCode) ? Visible(registryCode).Select(e => e.Id).ToList() : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<long>? FindByPrimaryKey(string registryCode, IReadOnlyList<ExpressionValue> keyParts)
    {
        ArgumentNullException.ThrowIfNull(registryCode);
        ArgumentNullException.ThrowIfNull(keyParts);

        if (!_registries.TryGetValue(registryCode, out var registry))
        {
            return null;
        }

        if (registry.PrimaryKey.Count == 0)
        {
            return keyParts.Count == 1
                ? Visible(registryCode).Where(e => Same(ExpressionValue.Text(e.Code), keyParts[0])).Select(e => e.Id).ToList()
                : null;
        }

        if (keyParts.Count != registry.PrimaryKey.Count)
        {
            return null;
        }

        return Visible(registryCode)
            .Where(e => registry.PrimaryKey.Select((field, i) => Same(e.Value(field), keyParts[i])).All(match => match))
            .Select(e => e.Id)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<long>? FindReferencing(string registryCode, string lookupFieldCode, long targetEntryId)
    {
        ArgumentNullException.ThrowIfNull(registryCode);
        ArgumentNullException.ThrowIfNull(lookupFieldCode);

        if (!_registries.TryGetValue(registryCode, out var registry) || !registry.LookupFields.Contains(lookupFieldCode))
        {
            return null;
        }

        return Visible(registryCode)
            .Where(e => e.Value(lookupFieldCode).AsNumber() == targetEntryId)
            .Select(e => e.Id)
            .ToList();
    }

    /// <inheritdoc />
    public ExpressionValue GetField(long entryId, string fieldCode)
    {
        ArgumentNullException.ThrowIfNull(fieldCode);

        if (!_entries.TryGetValue(entryId, out var entry)
            || !entry.Visible
            || !_registries[entry.RegistryCode].Fields.Contains(fieldCode))
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        return entry.Value(fieldCode);
    }

    private IEnumerable<Entry> Visible(string registryCode)
        => _entries.Values
            .Where(e => e.Visible && string.Equals(e.RegistryCode, registryCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Ordinal)
            .ThenBy(e => e.Id);

    /// <summary>Точна рівність частини ключа: без нормалізації (див. примітку класу).</summary>
    private static bool Same(ExpressionValue stored, ExpressionValue part)
    {
        if (stored.IsNull || part.IsNull || stored.IsError || part.IsError)
        {
            return false;
        }

        if (stored.AsNumber() is { } a && part.AsNumber() is { } b)
        {
            return a == b;
        }

        return stored.Type == part.Type && Equals(stored.Value, part.Value);
    }

    private sealed record RegistryDef(
        HashSet<string> Fields, IReadOnlyList<string> PrimaryKey, HashSet<string> LookupFields);

    private sealed record Entry(
        long Id, string RegistryCode, string Code, int Ordinal, bool Visible,
        Dictionary<string, ExpressionValue> Values)
    {
        /// <summary>Значення оголошеного поля; незаповнене — <c>null</c> (<c>R-11</c>).</summary>
        public ExpressionValue Value(string field) => Values.GetValueOrDefault(field, ExpressionValue.Null);
    }

    /// <summary>Контекст-обгортка, що додає знімок.</summary>
    private sealed class SnapshotContext(IEvaluationContext inner, IRegistrySnapshot snapshot) : IEvaluationContext
    {
        public IRegistrySnapshot? Registries => snapshot;

        public PeriodContext Period => inner.Period;

        public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => inner.Read(reference);

        public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
            => inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

        public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
            => inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

        public ExpressionValue GetArgument(string name) => inner.GetArgument(name);

        public ExpressionValue GetConstant(string name) => inner.GetConstant(name);

        public ExpressionValue GetFormulaResult(string name) => inner.GetFormulaResult(name);

        public ExpressionValue GetHeader(string name) => inner.GetHeader(name);

        public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
            => inner.GetRegistryField(registryEntryId, fieldCode);

        public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
            => inner.Convert(value, fromUnitCode, toUnitCode);
    }
}
