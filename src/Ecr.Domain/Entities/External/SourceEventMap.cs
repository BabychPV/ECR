// src/Ecr.Domain/Entities/External/SourceEventMap.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Опис одного поля мапінгу «атрибут події → колонка» — вхід
/// <see cref="SourceEventMap.Create"/> і <see cref="SourceEventMap.AddField"/>.
/// </summary>
/// <param name="Target">Колонка динамічної таблиці мапінгу.</param>
/// <param name="SourceAttribute">
/// Ім'я атрибута з каталогу шаблону або зарезервоване <c>$start</c>, <c>$end</c>, <c>$name</c>.
/// </param>
/// <param name="Scope">Де лежить атрибут (HQ-18).</param>
/// <param name="ValueKind">Як значення лягає в колонку.</param>
/// <param name="SourceUnitId">Одиниця атрибута в джерелі; <c>null</c> — без конверсії.</param>
/// <param name="TargetUnitId">Одиниця колонки; <c>null</c> — без конверсії.</param>
public sealed record SourceEventFieldSpec(
    ColumnDef Target,
    string SourceAttribute,
    SourceEventAttributeScope Scope = SourceEventAttributeScope.Event,
    SourceEventValueKind ValueKind = SourceEventValueKind.Direct,
    int? SourceUnitId = null,
    int? TargetUnitId = null);

/// <summary>
/// Мапінг «шаблон подій джерела → динамічна таблиця документа»
/// (<c>ext.SourceEventMap</c>, FEATURE-HSE301-VIEW §4.7.3, V-17 → <c>D-186</c>).
/// </summary>
/// <remarks>
/// ⛔ Окрема сутність, а не ще один режим <see cref="EntityFieldMap"/>: той має
/// фіксованого адресата рядка, а тут подія САМА стає рядком (§4.7.3).
///
/// ⛔ <c>$start</c> і <c>$end</c> обов'язкові в кожному стані мапінгу, а не лише
/// під час синхронізації: мапінг без кінця дав би рядки без тривалості й без
/// вікна об'єму — помилку налаштування ловив би прогін синхронізації, а не
/// налаштування (той самий принцип, що <see cref="RowWindowMap.Create"/>).
///
/// ⚠ Ціль — лише таблиця з <see cref="TableRowMode.Dynamic"/>: рядки створює
/// синхронізація з ключем <c>EF-…</c> (§4.7.4, крок 2). <see cref="TableRowMode.Mixed"/>
/// відхиляється свідомо — події, змішані з фіксованими рядками шаблону, дали б
/// реєстр, у якому частину рядків не можна ні синхронізувати, ні видалити.
/// </remarks>
public sealed class SourceEventMap : Entity<int>
{
    /// <summary>Зарезервований атрибут: початок події.</summary>
    public const string StartAttribute = "$start";

    /// <summary>Зарезервований атрибут: кінець події (виключно).</summary>
    public const string EndAttribute = "$end";

    /// <summary>Зарезервований атрибут: назва події.</summary>
    public const string NameAttribute = "$name";

    /// <summary>Довжина імені атрибута — як <c>ext.EntityFieldMap.SourceField</c>.</summary>
    public const int MaxAttributeLength = 200;

    /// <summary>Довжина значення фільтра й значення джерела в явній відповідності.</summary>
    public const int MaxValueLength = 400;

    private static readonly string[] ReservedAttributes = [StartAttribute, EndAttribute, NameAttribute];

    private static readonly string[] RequiredAttributes = [StartAttribute, EndAttribute];

    private readonly List<SourceEventFieldMap> _fields = [];

    private SourceEventMap() { }

    private SourceEventMap(int sourceEntityId, long documentId, int tableDefId, SourceEventVolumeMode volumeMode)
    {
        SourceEntityId = sourceEntityId;
        DocumentId = documentId;
        TableDefId = tableDefId;
        VolumeMode = volumeMode;
        IsActive = true;
    }

