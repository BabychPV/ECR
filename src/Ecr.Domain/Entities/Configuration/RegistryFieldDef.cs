using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Поле реєстру.</summary>
public sealed class RegistryFieldDef : Entity<int>
{
    private RegistryFieldDef() { }

    public RegistryFieldDef(int registryDefId, EcrCode code, LocalizedText name, CellDataType dataType, int ordinal)
    {
        RegistryDefId = registryDefId;
        Code = code.Value;
        NameL10n = name;
        DataType = dataType;
        Ordinal = ordinal;
    }

    public int RegistryDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public CellDataType DataType { get; private set; }
    public int Ordinal { get; private set; }
    public bool IsRequired { get; private set; }

    /// <summary>Чи входить у бізнес-ключ запису.</summary>
    public bool IsKey { get; private set; }

    /// <summary>Одиниця значення поля (напр. ліміт дозволу в м³).</summary>
    public int? UnitId { get; private set; }

    /// <summary>Посилання на інший реєстр: вкладеність або M:N.</summary>
    public int? RefRegistryDefId { get; private set; }

    /// <summary>Змінює підпис, порядок і обов'язковість поля (ФВ-8.12).</summary>
    /// <param name="name">Новий підпис мовами каталогу.</param>
    /// <param name="ordinal">Новий порядок у переліку.</param>
    /// <param name="isRequired">Чи обов'язкове поле.</param>
    /// <remarks>
    /// ⛔ <see cref="Code"/>, <see cref="DataType"/>, <see cref="IsKey"/> і
    /// <see cref="RefRegistryDefId"/> тут НЕ змінюються, і це рішення, а не
    /// недогляд (<c>D2-202</c>). Кожне з них перетлумачує вже збережені
    /// значення: код — те, чим на поле посилаються вирази і мапінг; тип —
    /// те, як читається колонка <c>dic.RegistryValue</c>; <see cref="IsKey"/>
    /// входить у бізнес-ключ запису, і його зміна перебудувала б
    /// <c>EntryKey</c> у кожного запису мовчки. Потрібне інше поле — заводять
    /// інше поле.
    ///
    /// ⚠ Обов'язковість змінити МОЖНА, і саме тому вона тут: вона нічого не
    /// перетлумачує, а лише вимагає значення від НАСТУПНИХ записів.
    /// </remarks>
    public void Update(LocalizedText name, int ordinal, bool isRequired)
    {
        ArgumentNullException.ThrowIfNull(name);

        NameL10n = name;
        Ordinal = ordinal;
        IsRequired = isRequired;
    }

    /// <summary>Позначає поле ключовим при створенні опису.</summary>
    /// <param name="isKey">Чи входить у бізнес-ключ.</param>
    /// <remarks>
    /// ⚠ Викликається лише під час створення поля — див. <see cref="Update"/>
    /// про те, чому ключовість наявного поля не змінюється.
    /// </remarks>
    public void MarkKey(bool isKey) => IsKey = isKey;

    /// <summary>Вказує довідник-джерело для поля-посилання.</summary>
    /// <param name="refRegistryDefId">Довідник; <c>null</c> — поле не є посиланням.</param>
    /// <remarks>
    /// ⚠ Так само лише під час створення: комірки <c>dic.RegistryValue</c>
    /// зберігають <c>ValueRefEntryId</c>, і зміна цілі перетворила б їх на
    /// посилання в чужий довідник.
    /// </remarks>
    public void PointTo(int? refRegistryDefId) => RefRegistryDefId = refRegistryDefId;

    /// <summary>Вказує одиницю значення поля.</summary>
    /// <param name="unitId">Одиниця; <c>null</c> — безрозмірне.</param>
    public void MeasureIn(int? unitId) => UnitId = unitId;
}

