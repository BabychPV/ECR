using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries;

/// <summary>
/// Значення поля запису довідника, прочитане завантажувачем знімка: колонки
/// <c>dic.RegistryValue</c> плюс код одиниці (для полів типу <c>Unit</c>).
/// </summary>
/// <param name="EntryId">Запис.</param>
/// <param name="FieldDefId">Поле (<c>cfg.RegistryFieldDef.Id</c>).</param>
/// <param name="Numeric">Число (<c>Int</c>/<c>Decimal</c>).</param>
/// <param name="Text">Текст (<c>String</c>).</param>
/// <param name="Date">Дата (<c>Date</c>).</param>
/// <param name="Bool">Булеве (<c>Bool</c>).</param>
/// <param name="RefEntryId">Ціль <c>Lookup</c>.</param>
/// <param name="UnitId">Одиниця (<c>Unit</c>; у числового поля — його одиниця).</param>
/// <param name="UnitCode">Код одиниці <paramref name="UnitId"/>; <c>null</c> — одиниці немає.</param>
public sealed record RegistrySnapshotValue(
    long EntryId,
    int FieldDefId,
    decimal? Numeric,
    string? Text,
    DateTime? Date,
    bool? Bool,
    long? RefEntryId,
    int? UnitId,
    string? UnitCode);

/// <summary>
/// Незмінний знімок довідників на бізнес-дату (реалізація <see cref="IRegistrySnapshot"/>,
/// RT-22, FEATURE-REGISTRY-TABLES §5.7).
/// </summary>
/// <remarks>
/// ⛔ <b>Видимість застосовується тут, і лише тут</b>: рушій її не знає
/// (<see cref="IRegistrySnapshot"/>). Запис потрапляє в знімок, якщо
/// <list type="number">
/// <item>він обирається на бізнес-дату — <see cref="RegistryResolver.IsSelectable"/>
/// (вікно <c>[ValidFrom, ValidTo)</c> містить дату, запис активний і не видалений). Та сама
/// умова, що в пікері <c>Lookup</c>, тож формула й пікер бачать один набір записів (§3.6);</item>
/// <item>якщо довідник — дитина композиції (<c>RelationKind = Composition</c>, <c>D-155</c>),
/// то його батько теж у знімку — рекурсивно. Дитина без батька (порожнє поле композиції)
/// невидима: «рядок видно рівно тоді, коли видно батька» (§4.8).</item>
/// </list>
///
/// ⚠ Ключі нормалізуються <see cref="RegistryKeyNormalizer"/> — тим самим, що пише
/// <c>dic.RegistryEntryKey</c>. Хеш тут не потрібен: індекс у пам'яті — за канонічним рядком.
/// Рядки <c>dic.RegistryEntryKey</c> не читаються навмисно: вони поточні, а знімок — «станом
/// на» момент прогону; ключ будується з тих самих значень <c>AS OF</c>.
///
/// ⚠ Після <see cref="Create"/> нічого не змінюється, тож читання з кількох потоків безпечне
/// без блокувань.
/// </remarks>
public sealed class RegistrySnapshot : IRegistrySnapshot
{
    private static readonly IReadOnlyList<long> None = Array.Empty<long>();

    private readonly Dictionary<string, RegistryData> _registries;
    private readonly Dictionary<long, EntryData> _entries;
    private readonly Dictionary<string, int> _unitIdsByCode;

    private RegistrySnapshot(
        Dictionary<string, RegistryData> registries,
        Dictionary<long, EntryData> entries,
        Dictionary<string, int> unitIdsByCode)
    {
        _registries = registries;
        _entries = entries;
        _unitIdsByCode = unitIdsByCode;
    }