    /// <summary>Створює мапінг, перевіривши таблицю й поля.</summary>
    /// <param name="sourceEntityId">Сутність-шаблон подій (<c>ext.SourceEntity</c>).</param>
    /// <param name="documentId">Документ ділянки, куди лягають події.</param>
    /// <param name="table">Таблиця-ціль; лише <see cref="TableRowMode.Dynamic"/>.</param>
    /// <param name="fields">Поля; серед них обов'язково <c>$start</c> і <c>$end</c>.</param>
    /// <param name="volumeMode">Звідки береться об'єм (§4.7.5).</param>
    /// <returns>Новий активний мапінг.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c>: таблиця не динамічна (<c>.eventMapTargetNotDynamic</c>),
    /// немає <c>$start</c> чи <c>$end</c> (<c>.eventMapStartEndRequired</c>) і
    /// відмови <see cref="AddField"/>.
    /// </exception>
    public static SourceEventMap Create(
        int sourceEntityId,
        long documentId,
        TableDef table,
        IEnumerable<SourceEventFieldSpec> fields,
        SourceEventVolumeMode volumeMode)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(fields);
        EnsureDefined(volumeMode, nameof(volumeMode));

        if (table.RowMode != TableRowMode.Dynamic)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Події лягають лише в динамічну таблицю; у таблиці «{table.Code}» режим рядків {table.RowMode}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapTargetNotDynamic",
                    ["tableCode"] = table.Code,
                    ["rowMode"] = table.RowMode.ToString(),
                });
        }

        var map = new SourceEventMap(sourceEntityId, documentId, table.Id, volumeMode);
        foreach (var field in fields)
        {
            map.AddField(field);
        }

        map.EnsureStartEnd(removing: null);
        return map;
    }

    public int SourceEntityId { get; private set; }

    public long DocumentId { get; private set; }

    /// <summary>Динамічна таблиця, у яку лягають події.</summary>
    public int TableDefId { get; private set; }

    /// <summary>Атрибут звуження (події лише цієї ділянки чи факела); <c>null</c> — без звуження.</summary>
    public string? FilterAttribute { get; private set; }

    public SourceEventAttributeScope? FilterScope { get; private set; }

    public string? FilterValue { get; private set; }

    public SourceEventVolumeMode VolumeMode { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Поля «атрибут → колонка».</summary>
    public IReadOnlyList<SourceEventFieldMap> Fields => _fields;

    /// <summary>Додає поле мапінгу.</summary>
    /// <param name="spec">Опис поля.</param>
    /// <returns>Додане поле.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c>: колонка з іншої таблиці (<c>.eventMapColumnNotInTable</c>),
    /// невідомий чи неправильно вжитий <c>$</c>-атрибут (<c>.eventMapReservedAttributeInvalid</c>),
    /// <c>$start</c>/<c>$end</c> не на <c>Date</c>-колонку (<c>.eventMapStartEndNotDate</c>),
    /// вид значення не пасує до типу колонки (<c>.eventMapValueKindMismatch</c>);
    /// <c>ECR-INT-0409</c> <c>.eventMapColumnTaken</c> — колонка вже має поле.
    /// </exception>
    public SourceEventFieldMap AddField(SourceEventFieldSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.SourceAttribute);
        EnsureDefined(spec.Scope, nameof(spec.Scope));
        EnsureDefined(spec.ValueKind, nameof(spec.ValueKind));

        var target = spec.Target;
        var attribute = spec.SourceAttribute.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attribute.Length, MaxAttributeLength, nameof(spec.SourceAttribute));

        if (target.TableDefId != TableDefId)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Колонка «{target.Code}» належить іншій таблиці, ніж таблиця мапінгу подій.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapColumnNotInTable",
                    ["targetColumn"] = target.Code,
                });
        }

        if (attribute.StartsWith('$'))
        {
            // «$Start» — те саме, що «$start»: зберігається канонічне ім'я, бо
            // синхронізація шукає зарезервовані атрибути порівнянням Ordinal.
            var known = Array.Find(
                ReservedAttributes, r => string.Equals(r, attribute, StringComparison.OrdinalIgnoreCase));
            attribute = known ?? attribute;
            if (known is null || spec.Scope != SourceEventAttributeScope.Event || spec.ValueKind != SourceEventValueKind.Direct)
            {
                throw new DomainException(
                    "ECR-INT-0422",
                    $"Атрибут «{attribute}» не є зарезервованим атрибутом події або вжитий не як пряме значення самої події.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0422.eventMapReservedAttributeInvalid",
                        ["attribute"] = attribute,
                    });
            }

            if (attribute is StartAttribute or EndAttribute && target.DataType != CellDataType.Date)
            {
                throw new DomainException(
                    "ECR-INT-0422",
                    $"Час події «{attribute}» лягає лише в колонку типу Date; «{target.Code}» має тип {target.DataType}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0422.eventMapStartEndNotDate",
                        ["attribute"] = attribute,
                        ["targetColumn"] = target.Code,
                        ["dataType"] = target.DataType.ToString(),
                    });
            }
        }

        // Lookup-колонка приймає лише запис довідника (за кодом, назвою чи явною
        // відповідністю), а решта — лише пряме значення. Обчислювану колонку
        // синхронізація не пише взагалі: її значення дає формула.
        var lookupKind = spec.ValueKind != SourceEventValueKind.Direct;
        var lookupColumn = target.DataType == CellDataType.Lookup;
        if (target.IsComputed)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Колонка «{target.Code}» типу {target.DataType} не приймає значення події виду {spec.ValueKind}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapValueKindMismatch",
                    ["targetColumn"] = target.Code,
                    ["dataType"] = target.DataType.ToString(),
                    ["valueKind"] = spec.ValueKind.ToString(),
                });
        }

        // Одне поле на колонку (UQ_SEFM_Target): два атрибути в ту саму комірку
        // дали б значення, яке залежить від порядку обходу. Колонки шаблону на
        // момент налаштування мапінгу вже збережені, тож порівняння за Id.
        if (_fields.Exists(f => f.TargetColumnDefId == target.Id))
        {
            throw new DomainException(
                "ECR-INT-0409",
                $"Колонка «{target.Code}» уже має поле в цьому мапінгу подій.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.eventMapColumnTaken",
                    ["targetColumn"] = target.Code,
                });
        }

        var field = new SourceEventFieldMap(
            Id, target.Id, attribute, spec.Scope, spec.ValueKind, spec.SourceUnitId, spec.TargetUnitId);
        _fields.Add(field);
        return field;
    }

    /// <summary>
    /// Замінює всі поля мапінгу новим переліком (HSE301 A6: <c>PUT</c> мапінгу — повна заміна).
    /// </summary>
    /// <param name="fields">Нові поля; серед них обов'язково <c>$start</c> і <c>$end</c>.</param>
    /// <exception cref="DomainException">Відмови <see cref="AddField"/> і <c>.eventMapStartEndRequired</c>.</exception>
    /// <remarks>
    /// ⚠ Атомарно: перелік спершу перевіряється на копії, тож відмова лишає поля мапінгу як були.
    /// Окремі <see cref="RemoveField"/> + <see cref="AddField"/> тут не годяться — заміна
    /// <c>$start</c> на інший атрибут проходила б через стан «без початку», який домен забороняє.
    /// </remarks>
    public void ReplaceFields(IEnumerable<SourceEventFieldSpec> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var specs = fields.ToList();

        EnsureFieldsAcceptable(specs);

        _fields.Clear();
        foreach (var spec in specs)
        {
            AddField(spec);
        }
    }

    /// <summary>
    /// Перевіряє перелік полів так, ніби мапінг створюється з ним, нічого в мапінгу не змінюючи.
    /// </summary>
    /// <param name="fields">Поля; серед них обов'язково <c>$start</c> і <c>$end</c>.</param>
    /// <exception cref="DomainException">Відмови <see cref="AddField"/> і <c>.eventMapStartEndRequired</c>.</exception>
    /// <remarks>
    /// ⚠ Окремий крок, а не лише всередині <see cref="ReplaceFields"/>: сховищу треба знати, що заміна ПРОЙДЕ, перш ніж
    /// позначати старі поля до видалення, — відмова після цього лишила б відстежені поля без власника.
    /// </remarks>
    public void EnsureFieldsAcceptable(IEnumerable<SourceEventFieldSpec> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var scratch = new SourceEventMap(SourceEntityId, DocumentId, TableDefId, VolumeMode);
        foreach (var spec in fields)
        {
            scratch.AddField(spec);
        }

        scratch.EnsureStartEnd(removing: null);
    }

    /// <summary>Прибирає поле колонки.</summary>
    /// <param name="targetColumnDefId">Колонка поля.</param>
    /// <returns><c>true</c> — поле було й прибране.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventMapStartEndRequired</c> — це останнє поле <c>$start</c> чи <c>$end</c>.
    /// </exception>
    public bool RemoveField(int targetColumnDefId)
    {
        var field = _fields.Find(f => f.TargetColumnDefId == targetColumnDefId);
        if (field is null)
        {
            return false;
        }

        EnsureStartEnd(removing: field);
        _fields.Remove(field);
        return true;
    }

    /// <summary>Задає звуження подій; усі три значення разом або жодного.</summary>
    /// <param name="attribute">Атрибут звуження.</param>
    /// <param name="scope">Де він лежить.</param>
    /// <param name="value">Значення, з яким порівнюється атрибут.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventMapFilterIncomplete</c> — задано не всі три.
    /// </exception>
    public void SetFilter(string? attribute, SourceEventAttributeScope? scope, string? value)
    {
        var name = string.IsNullOrWhiteSpace(attribute) ? null : attribute.Trim();
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (name is null && scope is null && text is null)
        {
            FilterAttribute = null;
            FilterScope = null;
            FilterValue = null;
            return;
        }

        // Половинний фільтр — або «звуження ні до чого», або «звуження до
        // порожнього»: обидва дають реєстр, повніший чи порожніший, ніж людина
        // налаштувала, і без жодного знаку про це.
        if (name is null || scope is null || text is null)
        {
            throw new DomainException(
                "ECR-INT-0422",
                "Звуження подій задається трьома значеннями разом: атрибут, де він лежить, і значення.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapFilterIncomplete",
                });
        }

        EnsureDefined(scope.Value, nameof(scope));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, MaxAttributeLength, nameof(attribute));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, MaxValueLength, nameof(value));

        FilterAttribute = name;
        FilterScope = scope;
        FilterValue = text;
    }

    /// <summary>Перемикає режим об'єму (§4.7.5) — даними, без релізу.</summary>
    public void SetVolumeMode(SourceEventVolumeMode volumeMode)
    {
        EnsureDefined(volumeMode, nameof(volumeMode));
        VolumeMode = volumeMode;
    }

    /// <summary>Вимикає синхронізацію за цим мапінгом; зв'язки й рядки лишаються.</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Вмикає синхронізацію знову.</summary>
    public void Activate() => IsActive = true;

    private void EnsureStartEnd(SourceEventFieldMap? removing)
    {
        foreach (var reserved in RequiredAttributes)
        {
            if (!_fields.Exists(f => !ReferenceEquals(f, removing)
                                     && string.Equals(f.SourceAttribute, reserved, StringComparison.Ordinal)))
            {
                throw new DomainException(
                    "ECR-INT-0422",
                    $"Мапінг подій мусить класти «{StartAttribute}» і «{EndAttribute}» у Date-колонки; бракує «{reserved}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0422.eventMapStartEndRequired",
                        ["attribute"] = reserved,
                    });
            }
        }
    }

    private static void EnsureDefined<TEnum>(TEnum value, string parameter)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameter, value, "Невідоме значення переліку.");
        }
    }
}

