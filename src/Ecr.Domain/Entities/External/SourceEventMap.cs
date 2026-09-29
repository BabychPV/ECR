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

    public SourceEventVolumeMode VolumeMode { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Поля «атрибут → колонка».</summary>
    public IReadOnlyList<SourceEventFieldMap> Fields => _fields;

    /// <summary>Додає поле мапінгу.</summary>
    /// <param name="spec">Опис поля.</param>
    /// <returns>Додане поле.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c>: колонка з іншої таблиці (<c>.eventMapColumnNotInTable</c>);
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
}