    /// <summary>Знімок без жодного довідника: кожна функція довідника дає <c>#REF</c>.</summary>
    public static RegistrySnapshot Empty { get; } = new(
        new Dictionary<string, RegistryData>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<long, EntryData>(),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Кількість записів, видимих у знімку (усіх довідників).</summary>
    public int EntryCount => _entries.Count;

    /// <summary>Будує знімок і застосовує видимість.</summary>
    /// <param name="registries">Довідники разом із полями (<see cref="RegistryDef.Fields"/>).</param>
    /// <param name="primaryKeys">Активні первинні ключі цих довідників разом із частинами.</param>
    /// <param name="entries">
    /// Записи довідників «станом на» системний момент — УСІ, без фільтра видимості.
    /// </param>
    /// <param name="values">Значення полів цих записів «станом на» той самий момент.</param>
    /// <param name="businessDate">Бізнес-дата — останній день періоду.</param>
    /// <returns>Незмінний знімок.</returns>
    public static RegistrySnapshot Create(
        IReadOnlyList<RegistryDef> registries,
        IReadOnlyList<RegistryKeyDef> primaryKeys,
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyList<RegistrySnapshotValue> values,
        DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(primaryKeys);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(values);

        var byId = new Dictionary<int, RegistryData>();
        foreach (var registry in registries)
        {
            var key = primaryKeys.FirstOrDefault(k => k.RegistryDefId == registry.Id);
            byId[registry.Id] = new RegistryData(registry, key);
        }

        // 1. Власна видимість запису — умова пікера.
        var resolver = new RegistryResolver();
        var candidates = new Dictionary<long, RegistryEntry>();
        foreach (var entry in entries)
        {
            if (byId.ContainsKey(entry.RegistryDefId) && resolver.IsSelectable(entry, businessDate))
            {
                candidates[entry.Id] = entry;
            }
        }

        var valuesByEntry = new Dictionary<long, Dictionary<int, RegistrySnapshotValue>>();
        var unitIdsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value is { UnitId: { } unitId, UnitCode: { } unitCode })
            {
                unitIdsByCode[unitCode] = unitId;
            }

            if (!candidates.ContainsKey(value.EntryId))
            {
                continue;
            }

            if (!valuesByEntry.TryGetValue(value.EntryId, out var perEntry))
            {
                valuesByEntry[value.EntryId] = perEntry = new Dictionary<int, RegistrySnapshotValue>();
            }

            perEntry[value.FieldDefId] = value;
        }

        // 2. Видимість батька композиції — рекурсивно. ⛔ Правило одне, у RegistryResolver (RT-12):
        // друга копія тут розійшлася б із переліком записів на першому ж виправленні.
        var isVisible = resolver.VisibleWithCompositionParents(
            candidates.ContainsKey,
            entryId =>
            {
                var composition = byId[candidates[entryId].RegistryDefId].Composition;
                if (composition is null)
                {
                    return (false, null);
                }

                var parent = valuesByEntry.TryGetValue(entryId, out var own)
                             && own.TryGetValue(composition.Id, out var link)
                    ? link.RefEntryId
                    : null;
                return (true, parent);
            });

        var visible = new Dictionary<long, EntryData>();
        foreach (var entry in candidates.Values
                     .Where(e => isVisible(e.Id))
                     .OrderBy(e => e.Ordinal)
                     .ThenBy(e => e.Id))
        {
            var registry = byId[entry.RegistryDefId];
            var data = new EntryData(
                entry.Id,
                entry.Code,
                registry,
                valuesByEntry.TryGetValue(entry.Id, out var own) ? own : new Dictionary<int, RegistrySnapshotValue>());
            visible[entry.Id] = data;
            registry.Entries.Add(entry.Id);
        }

        // 3. Індекси — вже по видимих записах і в порядку (Ordinal, Id).
        foreach (var registry in byId.Values)
        {
            foreach (var entryId in registry.Entries)
            {
                var entry = visible[entryId];

                if (registry.Canonical(entry) is { } canonical)
                {
                    registry.AddByKey(canonical, entryId);
                }

                foreach (var field in registry.LookupFields)
                {
                    if (entry.Values.TryGetValue(field.Id, out var value) && value.RefEntryId is { } target)
                    {
                        registry.AddReferencing(field.Id, target, entryId);
                    }
                }
            }
        }

        var byCode = new Dictionary<string, RegistryData>(StringComparer.OrdinalIgnoreCase);
        foreach (var registry in byId.Values)
        {
            byCode[registry.Code] = registry;
        }