/// <summary>
/// Поле мапінгу «атрибут події → колонка» (<c>ext.SourceEventFieldMap</c>, §4.7.3).
/// </summary>
public sealed class SourceEventFieldMap : Entity<int>
{
    private readonly List<SourceEventValueMap> _values = [];

    private SourceEventFieldMap() { }

    internal SourceEventFieldMap(
        int sourceEventMapId,
        int targetColumnDefId,
        string sourceAttribute,
        SourceEventAttributeScope attributeScope,
        SourceEventValueKind valueKind,
        int? sourceUnitId,
        int? targetUnitId)
    {
        SourceEventMapId = sourceEventMapId;
        TargetColumnDefId = targetColumnDefId;
        SourceAttribute = sourceAttribute;
        AttributeScope = attributeScope;
        ValueKind = valueKind;
        SourceUnitId = sourceUnitId;
        TargetUnitId = targetUnitId;
    }

    public int SourceEventMapId { get; private set; }

    public int TargetColumnDefId { get; private set; }

    /// <summary>Ім'я атрибута з каталогу або <c>$start</c>/<c>$end</c>/<c>$name</c>.</summary>
    public string SourceAttribute { get; private set; } = null!;

    public SourceEventAttributeScope AttributeScope { get; private set; }

    public SourceEventValueKind ValueKind { get; private set; }