        return new RegistrySnapshot(byCode, visible, unitIdsByCode);
    }

    /// <inheritdoc />
    public IReadOnlyList<long>? GetEntries(string registryCode)
        => _registries.TryGetValue(registryCode, out var registry) ? registry.ReadOnlyEntries : null;

    /// <inheritdoc />
    public IReadOnlyList<long>? FindByPrimaryKey(string registryCode, IReadOnlyList<ExpressionValue> keyParts)
    {
        ArgumentNullException.ThrowIfNull(keyParts);

        if (!_registries.TryGetValue(registryCode, out var registry) || keyParts.Count != registry.KeyArity)
        {
            return null;
        }

        var parts = new RegistryKeyPart[keyParts.Count];
        for (var i = 0; i < keyParts.Count; i++)
        {
            var dataType = registry.KeyPartType(i);
            if (keyParts[i].IsNull)
            {
                // Контракт: сюди null не доходить (REGFIND з порожньою частиною дає null до
                // пошуку). Якщо все ж дійшов — такого ключа не буває (D-153).
                return None;
            }

            if (!TryKeyPart(dataType, keyParts[i], out var part))
            {
                // Тип частини не збігається з полем ключа: публікація це відхиляє
                // (перевірка 17), отже опис змінили після неї — #REF.
                return null;
            }

            if (part is null)
            {
                return None;
            }

            parts[i] = new RegistryKeyPart(dataType, part);
        }

        var canonical = RegistryKeyNormalizer.Canonical(parts, registry.KeyIgnoreCase);
        return canonical is not null && registry.TryGetByKey(canonical, out var found) ? found : None;
    }

    /// <inheritdoc />
    public IReadOnlyList<long>? FindReferencing(string registryCode, string lookupFieldCode, long targetEntryId)
    {
        if (!_registries.TryGetValue(registryCode, out var registry)
            || !registry.Fields.TryGetValue(lookupFieldCode, out var field)
            || field.DataType != CellDataType.Lookup)
        {
            return null;
        }

        return registry.TryGetReferencing(field.Id, targetEntryId, out var found) ? found : None;
    }

    /// <inheritdoc />
    public ExpressionValue GetField(long entryId, string fieldCode)
    {
        if (!_entries.TryGetValue(entryId, out var entry)
            || !entry.Registry.Fields.TryGetValue(fieldCode, out var field))
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        if (!entry.Values.TryGetValue(field.Id, out var value))
        {
            return ExpressionValue.Null;
        }

        return field.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal => value.Numeric is { } n ? ExpressionValue.Number(n) : ExpressionValue.Null,
            CellDataType.String => value.Text is { } s ? ExpressionValue.Text(s) : ExpressionValue.Null,
            CellDataType.Bool => value.Bool is { } b ? ExpressionValue.Boolean(b) : ExpressionValue.Null,
            CellDataType.Date => value.Date is { } d ? ExpressionValue.Date(d) : ExpressionValue.Null,

            // ⚠ Ціль, невидима на дату знімка (закрита, видалена, дитина невидимого батька), —
            // #REF, а не id: тихо підставити «щось інше» гірше за помилку (§5.4).
            CellDataType.Lookup => value.RefEntryId switch
            {
                null => ExpressionValue.Null,
                { } target when _entries.ContainsKey(target) => ExpressionValue.Number(target),
                _ => ExpressionValue.Error(ExpressionErrors.BadReference),
            },
            CellDataType.Unit => value.UnitId is null
                ? ExpressionValue.Null
                : value.UnitCode is { } unit ? ExpressionValue.Text(unit) : ExpressionValue.Error(ExpressionErrors.BadReference),
            _ => ExpressionValue.Error(ExpressionErrors.BadReference),
        };
    }

    /// <summary>Значення виразу → типізована частина ключа для <see cref="RegistryKeyNormalizer"/>.</summary>
    /// <returns>
    /// <c>false</c> — тип не приводиться; <c>true</c> з <paramref name="part"/> = <c>null</c> —
    /// приводиться, але такої частини в знімку бути не може (одиниця з невідомим кодом,
    /// дробовий id запису).
    /// </returns>
    private bool TryKeyPart(CellDataType dataType, ExpressionValue value, out object? part)
    {
        part = null;
        switch (dataType)
        {
            case CellDataType.String when value.Type == ExpressionValueType.Text:
                part = (string)value.Value!;
                return true;

            case CellDataType.Int or CellDataType.Decimal when value.AsNumber() is { } number:
                part = number;
                return true;

            case CellDataType.Bool when value.Type == ExpressionValueType.Boolean:
                part = (bool)value.Value!;
                return true;

            case CellDataType.Date when value.Type == ExpressionValueType.Date:
                part = (DateTime)value.Value!;
                return true;

            case CellDataType.Lookup when value.AsNumber() is { } id:
                part = id == decimal.Truncate(id) && id > 0 && id <= long.MaxValue ? (long)id : null;
                return true;

            case CellDataType.Unit when value.Type == ExpressionValueType.Text:
                part = _unitIdsByCode.TryGetValue((string)value.Value!, out var unitId) ? unitId : null;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Поле довідника в знімку.</summary>
    private sealed record FieldData(int Id, string Code, CellDataType DataType, bool IsComposition);

    /// <summary>Видимий запис: код, довідник, значення за id поля.</summary>
    private sealed record EntryData(
        long Id,
        string Code,
        RegistryData Registry,
        Dictionary<int, RegistrySnapshotValue> Values);

    /// <summary>Довідник у знімку: поля, ключ, видимі записи й індекси.</summary>
    private sealed class RegistryData
    {
        private readonly FieldData[] _keyParts;
        private readonly Dictionary<string, List<long>> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<(int FieldId, long Target), List<long>> _referencing = new();
        private IReadOnlyList<long>? _readOnlyEntries;

        public RegistryData(RegistryDef registry, RegistryKeyDef? primaryKey)
        {
            Code = registry.Code;
            Fields = registry.Fields
                .Select(f => new FieldData(
                    f.Id,
                    f.Code,
                    f.DataType,
                    f.DataType == CellDataType.Lookup && f.RelationKind == RegistryRelationKind.Composition))
                .ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
            var byFieldId = Fields.Values.ToDictionary(f => f.Id);

            Composition = Fields.Values.FirstOrDefault(f => f.IsComposition);
            LookupFields = [.. Fields.Values.Where(f => f.DataType == CellDataType.Lookup)];

            if (primaryKey is null)
            {
                // Без первинного ключа REGFIND шукає за Code — однією текстовою частиною (§4.1).
                _keyParts = [];
                KeyIgnoreCase = true;
            }
            else
            {
                _keyParts = [.. primaryKey.Fields
                    .OrderBy(k => k.Ordinal)
                    .Select(k => byFieldId.TryGetValue(k.RegistryFieldDefId, out var f)
                        ? f
                        : throw new InvalidOperationException(
                            $"Ключ «{primaryKey.Code}» довідника «{registry.Code}» посилається на поле {k.RegistryFieldDefId}, якого в довіднику немає."))];
                KeyIgnoreCase = primaryKey.IgnoreCase;
            }
        }

        public string Code { get; }

        public Dictionary<string, FieldData> Fields { get; }

        public FieldData? Composition { get; }

        public FieldData[] LookupFields { get; }

        public bool KeyIgnoreCase { get; }

        public int KeyArity => _keyParts.Length == 0 ? 1 : _keyParts.Length;

        public List<long> Entries { get; } = [];

        public IReadOnlyList<long> ReadOnlyEntries => _readOnlyEntries ??= Entries.AsReadOnly();

        public CellDataType KeyPartType(int index)
            => _keyParts.Length == 0 ? CellDataType.String : _keyParts[index].DataType;

        /// <summary>Канонічний рядок первинного ключа запису; <c>null</c> — частина порожня.</summary>
        public string? Canonical(EntryData entry)
        {
            if (_keyParts.Length == 0)
            {
                return RegistryKeyNormalizer.Canonical(
                    [new RegistryKeyPart(CellDataType.String, entry.Code)], ignoreCase: true);
            }

            var parts = new RegistryKeyPart[_keyParts.Length];
            for (var i = 0; i < _keyParts.Length; i++)
            {
                var field = _keyParts[i];
                entry.Values.TryGetValue(field.Id, out var value);
                parts[i] = new RegistryKeyPart(field.DataType, StoredPart(field.DataType, value));
            }

            return RegistryKeyNormalizer.Canonical(parts, KeyIgnoreCase);
        }

        public void AddByKey(string canonical, long entryId)
        {
            if (!_byKey.TryGetValue(canonical, out var list))
            {
                _byKey[canonical] = list = [];
            }

            list.Add(entryId);
        }

        public bool TryGetByKey(string canonical, out IReadOnlyList<long> found)
        {
            var hit = _byKey.TryGetValue(canonical, out var list);
            found = hit ? list!.AsReadOnly() : None;
            return hit;
        }

        public void AddReferencing(int fieldId, long target, long entryId)
        {
            if (!_referencing.TryGetValue((fieldId, target), out var list))
            {
                _referencing[(fieldId, target)] = list = [];
            }

            list.Add(entryId);
        }

        public bool TryGetReferencing(int fieldId, long target, out IReadOnlyList<long> found)
        {
            var hit = _referencing.TryGetValue((fieldId, target), out var list);
            found = hit ? list!.AsReadOnly() : None;
            return hit;
        }

        /// <summary>Збережене значення як частина ключа — ті самі типи, що пише запис.</summary>
        private static object? StoredPart(CellDataType dataType, RegistrySnapshotValue? value)
            => value is null
                ? null
                : dataType switch
                {
                    CellDataType.String => value.Text,
                    CellDataType.Int or CellDataType.Decimal => value.Numeric,
                    CellDataType.Bool => value.Bool,
                    CellDataType.Date => value.Date,
                    CellDataType.Lookup => value.RefEntryId,
                    CellDataType.Unit => value.UnitId,
                    _ => null,
                };
    }
}