    public int? SourceUnitId { get; private set; }

    public int? TargetUnitId { get; private set; }

    /// <summary>Явні відповідності «значення джерела → запис довідника».</summary>
    public IReadOnlyList<SourceEventValueMap> Values => _values;

    /// <summary>Додає явну відповідність значення джерела запису довідника.</summary>
    /// <param name="sourceValue">Значення атрибута в джерелі.</param>
    /// <param name="registryEntryId">Запис довідника Lookup-колонки.</param>
    /// <returns>Додана відповідність.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventMapValueMapNotAllowed</c> — поле не виду
    /// <see cref="SourceEventValueKind.ValueMap"/>; <c>ECR-INT-0409</c>
    /// <c>.eventMapSourceValueTaken</c> — те саме значення вже зіставлено.
    /// </exception>
    /// <remarks>
    /// ⚠ Порівняння без регістру й з обрізкою — так само, як його порівнює
    /// <c>UQ_SEVM_Value</c> у базі (зіставлення <c>_CI_</c>).
    /// </remarks>
    public SourceEventValueMap AddValue(string sourceValue, long registryEntryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceValue);

        var value = sourceValue.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, SourceEventMap.MaxValueLength, nameof(sourceValue));

        // Відповідність на полі, яке шукає за кодом чи назвою, не діяла б
        // ніколи: людина бачила б налаштоване «зима → Winter» і порожню комірку.
        if (ValueKind != SourceEventValueKind.ValueMap)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Поле атрибута «{SourceAttribute}» шукає запис довідника як {ValueKind}; явна відповідність тут не діє.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapValueMapNotAllowed",
                    ["attribute"] = SourceAttribute,
                    ["valueKind"] = ValueKind.ToString(),
                });
        }

        if (_values.Exists(v => string.Equals(v.SourceValue, value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException(
                "ECR-INT-0409",
                $"Значення «{value}» уже зіставлено із записом довідника в цьому полі.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.eventMapSourceValueTaken",
                    ["sourceValue"] = value,
                });
        }

        var map = new SourceEventValueMap(Id, value, registryEntryId);
        _values.Add(map);
        return map;
    }
}

/// <summary>
/// Явна відповідність «значення джерела → запис довідника»
/// (<c>ext.SourceEventValueMap</c>, §4.7.3, §4.7.6).
/// </summary>
public sealed class SourceEventValueMap : Entity<int>
{
    private SourceEventValueMap() { }

    internal SourceEventValueMap(int sourceEventFieldMapId, string sourceValue, long registryEntryId)
    {
        SourceEventFieldMapId = sourceEventFieldMapId;
        SourceValue = sourceValue;
        RegistryEntryId = registryEntryId;
    }

    public int SourceEventFieldMapId { get; private set; }

    public string SourceValue { get; private set; } = null!;

    /// <summary>
    /// Запис довідника. ⚠ У домені <c>long</c> (<c>RegistryEntry : Entity&lt;long&gt;</c>), у базі
    /// <c>int</c> (<c>dic.RegistryEntry.Id</c> — <c>int</c>, звуження — <c>HasConversion&lt;int&gt;</c>,
    /// як у <c>RegistryEntryKey</c>): колонка мусить мати тип ключа, на який вказує.
    /// </summary>
    public long RegistryEntryId { get; private set; }
}
