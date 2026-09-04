# 05b — Скелет: `Ecr.Domain`

> Частина [`05-skeleton.md`](05-skeleton.md).
>
> **Правило цієї частини:** файли, позначені `COPY FROM`, беруться **дослівно**
> з `02-contracts.md` — щоб контракт мав рівно одне джерело і не розійшовся
> між двома файлами. Решта наведена повністю тут.
>
> `Ecr.Domain` **не має жодної зовнішньої залежності**. Якщо здається, що
> потрібна — це `questions.md`, а не `PackageReference`.

---

## 1. Файли, що копіюються з контрактів

| Файл | Джерело |
|---|---|
| `src/Ecr.Domain/Abstractions/IClock.cs` | [`02-contracts.md#conventions`](02-contracts.md#conventions) |
| `src/Ecr.Domain/Enums/Enums.cs` | [`02-contracts.md#enums`](02-contracts.md#enums) — усі перелічення §2 |
| `src/Ecr.Domain/ValueObjects/PeriodKey.cs` | [`02-contracts.md#value-objects`](02-contracts.md#value-objects) |
| `src/Ecr.Domain/ValueObjects/CellAddress.cs` | те саме |
| `src/Ecr.Domain/ValueObjects/CellValueData.cs` | те саме |
| `src/Ecr.Domain/ValueObjects/LocalizedText.cs` | те саме |
| `src/Ecr.Domain/ValueObjects/EcrCode.cs` | те саме |
| `src/Ecr.Domain/ValueObjects/RowKey.cs` | те саме |

Копіювати **без змін**, разом із XML-doc. Ці типи вже остаточні: `TODO` в них
немає і не має з'явитися.

---

## 2. Абстракції

### `src/Ecr.Domain/Abstractions/Entity.cs`
MODULE: domain | STAGE: 1
CONTRACT: 02-contracts.md#conventions
SCOPE: базовий тип сутності з ідентичністю.
NOT IN SCOPE: аудит, доменні події — вони не потрібні цій системі.

```csharp
namespace Ecr.Domain.Abstractions;

/// <summary>
/// Сутність із ідентичністю. Рівність — за <typeparamref name="TId"/>,
/// а не за значеннями полів.
/// </summary>
/// <typeparam name="TId">Тип ідентифікатора: <see cref="int"/> для метаданих,
/// <see cref="long"/> для даних документів.</typeparam>
public abstract class Entity<TId> where TId : struct, IEquatable<TId>
{
    /// <summary>Ідентифікатор. Для нових сутностей — значення за замовчуванням.</summary>
    public TId Id { get; protected set; }

    /// <summary>Чи збережена сутність у сховищі.</summary>
    public bool IsPersisted => !Id.Equals(default);

    public override bool Equals(object? obj)
        => obj is Entity<TId> other
           && other.GetType() == GetType()
           && IsPersisted
           && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
```

---

### `src/Ecr.Domain/Abstractions/DomainException.cs`
MODULE: domain | STAGE: 1
CONTRACT: 02-contracts.md#error-codes
SCOPE: порушення доменного інваріанта.
NOT IN SCOPE: HTTP-статуси — це рівень API.

```csharp
namespace Ecr.Domain.Abstractions;

/// <summary>
/// Порушення доменного інваріанта. Несе код із каталогу помилок, щоб
/// повідомлення дійшло до клієнта машинно-читаним, а не текстом.
/// </summary>
public sealed class DomainException(string errorCode, string message) : Exception(message)
{
    /// <summary>Код із <see href="02-contracts.md#error-codes">каталогу помилок</see>.</summary>
    public string ErrorCode { get; } = errorCode;
}
```

---

## 3. `Entities/Configuration`

### `src/Ecr.Domain/Entities/Configuration/Template.cs`
MODULE: domain-metadata | STAGE: 1
CONTRACT: 02a-db-schema.md#cfg
SCOPE: шаблон звітності — контейнер версій.
NOT IN SCOPE: структура аркушів — вона у версії, не в шаблоні.

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Іменований набір структур звітності. Сам по собі структури не містить:
/// вона живе у <see cref="TemplateVersion"/>, бо мусить бути версійною.
/// </summary>
public sealed class Template : Entity<int>
{
    private readonly List<TemplateVersion> _versions = [];

    private Template() { }   // для EF Core

    /// <summary>Створює шаблон.</summary>
    public Template(EcrCode code, LocalizedText name, int createdByUserId, DateTime utcNow)
    {
        Code = code.Value;
        NameL10n = name;
        CreatedByUserId = createdByUserId;
        CreatedAt = utcNow;
        IsActive = true;
    }

    /// <summary>Код шаблону, унікальний у системі.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Локалізована назва.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Довільні теги (<c>["ECR","Land"]</c>) — замість предметних колонок у ядрі.</summary>
    public string? TagsJson { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }

    /// <summary>Версії шаблону.</summary>
    public IReadOnlyList<TemplateVersion> Versions => _versions;

    /// <summary>Позначає шаблон неактивним. Наявні документи не зачіпаються.</summary>
    public void Deactivate() => IsActive = false;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/TemplateVersion.cs`
MODULE: domain-metadata | STAGE: 1
CONTRACT: 02a-db-schema.md#cfg
SCOPE: версія шаблону та інваріанти публікації — найважливіші в системі.
NOT IN SCOPE: валідація виразів — це `Ecr.Expressions`; тут лише стан.

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Версія шаблону. Після <see cref="Publish"/> **структурно незмінна**:
/// змінюється лише презентаційний шар, і кожна така зміна інкрементує
/// <see cref="PresentationRevision"/>.
/// </summary>
/// <remarks>
/// Саме ця незмінність робить ключ кешу <c>v{id}:r{rev}</c> самодостатнім і
/// прибирає когерентність кешу між інстансами як клас проблеми (D-16).
/// </remarks>
public sealed class TemplateVersion : Entity<int>
{
    private readonly List<SheetDef> _sheets = [];

    private TemplateVersion() { }

    /// <summary>Створює чернетку версії.</summary>
    public TemplateVersion(int templateId, string version, int createdByUserId, DateTime utcNow, int? clonedFromVersionId = null)
    {
        TemplateId = templateId;
        Version = version;
        Status = TemplateVersionStatus.Draft;
        ClonedFromVersionId = clonedFromVersionId;
        PresentationRevision = 0;
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
    }

    public int TemplateId { get; private set; }
    public string Version { get; private set; } = null!;
    public TemplateVersionStatus Status { get; private set; }
    public int? ClonedFromVersionId { get; private set; }

    /// <summary>Ревізія презентаційного шару. Інкрементує застосунок одним statement (R-B7).</summary>
    public int PresentationRevision { get; private set; }

    public byte[]? SourceWorkbookHash { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public int? PublishedByUserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }

    public IReadOnlyList<SheetDef> Sheets => _sheets;

    /// <summary>Чи заборонені структурні зміни.</summary>
    public bool IsStructurallyFrozen => Status is TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated;

    /// <summary>Ключ кешу метаданих.</summary>
    public string CacheKey => $"v{Id}:r{PresentationRevision}";

    /// <summary>
    /// Публікує версію. Валідація цілісності виконується <b>до</b> виклику
    /// (ФВ-2.9) — тут лише перехід стану.
    /// </summary>
    /// <exception cref="DomainException">Версія вже опублікована.</exception>
    public void Publish(int publishedByUserId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити Status == Draft (інакше DomainException ECR-TMPL-0409); " +
            "виставити Status = Published, PublishedAt = utcNow, PublishedByUserId; " +
            "PresentationRevision лишити 0.");

    /// <summary>
    /// Реєструє презентаційну правку. Викликається <b>після</b> успішного
    /// оновлення в БД, значення береться з <c>OUTPUT</c> (R-B7).
    /// </summary>
    public void ApplyPresentationRevision(int newRevision)
        => throw new NotImplementedException(
            "TODO: перевірити newRevision == PresentationRevision + 1; інакше DomainException " +
            "(розбіжність означає паралельну правку, яку ми пропустили); присвоїти значення.");

    /// <summary>Перевіряє, чи допустима структурна зміна в поточному стані.</summary>
    /// <exception cref="DomainException">Версія структурно заморожена.</exception>
    public void EnsureStructurallyMutable()
        => throw new NotImplementedException(
            "TODO: якщо IsStructurallyFrozen — кинути DomainException з кодом ECR-TMPL-0409 " +
            "і поясненням, що структурні зміни робляться через CloneFrom (ФВ-7.1).");
}
```

---

### `src/Ecr.Domain/Entities/Configuration/SheetDef.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Аркуш шаблону — аналог аркуша Excel.</summary>
public sealed class SheetDef : Entity<int>
{
    private readonly List<TableDef> _tables = [];

    private SheetDef() { }

    public SheetDef(int templateVersionId, EcrCode code, LocalizedText name, int ordinal)
    {
        TemplateVersionId = templateVersionId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        IsVisible = true;
    }

    public int TemplateVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Порядок відображення. **Не ідентичність** — на нього не можна посилатися.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Група аркушів для правил складу документа (<c>SheetGroupRule</c>).</summary>
    public string? SheetGroup { get; private set; }

    public bool IsMandatory { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    public IReadOnlyList<TableDef> Tables => _tables;

    /// <summary>Змінює порядок — **презентаційна** операція, дозволена після публікації.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>Логічне видалення: фізично запис лишається, бо на нього посилаються дані (ФВ-7.6).</summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/TableDef.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Таблиця на аркуші.</summary>
public sealed class TableDef : Entity<int>
{
    private readonly List<ColumnDef> _columns = [];
    private readonly List<RowDef> _rows = [];

    private TableDef() { }

    public TableDef(int sheetDefId, EcrCode code, LocalizedText name, int ordinal,
                    TableLayoutKind layoutKind, TableRowMode rowMode)
    {
        SheetDefId = sheetDefId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        LayoutKind = layoutKind;
        RowMode = rowMode;
        StorageMode = CellStorageMode.Normalized;
    }

    public int SheetDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public TableLayoutKind LayoutKind { get; private set; }
    public TableRowMode RowMode { get; private set; }
    public int? MaxDynamicRows { get; private set; }
    public int? HeaderStyleId { get; private set; }

    /// <summary>
    /// Фізична модель зберігання комірок саме цієї таблиці. Перехід на гібрид
    /// **вибірковий** — глобальний перехід не потрібен і надто дорогий (D-21).
    /// </summary>
    public CellStorageMode StorageMode { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    public IReadOnlyList<ColumnDef> Columns => _columns;
    public IReadOnlyList<RowDef> Rows => _rows;

    /// <summary>Чи може користувач додавати рядки.</summary>
    public bool AllowsDynamicRows => RowMode is TableRowMode.Dynamic or TableRowMode.Mixed;

    /// <summary>Перемикає модель зберігання за результатом гейта Етапу 0.</summary>
    public void SwitchStorage(CellStorageMode mode) => StorageMode = mode;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/ColumnDef.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Колонка таблиці: тип, формат, довідник, одиниця.</summary>
public sealed class ColumnDef : Entity<int>
{
    private ColumnDef() { }

    public ColumnDef(int tableDefId, EcrCode code, LocalizedText header, int ordinal, CellDataType dataType)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        HeaderL10n = header;
        Ordinal = ordinal;
        DataType = dataType;
    }

    public int TableDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText HeaderL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public CellDataType DataType { get; private set; }
    public byte? Precision { get; private set; }
    public byte? Scale { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool IsRequired { get; private set; }
    public bool IsHidden { get; private set; }
    public bool IsMonthColumn { get; private set; }
    public byte? MonthNumber { get; private set; }
    public string? DefaultValue { get; private set; }
    public string? DisplayFormat { get; private set; }

    /// <summary>Довідник для листбокса. Обов'язковий при <see cref="CellDataType.Lookup"/>.</summary>
    public int? LookupRegistryDefId { get; private set; }

    /// <summary>Звуження списку довідника.</summary>
    public string? LookupFilter { get; private set; }

    /// <summary>Каскад: список залежить від значення іншої колонки.</summary>
    public int? CascadeFromColumnId { get; private set; }

    /// <summary>Одиниця, в якій зберігаються значення колонки (ФВ-16.1).</summary>
    public int? UnitId { get; private set; }

    public bool IsBusinessKey { get; private set; }
    public bool IsScopeField { get; private set; }
    public bool IsIndexed { get; private set; }
    public int? StyleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Чи значення обчислюється системою, а не вводиться користувачем.</summary>
    public bool IsComputed => DataType is CellDataType.Formula or CellDataType.Calculated;

    /// <summary>Перевіряє, чи значення сумісне з типом і обмеженнями колонки.</summary>
    /// <returns>Код помилки з каталогу або <c>null</c>, якщо все гаразд.</returns>
    public string? ValidateValue(CellValueData value)
        => throw new NotImplementedException(
            "TODO: перевірити IsWellFormed(); відповідність заповненого поля DataType; " +
            "IsRequired проти IsEmpty; для Lookup — наявність ValueRegistryEntryId; " +
            "для Unit — наявність ValueUnitId (R-A4); для Decimal — Precision/Scale. " +
            "Повернути ECR-CELL-0422 або ECR-CELL-4221 для IsComputed, або null.");
}
```

---

### `src/Ecr.Domain/Entities/Configuration/RowDef.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Рядок фіксованої таблиці. Ідентичність — <see cref="RowKey"/>;
/// <see cref="Ordinal"/> відповідає лише за порядок на екрані.
/// </summary>
/// <remarks>
/// Це головна відмінність від чинного рішення, де ідентичність рядка була
/// позиційною (<c>Attribute_0010</c> = «перший рядок діапазону»), і вставка
/// рядка в Excel робила всі раніше збережені дані неправильними.
/// </remarks>
public sealed class RowDef : Entity<int>
{
    private RowDef() { }

    public RowDef(int tableDefId, RowKey rowKey, int ordinal, LocalizedText label, RowKind rowKind)
    {
        TableDefId = tableDefId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        LabelL10n = label;
        RowKind = rowKind;
    }

    public int TableDefId { get; private set; }

    /// <summary>Стабільна бізнес-ідентичність (<c>"7001001"</c>).</summary>
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Порядок відображення. Змінюється після публікації — це презентаційна правка.</summary>
    public int Ordinal { get; private set; }

    public LocalizedText LabelL10n { get; private set; } = null!;
    public RowKind RowKind { get; private set; }
    public int? ParentRowDefId { get; private set; }
    public bool IsReadOnly { get; private set; }
    public int? StyleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Змінює порядок — презентаційна операція.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/StyleDef.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Іменований стиль. Стиль — окрема сутність, а не набір атрибутів комірки:
/// інакше зміна оформлення означала б зміну даних.
/// </summary>
public sealed class StyleDef : Entity<int>
{
    private StyleDef() { }

    public StyleDef(int templateVersionId, EcrCode code)
    {
        TemplateVersionId = templateVersionId;
        Code = code.Value;
    }

    public int TemplateVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public string? FontName { get; private set; }
    public decimal? FontSize { get; private set; }
    public bool IsBold { get; private set; }
    public bool IsItalic { get; private set; }
    public int? ForegroundArgb { get; private set; }
    public int? BackgroundArgb { get; private set; }
    public string? BorderJson { get; private set; }
    public byte? HorizontalAlign { get; private set; }
    public byte? VerticalAlign { get; private set; }
    public bool WrapText { get; private set; }
    public string? NumberFormat { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/FormulaDef.cs`
MODULE: domain-metadata | STAGE: 2

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Формула шаблону: вираз плюс область дії.</summary>
public sealed class FormulaDef : Entity<int>
{
    private FormulaDef() { }

    public FormulaDef(int tableDefId, FormulaScope scope, string expression, ExpressionDialect dialect)
    {
        TableDefId = tableDefId;
        Scope = scope;
        Expression = expression;
        Dialect = dialect;
    }

    public int TableDefId { get; private set; }
    public FormulaScope Scope { get; private set; }
    public int? ColumnDefId { get; private set; }
    public int? RowDefId { get; private set; }
    public ExpressionDialect Dialect { get; private set; }
    public string Expression { get; private set; } = null!;

    /// <summary>
    /// Топологічний порядок. Обчислюється **при публікації**, а не в рантаймі:
    /// сортувати граф на кожен запит — це витрата, якої бюджет не передбачає.
    /// </summary>
    public int EvaluationOrder { get; private set; }

    public bool IsCrossSheet { get; private set; }

    /// <summary>Знімок: значення матеріалізується один раз і не перераховується каскадом.</summary>
    public bool IsSnapshot { get; private set; }

    public bool IsDeleted { get; private set; }

    /// <summary>Фіксує обчислений порядок. Викликається лише під час <c>Publish</c>.</summary>
    public void SetEvaluationOrder(int order) => EvaluationOrder = order;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/FormulaDependency.cs`
MODULE: domain-metadata | STAGE: 2

```csharp
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Розкрита залежність формули. Діапазони рядків матеріалізуються в явний
/// список <c>RowKey</c> на момент <c>Publish</c> — **у рантаймі діапазонів
/// не існує** (B03 §4).
/// </summary>
/// <remarks>
/// Саме тому зміна <c>Ordinal</c> після публікації не змінює результат:
/// формула вже посилається на конкретні рядки, а не на позиції.
/// </remarks>
public sealed class FormulaDependency : Entity<long>
{
    private FormulaDependency() { }

    public FormulaDependency(byte sourceKind, byte dependsOnKind, int sortOrder)
    {
        SourceKind = sourceKind;
        DependsOnKind = dependsOnKind;
        SortOrder = sortOrder;
    }

    /// <summary>0 — формула, 1 — прив'язка розрахунку.</summary>
    public byte SourceKind { get; private set; }

    public int? FormulaDefId { get; private set; }
    public int? BindingId { get; private set; }

    /// <summary>0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</summary>
    public byte DependsOnKind { get; private set; }

    public int? TableDefId { get; private set; }

    /// <summary>**Конкретний** рядок, не діапазон.</summary>
    public string? RowKey { get; private set; }

    public int? ColumnDefId { get; private set; }

    /// <summary>Предикат для <c>RowMode = Dynamic</c>: списку рядків наперед не існує.</summary>
    public string? FilterJson { get; private set; }

    /// <summary>Зсув періоду: <c>[Period:-1]</c> → −1.</summary>
    public short? PeriodOffset { get; private set; }

    /// <summary>Позиція в розкритому діапазоні.</summary>
    public int SortOrder { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/ValidationRule.cs`
MODULE: domain-metadata | STAGE: 2

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Правило валідації. Рівень визначає, чи блокує воно запис (R-B3).</summary>
public sealed class ValidationRule : Entity<int>
{
    private ValidationRule() { }

    public ValidationRule(int tableDefId, EcrCode code, ValidationSeverity severity, byte scope,
                          string expression, LocalizedText message)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        Severity = severity;
        Scope = scope;
        Expression = expression;
        MessageL10n = message;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public ValidationSeverity Severity { get; private set; }

    /// <summary>0 Cell, 1 Row, 2 Table, 3 Document.</summary>
    public byte Scope { get; private set; }

    public int? ColumnDefId { get; private set; }
    public string Expression { get; private set; } = null!;
    public LocalizedText MessageL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    /// <summary>
    /// Чи блокує правило збереження. Блокує **лише** комірковий <c>Error</c>:
    /// заборона зберегти проміжний стан робить роботу з великою таблицею
    /// неможливою (R-B3).
    /// </summary>
    public bool BlocksSave => Severity == ValidationSeverity.Error && Scope == 0;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/TableRelationDef.cs`
MODULE: domain-metadata | STAGE: 2

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Зв'язок між таблицями: дзеркало, rollup, посилання, каскад, перевірка, копія.</summary>
public sealed class TableRelationDef : Entity<int>
{
    private TableRelationDef() { }

    public TableRelationDef(EcrCode code, int sourceTableDefId, int targetTableDefId,
                            TableRelationKind kind, string matchJson)
    {
        Code = code.Value;
        SourceTableDefId = sourceTableDefId;
        TargetTableDefId = targetTableDefId;
        RelationKind = kind;
        MatchJson = matchJson;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public int SourceTableDefId { get; private set; }
    public int TargetTableDefId { get; private set; }
    public TableRelationKind RelationKind { get; private set; }

    /// <summary>Як зіставляються рядки джерела і приймача.</summary>
    public string MatchJson { get; private set; } = null!;

    /// <summary>Які колонки на які.</summary>
    public string? MapJson { get; private set; }

    /// <summary>0 Recalc, 1 Warn, 2 Block — що робити при зміні джерела.</summary>
    public byte OnSourceChange { get; private set; }

    public bool IsActive { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/PeriodAccessRuleDef.cs`
MODULE: domain-metadata | STAGE: 3

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Правило доступу до періоду — заміна кнопки <c>Protect</c> чинного рішення
/// (ФВ-2.15): який аркуш або таблиця в якому періоді доступні на введення.
/// </summary>
public sealed class PeriodAccessRuleDef : Entity<int>
{
    private PeriodAccessRuleDef() { }

    public PeriodAccessRuleDef(int templateVersionId, OutOfWindowBehavior onOutOfWindow)
    {
        TemplateVersionId = templateVersionId;
        OnOutOfWindow = onOutOfWindow;
    }

    public int TemplateVersionId { get; private set; }
    public int? SheetDefId { get; private set; }
    public int? TableDefId { get; private set; }

    /// <summary><c>null</c> — правило діє для всіх ролей.</summary>
    public int? RoleId { get; private set; }

    /// <summary>Від якого порядкового номера періоду доступно; <c>null</c> — без обмеження.</summary>
    public byte? FromSequence { get; private set; }

    public byte? ToSequence { get; private set; }
    public OutOfWindowBehavior OnOutOfWindow { get; private set; }

    /// <summary>Чи діє правило для періоду з таким порядковим номером.</summary>
    public bool AppliesTo(byte sequence)
        => (FromSequence is null || sequence >= FromSequence)
        && (ToSequence   is null || sequence <= ToSequence);
}
```

---

### `src/Ecr.Domain/Entities/Configuration/SheetGroupRule.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Правило складу документа: які групи аркушів обов'язкові разом або взаємовиключні.</summary>
public sealed class SheetGroupRule : Entity<int>
{
    private SheetGroupRule() { }

    public SheetGroupRule(int templateVersionId, string sheetGroup, byte ruleKind, string? targetGroup)
    {
        TemplateVersionId = templateVersionId;
        SheetGroup = sheetGroup;
        RuleKind = ruleKind;
        TargetGroup = targetGroup;
    }

    public int TemplateVersionId { get; private set; }
    public string SheetGroup { get; private set; } = null!;

    /// <summary>0 RequiresAll, 1 RequiresOne, 2 Excludes.</summary>
    public byte RuleKind { get; private set; }

    public string? TargetGroup { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/RegistryDef.cs`
MODULE: domain-metadata | STAGE: 4

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Визначення реєстру. **Один механізм на всі довідники** — від плоского
/// списку до <c>Permit</c> із каскадами і M:N (ФВ-8.1).
/// </summary>
public sealed class RegistryDef : Entity<int>
{
    private readonly List<RegistryFieldDef> _fields = [];

    private RegistryDef() { }

    public RegistryDef(EcrCode code, LocalizedText name, bool isTemporal)
    {
        Code = code.Value;
        NameL10n = name;
        IsTemporal = isTemporal;
        SourceKind = RegistrySourceKind.Local;
        DataRevision = 0;
        DefinitionVersion = 1;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Чи мають записи період дії (резолвінг «станом на дату періоду»).</summary>
    public bool IsTemporal { get; private set; }

    /// <summary>Хто master: зовнішня система, гібрид або ECR (ФВ-8.9).</summary>
    public RegistrySourceKind SourceKind { get; private set; }

    /// <summary>
    /// Ревізія **даних**. Входить у ключ кешу списків: без неї кеш або
    /// застаріває після синку, або потребує інвалідації між інстансами.
    /// </summary>
    public int DataRevision { get; private set; }

    /// <summary>Версія **визначення** (склад полів).</summary>
    public int DefinitionVersion { get; private set; }

    public bool IsActive { get; private set; }
    public IReadOnlyList<RegistryFieldDef> Fields => _fields;

    /// <summary>Інкремент ревізії даних після зміни записів.</summary>
    public void BumpDataRevision() => DataRevision++;

    /// <summary>
    /// Перемикає master. Дозволено **лише поза відкритим періодом** —
    /// перевірка виконується в use-case, тут лише зміна стану (ФВ-8.9).
    /// </summary>
    public void SwitchSource(RegistrySourceKind kind) => SourceKind = kind;
}
```

---

### `src/Ecr.Domain/Entities/Configuration/RegistryFieldDef.cs`
MODULE: domain-metadata | STAGE: 4

```csharp
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
}
```

---

### `src/Ecr.Domain/Entities/Configuration/CalculationBinding.cs`
MODULE: domain-metadata | STAGE: 4

```csharp
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Прив'язка результату методології до колонки документа.
/// Значення **не копіюється** в <c>doc.CellValue</c> — воно читається за
/// посиланням (D-69): інакше нічний перерахунок писав би десятки мільйонів
/// рядків у партиції документів і роздував аудит.
/// </summary>
public sealed class CalculationBinding : Entity<int>
{
    private CalculationBinding() { }

    public CalculationBinding(int tableDefId, int columnDefId, int methodologyId, string outputCode, string matchJson)
    {
        TableDefId = tableDefId;
        ColumnDefId = columnDefId;
        MethodologyId = methodologyId;
        OutputCode = outputCode;
        MatchJson = matchJson;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public int ColumnDefId { get; private set; }
    public int MethodologyId { get; private set; }

    /// <summary>Який вихід методології (<c>tons</c>, <c>gsec</c>).</summary>
    public string OutputCode { get; private set; } = null!;

    /// <summary>Як зіставити рядок документа з результатом розрахунку.</summary>
    public string MatchJson { get; private set; } = null!;

    public bool IsActive { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Configuration/TemplateVersionSnapshot.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Незмінний знімок структури версії для кешу метаданих. Будується один раз
/// і живе під ключем <c>v{id}:r{rev}</c> — інвалідація не потрібна (D-16).
/// </summary>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару.</param>
/// <param name="Sheets">Аркуші з таблицями, колонками і рядками.</param>
/// <param name="ColumnsById">Плоский індекс колонок для швидкого доступу.</param>
/// <param name="RowsByKey">Індекс рядків: <c>(TableDefId, RowKey)</c> → <see cref="RowDef"/>.</param>
public sealed record TemplateVersionSnapshot(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDef> Sheets,
    IReadOnlyDictionary<int, ColumnDef> ColumnsById,
    IReadOnlyDictionary<(int TableDefId, string RowKey), RowDef> RowsByKey)
{
    /// <summary>Ключ кешу.</summary>
    public string CacheKey => $"v{TemplateVersionId}:r{PresentationRevision}";
}
```

---

## 4. `Entities/Documents`

### `src/Ecr.Domain/Entities/Documents/Project.cs`
MODULE: domain-documents | STAGE: 1
CONTRACT: 02a-db-schema.md#doc

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Проєкт — звітний рік або інший діапазон.</summary>
public sealed class Project : Entity<int>
{
    private readonly List<Period> _periods = [];

    private Project() { }

    public Project(EcrCode code, LocalizedText name, DateOnly periodStart, DateOnly periodEnd,
                   int templateVersionId, PeriodKind periodKind, int periodPolicyId, string timeZoneId)
    {
        Code = code.Value;
        NameL10n = name;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        TemplateVersionId = templateVersionId;
        PeriodKind = periodKind;
        PeriodPolicyId = periodPolicyId;
        TimeZoneId = timeZoneId;
        Status = ProjectStatus.Draft;
        CurrentPeriodMode = CurrentPeriodMode.Auto;
        YearGraceOffsetDays = 45;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Джерело істини про межі проєкту.</summary>
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Лише підпис для UI. **Не ідентичність.**</summary>
    public short? Year { get; private set; }

    public string? TagsJson { get; private set; }
    public int TemplateVersionId { get; private set; }
    public PeriodKind PeriodKind { get; private set; }
    public int PeriodPolicyId { get; private set; }
    public int YearGraceOffsetDays { get; private set; }

    /// <summary>
    /// Пояс майданчика. У ньому рахуються межі періодів, offsets і
    /// <c>IsLateEdit</c> — не в UTC (D-68).
    /// </summary>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>Режим визначення поточного періоду (D-77).</summary>
    public CurrentPeriodMode CurrentPeriodMode { get; private set; }

    /// <summary>Поточний звітний період — **наша конфігурація**, а не значення з AF.</summary>
    public int? CurrentPeriodId { get; private set; }

    public string? CurrentPeriodPinnedReason { get; private set; }
    public DateTime? CurrentPeriodChangedAt { get; private set; }
    public int? CurrentPeriodChangedByUserId { get; private set; }

    /// <summary>Разові налаштування, зібрані ззовні при створенні. **Не постійна залежність.**</summary>
    public string? ExternalSettingsJson { get; private set; }

    public ProjectStatus Status { get; private set; }

    /// <summary>Прапорець виконання архівації: читання має брати джерело за станом, не за датою.</summary>
    public bool IsArchiving { get; private set; }

    public DateTime? ClosedAt { get; private set; }
    public int? ClosedByUserId { get; private set; }

    public IReadOnlyList<Period> Periods => _periods;

    /// <summary>
    /// Фіксує поточний період вручну. Причина обов'язкова: стан неочевидний
    /// і має бути видимим в UI.
    /// </summary>
    public void PinCurrentPeriod(int periodId, string reason, int userId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити, що period належить цьому проєкту і reason не порожній; " +
            "виставити CurrentPeriodMode = Pinned, CurrentPeriodId, причину і аудит-поля.");

    /// <summary>Повертає автоматичне визначення поточного періоду.</summary>
    public void UnpinCurrentPeriod(int userId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: CurrentPeriodMode = Auto; CurrentPeriodPinnedReason = null; " +
            "CurrentPeriodId лишити — його перерахує PeriodStateJob.");

    /// <summary>Оновлює поточний період у режимі <c>Auto</c>. Викликає лише <c>PeriodStateJob</c>.</summary>
    public void SetCurrentPeriodAutomatically(int? periodId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: якщо CurrentPeriodMode = Pinned — нічого не робити (ручний пін має пріоритет); " +
            "інакше присвоїти CurrentPeriodId і CurrentPeriodChangedAt.");

    /// <summary>Перевіряє, що дата належить проєкту (ФВ-1.11).</summary>
    public bool ContainsDate(DateOnly date) => date >= PeriodStart && date <= PeriodEnd;
}
```

---

### `src/Ecr.Domain/Entities/Documents/PeriodPolicy.cs`
MODULE: domain-documents | STAGE: 3

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Offsets переходів періоду. Налаштовуються адміністратором (ФВ-1.6).</summary>
public sealed class PeriodPolicy : Entity<int>
{
    private PeriodPolicy() { }

    public PeriodPolicy(EcrCode code, int openOffsetDays, int graceOffsetDays,
                        int hardCloseOffsetDays, int yearGraceOffsetDays)
    {
        Code = code.Value;
        OpenOffsetDays = openOffsetDays;
        GraceOffsetDays = graceOffsetDays;
        HardCloseOffsetDays = hardCloseOffsetDays;
        YearGraceOffsetDays = yearGraceOffsetDays;
    }

    public string Code { get; private set; } = null!;

    /// <summary>Коли період відкривається, від його **початку**. Може бути від'ємним.</summary>
    public int OpenOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення періоду редагування ще дозволене.</summary>
    public int GraceOffsetDays { get; private set; }

    /// <summary>Коли період закривається остаточно.</summary>
    public int HardCloseOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення **року** дані ще редагуються.</summary>
    public int YearGraceOffsetDays { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Documents/Period.cs`
MODULE: domain-documents | STAGE: 3

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Звітний період. Стан — **збережене значення**, яке рахує
/// <c>PeriodStateJob</c>, а не функція від <c>now()</c> у запиті (ФВ-1.12).
/// </summary>
public sealed class Period : Entity<int>
{
    private Period() { }

    public Period(int projectId, PeriodKey periodKey, byte sequence, DateOnly start, DateOnly end)
    {
        ProjectId = projectId;
        PeriodKeyValue = periodKey.Value;
        Sequence = sequence;
        PeriodStart = start;
        PeriodEnd = end;
        State = PeriodState.Scheduled;
    }

    public int ProjectId { get; private set; }

    /// <summary><c>Year*100 + Sequence</c> — ключ партиціонування (R-A6).</summary>
    public int PeriodKeyValue { get; private set; }

    public byte Sequence { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public PeriodState State { get; private set; }

    /// <summary>Обчислені межі — денормалізація, щоб перевірка доступу не рахувала offsets щоразу.</summary>
    public DateTime ComputedOpenAt { get; private set; }
    public DateTime ComputedGraceAt { get; private set; }
    public DateTime ComputedCloseAt { get; private set; }

    /// <summary>Тимчасове відкриття адміністратором.</summary>
    public DateTime? ReopenedUntil { get; private set; }
    public string? ReopenReason { get; private set; }
    public DateTime StateChangedAt { get; private set; }

    /// <summary>Ключ періоду як значеннєвий тип.</summary>
    public PeriodKey Key => new(PeriodKeyValue);

    /// <summary>Чи дозволене редагування в поточному стані.</summary>
    public bool AllowsEditing => State is PeriodState.Open or PeriodState.Grace;

    /// <summary>Чи є зміна пізньою — потребує позначки <c>IsLateEdit</c> (D-70).</summary>
    public bool IsLateEditWindow => State == PeriodState.Grace;

    /// <summary>Перераховує межі за політикою в поясі майданчика.</summary>
    public void RecomputeBoundaries(PeriodPolicy policy, TimeZoneInfo siteTimeZone)
        => throw new NotImplementedException(
            "TODO: обчислити ComputedOpenAt = PeriodStart + OpenOffsetDays, " +
            "ComputedGraceAt = PeriodEnd + 1 день, ComputedCloseAt = PeriodEnd + HardCloseOffsetDays; " +
            "усі — опівночі В ПОЯСІ МАЙДАНЧИКА, потім TimeZoneInfo.ConvertTimeToUtc (D-68). " +
            "DateTime.Now використовувати заборонено.");

    /// <summary>Переводить у новий стан. Викликає лише <c>PeriodStateJob</c>.</summary>
    public void TransitionTo(PeriodState state, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити допустимість переходу (Scheduled→Open→Grace→Closed, " +
            "назад лише Closed→Grace через Reopen); присвоїти State і StateChangedAt.");

    /// <summary>Відкриває закритий період до вказаного моменту. Причина обов'язкова.</summary>
    public void Reopen(DateTime until, string reason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити State == Closed і непорожню причину; State = Grace, " +
            "ReopenedUntil = until, ReopenReason = reason.");
}
```

---

### `src/Ecr.Domain/Entities/Documents/Document.cs`
MODULE: domain-documents | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Документ — екземпляр звітності; аналог «HSE-файлу» чинного рішення.</summary>
public sealed class Document : Entity<long>
{
    private readonly List<DocumentSheet> _sheets = [];

    private Document() { }

    public Document(int projectId, string businessKey, int createdByUserId, DateTime utcNow)
    {
        ProjectId = projectId;
        BusinessKey = businessKey;
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
        ModifiedAt = utcNow;
        ModifiedByUserId = createdByUserId;
    }

    public int ProjectId { get; private set; }

    /// <summary>Складається зі значень колонок із <c>IsBusinessKey</c>.</summary>
    public string BusinessKey { get; private set; } = null!;

    public LocalizedText? NameL10n { get; private set; }

    // ⛔ Статусу документа тут НЕМАЄ (D-93). Гранулярність затвердження —
    // аркуш × період (D-38), і єдине джерело істини — wf.ApprovalState.
    // Скалярний Status тут був би другим джерелом, яке рано чи пізно
    // розійдеться з першим і покаже «Approved» на документі, половина
    // аркушів якого ще в Draft. Потрібен зведений стан для списку
    // документів — рахуйте його запитом до wf.ApprovalState, не колонкою.

    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public int ModifiedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<DocumentSheet> Sheets => _sheets;

    /// <summary>Фіксує зміну документа.</summary>
    public void Touch(int userId, DateTime utcNow)
    {
        ModifiedAt = utcNow;
        ModifiedByUserId = userId;
    }
}
```

---

### `src/Ecr.Domain/Entities/Documents/DocumentSheet.cs`
MODULE: domain-documents | STAGE: 1

```csharp
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Включення аркуша до складу документа (ФВ-3.2).</summary>
public sealed class DocumentSheet : Entity<long>
{
    private DocumentSheet() { }

    public DocumentSheet(long documentId, int sheetDefId)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        IsIncluded = true;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public bool IsIncluded { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Documents/TableInstance.cs`
MODULE: domain-documents | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Екземпляр таблиці документа за період. Ключ складений
/// <c>(PeriodKey, Id)</c>: партиційний ключ мусить входити в PK, інакше
/// індекси не вирівняні (R-A1).
/// </summary>
public sealed class TableInstance
{
    private TableInstance() { }

    public TableInstance(PeriodKey periodKey, long id, long documentId, int tableDefId, DateTime utcNow)
    {
        PeriodKeyValue = periodKey.Value;
        Id = id;
        DocumentId = documentId;
        TableDefId = tableDefId;
        CreatedAt = utcNow;
        ModifiedAt = utcNow;
    }

    public int PeriodKeyValue { get; private set; }

    /// <summary>Береться з <c>SEQUENCE</c>, не <c>IDENTITY</c>: значення потрібне до вставки.</summary>
    public long Id { get; private set; }

    public long DocumentId { get; private set; }
    public int TableDefId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public PeriodKey PeriodKey => new(PeriodKeyValue);

    public void Touch(DateTime utcNow) => ModifiedAt = utcNow;
}
```

---

### `src/Ecr.Domain/Entities/Documents/TableRow.cs`
MODULE: domain-documents | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Рядок таблиці документа.</summary>
/// <remarks>
/// <see cref="ModifiedAt"/> оновлюється **завжди** при зміні комірок цього
/// рядка — саме це піднімає <c>RowVersion</c>. Забути про це = зламати
/// оптимістичне блокування тихо (B04 §2.4).
/// </remarks>
public sealed class TableRow
{
    private TableRow() { }

    public TableRow(PeriodKey periodKey, long id, long tableInstanceId, RowKey rowKey, int ordinal, DateTime utcNow)
    {
        PeriodKeyValue = periodKey.Value;
        Id = id;
        TableInstanceId = tableInstanceId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        ModifiedAt = utcNow;
    }

    public int PeriodKeyValue { get; private set; }
    public long Id { get; private set; }
    public long TableInstanceId { get; private set; }
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Заповнене для <c>RowMode = Fixed</c>.</summary>
    public int? RowDefId { get; private set; }

    public int Ordinal { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// Рядок посилається на запис реєстру, який перестав бути чинним у цьому
    /// періоді (ФВ-8.13, <c>D-98</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Ознака <b>зберігається</b>, а не обчислюється при читанні: перерахунок
    /// на кожен зріз убив би бюджет 400 мс. Її ставить нічна перевірка
    /// інваріантів (ФВ-7.7) і перерахунок при зміні вікна дії запису реєстру.
    /// Читання осиротілий рядок <b>не</b> блокує, <c>Submit</c> — блокує
    /// (<c>ECR-SUB-4221</c>).
    /// </remarks>
    public bool IsOrphaned { get; private set; }

    /// <summary>Коли рядок став осиротілим; <c>null</c>, якщо не є.</summary>
    public DateTime? OrphanedAt { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public PeriodKey PeriodKey => new(PeriodKeyValue);
    public RowKey RowKey => ValueObjects.RowKey.Create(RowKeyValue);

    /// <summary>«Дотик» рядка при зміні його комірок.</summary>
    public void Touch(DateTime utcNow) => ModifiedAt = utcNow;

    /// <summary>
    /// Позначає рядок осиротілим або знімає позначку.
    /// </summary>
    /// <remarks>
    /// Викликається <c>OrphanScanJob</c> (Етап 4), а не шляхом читання:
    /// саме тому ознака є полем, а не обчисленням.
    /// </remarks>
    public void SetOrphaned(bool orphaned, DateTime utcNow)
    {
        IsOrphaned = orphaned;
        OrphanedAt = orphaned ? utcNow : null;
    }

    /// <summary>Логічне видалення рядка динамічної таблиці.</summary>
    public void SoftDelete(DateTime utcNow)
    {
        IsDeleted = true;
        ModifiedAt = utcNow;
    }
}
```

---

### `src/Ecr.Domain/Entities/Documents/CellValue.cs`
MODULE: domain-documents | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Значення комірки. Основний обсяг системи — ~108 млн рядків на рік.
/// </summary>
/// <remarks>
/// Порожні комірки **не матеріалізуються**: рядок існує тільки для заповненого
/// значення або для явної порожнечі (<c>IsEmpty = 1</c>). Це два різні стани,
/// і зводити їх один до одного не можна (R-B4).
/// </remarks>
public sealed class CellValue
{
    private CellValue() { }

    public CellValue(CellAddress address, int tableDefId, CellValueData data)
    {
        PeriodKeyValue = address.PeriodKey.Value;
        TableRowId = address.TableRowId;
        ColumnDefId = address.ColumnDefId;
        TableDefId = tableDefId;
        Apply(data);
    }

    public int PeriodKeyValue { get; private set; }
    public long TableRowId { get; private set; }
    public int ColumnDefId { get; private set; }

    /// <summary>Денормалізовано під складений FK: комірка не може потрапити в чужу колонку.</summary>
    public int TableDefId { get; private set; }

    public string? ValueString { get; private set; }
    public decimal? ValueNumeric { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }
    public int? ValueRegistryEntryId { get; private set; }

    /// <summary>Одиниця на рядок; лише для <c>DataType = Unit</c> (R-A4).</summary>
    public int? ValueUnitId { get; private set; }

    public bool IsCalculated { get; private set; }
    public bool IsEmpty { get; private set; }

    public CellAddress Address => new(new PeriodKey(PeriodKeyValue), TableRowId, ColumnDefId);

    /// <summary>Поточне значення як значеннєвий тип.</summary>
    public CellValueData ToData() => new()
    {
        ValueString = ValueString,
        ValueNumeric = ValueNumeric,
        ValueDate = ValueDate,
        ValueBool = ValueBool,
        ValueRegistryEntryId = ValueRegistryEntryId,
        ValueUnitId = ValueUnitId,
        IsCalculated = IsCalculated,
        IsEmpty = IsEmpty
    };

    /// <summary>Замінює значення.</summary>
    public void Apply(CellValueData data)
    {
        ValueString = data.ValueString;
        ValueNumeric = data.ValueNumeric;
        ValueDate = data.ValueDate;
        ValueBool = data.ValueBool;
        ValueRegistryEntryId = data.ValueRegistryEntryId;
        ValueUnitId = data.ValueUnitId;
        IsCalculated = data.IsCalculated;
        IsEmpty = data.IsEmpty;
    }
}
```

---

## 5. `Entities/Units`

### `src/Ecr.Domain/Entities/Units/Dimension.cs`
MODULE: domain-units | STAGE: 4

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Units;

/// <summary>
/// Розмірність величини. Конверсія можлива **лише в межах однієї
/// розмірності** — це те, що не дає щільності стати «конверсією» (ФВ-16.5).
/// </summary>
public sealed class Dimension
{
    private Dimension() { }

    public Dimension(byte id, EcrCode code, LocalizedText name)
    {
        Id = id;
        Code = code.Value;
        NameL10n = name;
    }

    public byte Id { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Канонічна одиниця розмірності.</summary>
    public int? BaseUnitId { get; private set; }

    /// <summary>Похідна = відношення двох розмірностей (<c>MassFlow = Mass / Time</c>).</summary>
    public bool IsDerived { get; private set; }

    public byte? NumeratorDimensionId { get; private set; }
    public byte? DenominatorDimensionId { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Units/Unit.cs`
MODULE: domain-units | STAGE: 4

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Units;

/// <summary>Одиниця вимірювання.</summary>
/// <remarks>
/// Похідні одиниці складаються **посиланнями** на чисельник і знаменник, а не
/// розбираються з рядка: повної алгебри розмірностей навмисно немає — вона не
/// потрібна для звітності й коштує дорого (ФВ-16.2).
/// </remarks>
public sealed class Unit
{
    private Unit() { }

    public Unit(EcrCode code, LocalizedText symbol, LocalizedText name, byte dimensionId,
                bool isBase, decimal factorToBase, decimal offsetToBase)
    {
        Code = code.Value;
        SymbolL10n = symbol;
        NameL10n = name;
        DimensionId = dimensionId;
        IsBase = isBase;
        FactorToBase = factorToBase;
        OffsetToBase = offsetToBase;
        IsActive = true;
    }

    public int Id { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText SymbolL10n { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public byte DimensionId { get; private set; }
    public bool IsBase { get; private set; }

    /// <summary>Множник переходу до базової одиниці розмірності.</summary>
    public decimal FactorToBase { get; private set; }

    /// <summary>Зсув; потрібен лише для температури (<c>°C → K</c>).</summary>
    public decimal OffsetToBase { get; private set; }

    public int? NumeratorUnitId { get; private set; }
    public int? DenominatorUnitId { get; private set; }
    public string? DisplayFormat { get; private set; }
    public bool IsActive { get; private set; }
}
```

---

### `src/Ecr.Domain/Entities/Units/UnitConversion.cs`
MODULE: domain-units | STAGE: 4

```csharp
namespace Ecr.Domain.Entities.Units;

/// <summary>
/// Явна конверсія: виняток із маршруту через базову одиницю.
/// </summary>
/// <remarks>
/// ⛔ Контекстні коефіцієнти (щільність, теплотворність, молярна маса) сюди
/// **не потрапляють**: вони залежать від речовини й умов і змінюються з часом,
/// тому належать до <c>calc.MethodologyConstant</c>. На рівні БД це
/// забезпечує <c>CK_Conv_SameDimension</c> (ФВ-16.5).
/// </remarks>
public sealed class UnitConversion
{
    private UnitConversion() { }

    public UnitConversion(int fromUnitId, int toUnitId, decimal factor, decimal offset, byte kind, string? note)
    {
        FromUnitId = fromUnitId;
        ToUnitId = toUnitId;
        Factor = factor;
        Offset = offset;
        Kind = kind;
        Note = note;
    }

    public int Id { get; private set; }
    public int FromUnitId { get; private set; }
    public int ToUnitId { get; private set; }
    public decimal Factor { get; private set; }
    public decimal Offset { get; private set; }

    /// <summary>0 <c>Exact</c> — заміщає маршрут через базу; 1 <c>LegacyPinned</c> — заради сумісності чисел.</summary>
    public byte Kind { get; private set; }

    /// <summary>Обов'язковий для <c>LegacyPinned</c>: звідки взято коефіцієнт.</summary>
    public string? Note { get; private set; }
}
```

---

## 6. Доменні сервіси

### `src/Ecr.Domain/Services/UnitConverter.cs`
MODULE: domain-units | STAGE: 4
CONTRACT: 02b-expressions.md#convert
SCOPE: маршрут конверсії — чотири кроки, без винятків.
NOT IN SCOPE: контекстні коефіцієнти; звернення до БД.

```csharp
using Ecr.Domain.Entities.Units;

namespace Ecr.Domain.Services;

/// <summary>
/// Конверсія одиниць. **Неявних конверсій не буває** (D-74): цей сервіс
/// викликається лише там, де у виразі написано <c>CONVERT</c> або задано
/// мапінг <c>SourceUnit → TargetUnit</c>.
/// </summary>
public sealed class UnitConverter
{
    /// <summary>
    /// Виконує конверсію за маршрутом: тотожність → явна конверсія →
    /// через базову одиницю → помилка.
    /// </summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="from">Вихідна одиниця.</param>
    /// <param name="to">Цільова одиниця.</param>
    /// <param name="explicitConversion">Явна конверсія, якщо вона є в <c>uom.Conversion</c>.</param>
    /// <returns>Значення в цільовій одиниці.</returns>
    /// <exception cref="Abstractions.DomainException">
    /// Різні розмірності — <c>ECR-UOM-0422</c>. Це відмова, а не спроба вгадати.
    /// </exception>
    public decimal Convert(decimal value, Unit from, Unit to, UnitConversion? explicitConversion)
        => throw new NotImplementedException(
            "TODO: 1) from.Id == to.Id → value; " +
            "2) explicitConversion != null → value * Factor + Offset; " +
            "3) from.DimensionId == to.DimensionId → base = value * from.FactorToBase + from.OffsetToBase, " +
            "   result = (base - to.OffsetToBase) / to.FactorToBase; " +
            "4) інакше DomainException('ECR-UOM-0422'). " +
            "Усі обчислення в decimal — float заборонений (D-30).");

    /// <summary>Чи можлива конверсія без явного правила.</summary>
    public bool CanConvert(Unit from, Unit to) => from.DimensionId == to.DimensionId;
}
```

---

### `src/Ecr.Domain/Services/PeriodStateCalculator.cs`
MODULE: domain-periods | STAGE: 3

```csharp
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Обчислює стан періоду за offsets. Викликається <c>PeriodStateJob</c>,
/// а не запитом: стан періоду — збережене значення, а не функція від
/// <c>now()</c> (ФВ-1.12).
/// </summary>
public sealed class PeriodStateCalculator
{
    /// <summary>
    /// Визначає, яким має бути стан періоду на вказаний момент.
    /// </summary>
    /// <param name="period">Період із обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент у UTC (з <c>IClock</c>).</param>
    /// <param name="siteTimeZone">Пояс майданчика: межі — саме в ньому (D-68).</param>
    public PeriodState Calculate(Period period, DateTime utcNow, TimeZoneInfo siteTimeZone)
        => throw new NotImplementedException(
            "TODO: якщо ReopenedUntil > utcNow → Grace; " +
            "utcNow < ComputedOpenAt → Scheduled; " +
            "utcNow <= кінець періоду → Open; " +
            "utcNow <= ComputedCloseAt → Grace; інакше Closed. " +
            "Порівняння в UTC, але межі вже обчислені з поясу майданчика — " +
            "повторно конвертувати не треба.");

    /// <summary>
    /// Обирає поточний період проєкту в режимі <c>Auto</c>: найраніший
    /// <c>Open</c>, інакше найпізніший <c>Grace</c>, інакше нічого (D-77).
    /// </summary>
    public Period? SelectCurrentPeriod(IReadOnlyList<Period> periods)
        => throw new NotImplementedException(
            "TODO: спершу шукати State == Open з мінімальним PeriodKeyValue; " +
            "якщо немає — State == Grace з максимальним; інакше null.");
}
```

---

### `src/Ecr.Domain/Services/ChangeClassifier.cs`
MODULE: domain-metadata | STAGE: 1

```csharp
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Класифікує структурну зміну. Від класу залежить, чи дозволена вона взагалі
/// і чи потрібна міграція документів (ФВ-7.4).
/// </summary>
public sealed class ChangeClassifier
{
    /// <summary>Поля презентаційного шару — їх можна міняти в опублікованій версії.</summary>
    public static readonly IReadOnlySet<string> PresentationFields = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Entities.Configuration.ColumnDef.HeaderL10n),
        nameof(Entities.Configuration.ColumnDef.Ordinal),
        nameof(Entities.Configuration.ColumnDef.DisplayFormat),
        nameof(Entities.Configuration.ColumnDef.IsHidden),
        nameof(Entities.Configuration.ColumnDef.StyleId),
        nameof(Entities.Configuration.RowDef.LabelL10n),
        nameof(Entities.Configuration.SheetDef.NameL10n),
        nameof(Entities.Configuration.SheetDef.IsVisible),
        nameof(Entities.Configuration.TableDef.NameL10n)
    };

    /// <summary>Класифікує зміну поля сутності.</summary>
    /// <param name="entityType">Тип сутності (<c>ColumnDef</c>, <c>RowDef</c>…).</param>
    /// <param name="fieldName">Назва поля.</param>
    /// <param name="hasDocuments">Чи існують документи на цій версії.</param>
    public ChangeClass Classify(string entityType, string fieldName, bool hasDocuments)
        => throw new NotImplementedException(
            "TODO: якщо fieldName у PresentationFields → Presentation; " +
            "додавання нової сутності → Safe; " +
            "зміна DataType/Precision/UnitId/LookupRegistryDefId → Guarded; " +
            "видалення колонки/рядка або зміна Code/RowKey при hasDocuments → Breaking. " +
            "Breaking у версії з документами = відмова операції, не попередження (ФВ-7.4).");
}
```

---

## 7. Решта сутностей

Усі наведені **повністю** в §8 нижче. Таблиця лишається як покажчик
«файл → таблиця БД → що в ньому нетипового»; поля звіряються з
[`02a-db-schema.md`](02a-db-schema.md), імена — з
[`03-glossary.md`](03-glossary.md).

| Файл | Схема-джерело | Особливості |
|---|---|---|
| `Entities/Dictionaries/RegistryEntry.cs` | `dic.RegistryEntry` | темпоральність `ValidFrom`/`ValidTo`, soft delete, метод `IsValidOn(DateOnly)` |
| `Entities/Dictionaries/RegistryValue.cs` | `dic.RegistryValue` | одне значення поля; типізовані колонки |
| `Entities/Dictionaries/RegistryEntryLink.cs` | `dic.RegistryEntryLink` | M:N із `PayloadJson` |
| `Entities/Dictionaries/RegistryExternalKey.cs` | `dic.RegistryExternalKey` | GUID зберігається **після** зіставлення за бізнес-ключем |
| `Entities/Calculations/Methodology.cs` | `calc.Methodology` | контейнер версій |
| `Entities/Calculations/MethodologyVersion.cs` | `calc.MethodologyVersion` | `NumericMode`, `CalendarMode`, `TraceLevel`; метод `Publish` перевіряє **чотири очі** (D-40) |
| `Entities/Calculations/MethodologyFormula.cs` | `calc.MethodologyFormula` | `EvaluationOrder` — обчислюваний, не введений |
| `Entities/Calculations/MethodologyConstant.cs` | `calc.MethodologyConstant` | `UnitId` обов'язковий; темпоральність |
| `Entities/Calculations/MethodologySubstance.cs` | `calc.MethodologySubstance` | |
| `Entities/Calculations/MethodologyOutput.cs` | `calc.MethodologyOutput` | `UnitId` обов'язковий |
| `Entities/Calculations/MethodologyRule.cs` | `calc.MethodologyRule` | прив'язка правилами, не списком |
| `Entities/Calculations/ScriptVersion.cs` | `calc.ScriptVersion` | `HasGreenTest` — без нього публікація неможлива |
| `Entities/Calculations/CalculationRun.cs` | `calc.CalculationRun` | `ModulesProfileJson` — профіль для J-1 |
| `Entities/Calculations/CalculationResult.cs` | `calc.CalculationResult` | `decimal(28,10)`; складений ключ із `PeriodKey` |
| `Entities/Calculations/SubmissionSnapshot.cs` | `calc.SubmissionSnapshot` | іммутабельний; доказова база (ФВ-5.7) |
| `Entities/Security/User.cs` | `sec.User` | `WindowsSid` **або** `PasswordHash`, не обидва; `SecurityStamp` |
| `Entities/Security/Role.cs` | `sec.Role` | |
| `Entities/Security/Permission.cs` | `sec.Permission` | `IsDangerous` |
| `Entities/Security/RoleAssignment.cs` | `sec.RoleAssignment` | `ScopeJson` |
| `Entities/Security/ResourceGrant.cs` | `sec.ResourceGrant` | `IsDeny` **виграє завжди** |
| `Entities/Security/PasswordPolicy.cs` | `sec.PasswordPolicy` | |
| `Entities/Workflow/ApprovalRoute.cs` | `wf.ApprovalRoute` | |
| `Entities/Workflow/ApprovalStep.cs` | `wf.ApprovalStep` | |
| `Entities/Workflow/ApprovalState.cs` | `wf.ApprovalState` | ключ **аркуш × період**; `Reopen` вимагає причини |
| `Entities/External/DataSource.cs` | `ext.DataSource` | `SecretName` — **лише ім'я** |
| `Entities/External/SourceEntity.cs` | `ext.SourceEntity` | `SourceKind` |
| `Entities/External/EntityFieldMap.cs` | `ext.EntityFieldMap` | `SourceUnitId`/`TargetUnitId` |
| `Entities/External/CollectionSchedule.cs` | `ext.CollectionSchedule` | `Watermark` — оптимізація, не стан |

---

## 8. Решта сутностей — повний текст

> Двадцять вісім файлів. Спільні правила ті самі, що вище: приватний
> конструктор для EF, публічний із обов'язковими полями, `private set`,
> XML-doc українською, методи зміни стану з `NotImplementedException` і
> змістовним TODO. Кожен файл наводиться під власним заголовком, щоб
> Етап 0 мав що копіювати, а `Definition of Done` було перевірним.

### `src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs`
MODULE: domain-dictionaries | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Запис довідника. **Темпоральний**: чинність задається вікном
/// <see cref="ValidFrom"/>…<see cref="ValidTo"/>, і саме вона визначає, чи
/// можна обрати запис у періоді (ФВ-8.5).
/// </summary>
/// <remarks>
/// Фізично не видаляється ніколи, поки на нього посилаються дані (ФВ-8.6,
/// <c>ECR-REG-0409</c>). Перейменування не змінює історію, бо в комірці лежить
/// <c>Id</c>, а не текст (ФВ-8.8).
/// </remarks>
public sealed class RegistryEntry : Entity<long>
{
    private RegistryEntry() { }

    public RegistryEntry(int registryDefId, EcrCode code, LocalizedText display)
    {
        RegistryDefId = registryDefId;
        Code = code.Value;
        DisplayL10n = display;
        IsActive = true;
    }

    public int RegistryDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText DisplayL10n { get; private set; } = null!;

    /// <summary>Початок вікна чинності; <c>null</c> — «завжди від початку».</summary>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Кінець вікна; <c>null</c> — «без обмеження».</summary>
    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }
    public long? ParentEntryId { get; private set; }

    /// <summary>Чинний на дату. Межі **включні** з обох боків.</summary>
    public bool IsValidOn(DateOnly date)
        => (ValidFrom is null || date >= ValidFrom)
        && (ValidTo   is null || date <= ValidTo);

    /// <summary>
    /// Змінює вікно чинності. Викликає перерахунок <c>IsOrphaned</c> на рядках,
    /// що посилаються на цей запис (ФВ-8.13a) — але **не тут**: сутність не
    /// знає про документи. Цим займається <c>SetEntryValidityHandler</c>.
    /// </summary>
    public void SetValidity(DateOnly? from, DateOnly? to)
        => throw new NotImplementedException(
            "TODO: перевірити from <= to, записати межі, підняти ModifiedAt. " +
            "Звуження вікна може осиротити рядки, розширення — повернути їх; " +
            "обидва напрямки обробляє IOrphanScanner.RescanForEntryAsync (D-98).");

    /// <summary>Логічне видалення: фізичне заборонене при посиланнях (ФВ-8.6).</summary>
    public void SoftDelete()
        => throw new NotImplementedException(
            "TODO: IsDeleted = true, IsActive = false. Перевірку посилань робить " +
            "use-case через ICellStore — сутність про дані не знає.");
}
```

### `src/Ecr.Domain/Entities/Dictionaries/RegistryValue.cs`
MODULE: domain-dictionaries | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Dictionaries/RegistryValue.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Значення одного поля запису довідника. Колонки **типізовані**, а не одне
/// текстове поле: інакше повернулася б та сама проблема, що з
/// <c>Attribute_XXXX</c> — тип відомий лише за домовленістю.
/// </summary>
public sealed class RegistryValue : Entity<long>
{
    private RegistryValue() { }

    public RegistryValue(long registryEntryId, int registryFieldDefId)
    {
        RegistryEntryId = registryEntryId;
        RegistryFieldDefId = registryFieldDefId;
    }

    public long RegistryEntryId { get; private set; }
    public int RegistryFieldDefId { get; private set; }

    public decimal? ValueDecimal { get; private set; }
    public string? ValueString { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }
    public long? ValueRegistryEntryId { get; private set; }

    /// <summary>Одиниця значення, якщо поле її має (ФВ-16.1).</summary>
    public int? UnitId { get; private set; }

    /// <summary>Записує значення відповідно до типу поля.</summary>
    public void Set(object? value, int? unitId)
        => throw new NotImplementedException(
            "TODO: обрати колонку за RegistryFieldDef.DataType; решту занулити — " +
            "заповнені дві колонки означають, що тип змінили і не прибрали старе. " +
            "unitId приймати лише для полів з оголошеною розмірністю.");
}
```

### `src/Ecr.Domain/Entities/Dictionaries/RegistryEntryLink.cs`
MODULE: domain-dictionaries | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Dictionaries/RegistryEntryLink.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Зв'язок M:N між записами довідників (ФВ-8.4). <see cref="PayloadJson"/>
/// тримає атрибути самого зв'язку — наприклад частку або пріоритет, — щоб не
/// заводити окрему сутність під кожен вид відношення.
/// </summary>
public sealed class RegistryEntryLink : Entity<long>
{
    private RegistryEntryLink() { }

    public RegistryEntryLink(int relationDefId, long fromEntryId, long toEntryId)
    {
        RegistryRelationDefId = relationDefId;
        FromEntryId = fromEntryId;
        ToEntryId = toEntryId;
    }

    public int RegistryRelationDefId { get; private set; }
    public long FromEntryId { get; private set; }
    public long ToEntryId { get; private set; }

    /// <summary>Атрибути зв'язку. Схема — за `RegistryRelationDef`.</summary>
    public string? PayloadJson { get; private set; }

    public void SetPayload(string? json)
        => throw new NotImplementedException(
            "TODO: валідувати JSON за схемою з RegistryRelationDef; невалідний — " +
            "помилка, а не мовчазне збереження рядка.");
}
```

### `src/Ecr.Domain/Entities/Dictionaries/RegistryExternalKey.cs`
MODULE: domain-dictionaries | STAGE: 5

```csharp
// src/Ecr.Domain/Entities/Dictionaries/RegistryExternalKey.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Зовнішній ідентифікатор запису довідника (ФВ-8.10): GUID-и, розкидані
/// сьогодні по клітинках конфігурації, стають іменованим полем.
/// </summary>
/// <remarks>
/// ⚠ Зіставлення при міграції йде **за бізнес-ключем**, а GUID зберігається
/// лише як наслідок (ФВ-11.6). Зворотний порядок — зіставити за GUID —
/// виглядає надійніше, але прив'язує нашу історію до ідентифікаторів системи,
/// яку ми виводимо з експлуатації.
/// </remarks>
public sealed class RegistryExternalKey : Entity<long>
{
    private RegistryExternalKey() { }

    public RegistryExternalKey(long registryEntryId, string systemCode, string externalId)
    {
        RegistryEntryId = registryEntryId;
        SystemCode = systemCode;
        ExternalId = externalId;
    }

    public long RegistryEntryId { get; private set; }

    /// <summary>Яка зовнішня система: <c>PiAf</c>, <c>Flert</c>.</summary>
    public string SystemCode { get; private set; } = null!;

    public string ExternalId { get; private set; } = null!;
}
```

### `src/Ecr.Domain/Entities/Calculations/Methodology.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/Methodology.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Методологія розрахунку — **контейнер версій**, а не сама формула
/// (ФВ-9.1). Обчислює завжди конкретна версія, обрана за датою періоду.
/// </summary>
public sealed class Methodology : Entity<int>
{
    private readonly List<MethodologyVersion> _versions = [];

    private Methodology() { }

    public Methodology(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public IReadOnlyList<MethodologyVersion> Versions => _versions;

    /// <summary>Версія, чинна на дату звітного періоду (ФВ-9.3).</summary>
    public MethodologyVersion? VersionOn(DateOnly periodDate)
        => throw new NotImplementedException(
            "TODO: обрати опубліковану версію, чиє вікно дії містить дату. " +
            "Вікна опублікованих версій НЕ перетинаються (ФВ-13.3), тому " +
            "збігів бути не може; якщо їх два — це помилка даних, а не привід " +
            "узяти першу.");
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologyVersion.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologyVersion.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Версія методології — те, що реально рахує. Опублікована **незмінна**;
/// зміна — клон плюс нове вікно дії.
/// </summary>
/// <remarks>
/// Три режими тут визначають числа, і жоден не є технічною дрібницею:
/// <see cref="NumericMode"/> — момент округлення (ФВ-9.9),
/// <see cref="CalendarMode"/> — тривалість періоду (ФВ-16.11),
/// <see cref="TraceLevel"/> — обсяг журналу (ФВ-9.13).
/// Перші два **обов'язкові в diff при публікації**: їх зміна тихо змінює
/// всі результати.
/// </remarks>
public sealed class MethodologyVersion : Entity<int>
{
    private MethodologyVersion() { }

    public MethodologyVersion(int methodologyId, string versionNumber, DateOnly effectiveFrom)
    {
        MethodologyId = methodologyId;
        VersionNumber = versionNumber;
        EffectiveFrom = effectiveFrom;
        NumericMode = NumericMode.Legacy;
        CalendarMode = CalendarMode.Actual;
        TraceLevel = TraceLevel.ErrorsOnly;
        Status = TemplateVersionStatus.Draft;
    }

    public int MethodologyId { get; private set; }
    public string VersionNumber { get; private set; } = null!;
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public TemplateVersionStatus Status { get; private set; }

    /// <summary>Арифметика: <c>Legacy</c> відтворює числа чинної системи (ФВ-9.9).</summary>
    public NumericMode NumericMode { get; private set; }

    /// <summary>Джерело тривалості періоду (ФВ-16.11). Не зберігається на періоді (D-112).</summary>
    public CalendarMode CalendarMode { get; private set; }

    public TraceLevel TraceLevel { get; private set; }

    public int? LastEditedByUserId { get; private set; }
    public int? PublishedByUserId { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public string? ChangeReason { get; private set; }

    /// <summary>
    /// Публікація. **Чотири очі** (D-40): публікувати власну останню правку
    /// заборонено системно, а не інструкцією.
    /// </summary>
    public void Publish(int publishedByUserId, string changeReason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: 1) Status має бути Draft;\n" +
            "2) publishedByUserId != LastEditedByUserId, інакше ECR-CALC-0409 (D-40);\n" +
            "3) changeReason обов'язковий і непорожній (ФВ-14.7);\n" +
            "4) вікно дії не перетинається з іншими опублікованими версіями (ФВ-13.3);\n" +
            "5) зелений тест обов'язковий, інакше ECR-CALC-0422 (ФВ-9.12);\n" +
            "6) Status = Published, зафіксувати автора і час. " +
            "Перевірку diff результатів робить use-case: сутність не має доступу до даних.");
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Формула методології. <see cref="EvaluationOrder"/> — **обчислюваний**, а не
/// введений: порядок топологічний і рахується при публікації (ФВ-9.4).
/// </summary>
/// <remarks>
/// Дозволити людині задати порядок руками означало б, що додана формула тихо
/// зміщує решту, а помилка виявиться числом у звіті, не помилкою публікації.
/// </remarks>
public sealed class MethodologyFormula : Entity<int>
{
    private MethodologyFormula() { }

    public MethodologyFormula(int methodologyVersionId, EcrCode code, string expression)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Expression = expression;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Expression { get; private set; } = null!;

    /// <summary>Оголошений список аргументів; токен поза ним — помилка публікації.</summary>
    public string? ArgumentsJson { get; private set; }

    /// <summary>Топологічний порядок. Заповнюється при `Publish`, не користувачем.</summary>
    public int EvaluationOrder { get; private set; }

    public int? OutputUnitId { get; private set; }

    /// <summary>Проставляє порядок, отриманий із графа залежностей.</summary>
    public void SetEvaluationOrder(int order)
        => throw new NotImplementedException(
            "TODO: присвоїти порядок; викликається ЛИШЕ з процедури публікації " +
            "після топологічного сортування. Виклик ззовні — помилка проєктування.");
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologyConstant.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologyConstant.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Константа методології: щільність, теплотворність, молярна маса, коефіцієнт
/// емісії. **Одиниця обов'язкова** (ФВ-16.1).
/// </summary>
/// <remarks>
/// ⚠ Саме тут живуть **контекстні коефіцієнти**, а не в <c>uom.Conversion</c>
/// (ФВ-16.5, D-75). Щільність води — не конверсія «м³ → кг»: вона залежить від
/// температури, а для нафти взагалі інша. Спроба покласти таке в таблицю
/// конверсій відхиляється базою (<c>ECR-UOM-4221</c>).
/// </remarks>
public sealed class MethodologyConstant : Entity<int>
{
    private MethodologyConstant() { }

    public MethodologyConstant(int methodologyVersionId, EcrCode code, decimal value, int unitId)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Value = value;
        UnitId = unitId;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Зберігається типізовано (<c>decimal</c>), не текстом.</summary>
    public decimal Value { get; private set; }

    /// <summary>Одиниця. Без неї константа не має сенсу в перевірці розмірностей.</summary>
    public int UnitId { get; private set; }

    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public long? SubstanceEntryId { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologySubstance.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologySubstance.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Речовина, яку рахує версія методології. Сама речовина — запис довідника;
/// тут лише прив'язка і параметри, специфічні для цього розрахунку.
/// </summary>
public sealed class MethodologySubstance : Entity<int>
{
    private MethodologySubstance() { }

    public MethodologySubstance(int methodologyVersionId, long substanceEntryId)
    {
        MethodologyVersionId = methodologyVersionId;
        SubstanceEntryId = substanceEntryId;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Посилання на `dic.RegistryEntry`, а не текстова назва (ФВ-8.8).</summary>
    public long SubstanceEntryId { get; private set; }

    public int Ordinal { get; private set; }
    public bool IsActive { get; private set; } = true;
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologyOutput.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologyOutput.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Оголошений вихід методології: що саме вона повертає і **в якій одиниці**.
/// Одиниця обов'язкова — на ній тримається перевірка розмірностей при
/// публікації (ФВ-16.6).
/// </summary>
public sealed class MethodologyOutput : Entity<int>
{
    private MethodologyOutput() { }

    public MethodologyOutput(int methodologyVersionId, EcrCode code, int unitId)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        UnitId = unitId;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Одиниця результату. Несумісна з формулою → відмова публікації.</summary>
    public int UnitId { get; private set; }

    /// <summary>Формула, що дає цей вихід.</summary>
    public int? MethodologyFormulaId { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Правило прив'язки методології до рядків документа (ФВ-13.3, ФВ-13.8):
/// **умова**, а не жорсткий список <c>RowKey</c>.
/// </summary>
/// <remarks>
/// Правила впорядковані за <see cref="Priority"/>, **перший збіг виграє**
/// (ФВ-13.4). На тому самому механізмі будується матриця покриття: рядок, який
/// не зачепило жодне правило, видно до публікації, а не за розбіжністю в звіті.
/// </remarks>
public sealed class MethodologyRule : Entity<int>
{
    private MethodologyRule() { }

    public MethodologyRule(int methodologyVersionId, string conditionExpression, int priority)
    {
        MethodologyVersionId = methodologyVersionId;
        ConditionExpression = conditionExpression;
        Priority = priority;
        IsActive = true;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Умова діалекту методологій; посилається на реєстри й атрибути.</summary>
    public string ConditionExpression { get; private set; } = null!;

    /// <summary>Менше значення — вищий пріоритет. Перший збіг виграє.</summary>
    public int Priority { get; private set; }

    public bool IsActive { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Calculations/ScriptVersion.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/ScriptVersion.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Версія скрипта рівня 2. **Виконання в першому релізі не будується**
/// (D-105): сутність існує, щоб модель була повною і щоб додавання рівня 2
/// пізніше не ламало схему, але рушія до неї немає.
/// </summary>
/// <remarks>
/// <see cref="HasGreenTest"/> — умова публікації (ФВ-9.12, ФВ-13.7): без
/// зеленого тесту версія не публікується, і це перевіряється, а не мається на
/// увазі.
/// </remarks>
public sealed class ScriptVersion : Entity<int>
{
    private ScriptVersion() { }

    public ScriptVersion(int methodologyVersionId, string sourceCode)
    {
        MethodologyVersionId = methodologyVersionId;
        SourceCode = sourceCode;
        Status = TemplateVersionStatus.Draft;
    }

    public int MethodologyVersionId { get; private set; }
    public string SourceCode { get; private set; } = null!;
    public TemplateVersionStatus Status { get; private set; }

    /// <summary>Чи пройшов останній прогін тестів. Без цього публікація неможлива.</summary>
    public bool HasGreenTest { get; private set; }

    public DateTime? LastTestedAt { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Calculations/CalculationRun.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/CalculationRun.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Прогін розрахунку. Одиниця відтворюваності: результат завжди належить
/// конкретному прогону, а не «поточному стану» (ФВ-9.11).
/// </summary>
/// <remarks>
/// ⚠ Окремого поля «режим» тут **немає**, і це навмисно: три режими `ФВ-12.1`
/// виводяться з двох nullable-полів, тому їх неможливо виставити суперечливо.
/// <list type="bullet">
///   <item><see cref="PeriodKey"/> = <c>null</c> → **плановий повний** (річний);</item>
///   <item><see cref="PeriodKey"/> задано → **інкрементний** за подією;</item>
///   <item><see cref="TriggeredByUserId"/> задано → **ручний**, інакше за розкладом.</item>
/// </list>
/// Окреме поле режиму дозволило б запис «повний прогін одного періоду», який
/// нічого не означає.
/// </remarks>
public sealed class CalculationRun : Entity<long>
{
    private CalculationRun() { }

    public CalculationRun(int projectId, int? periodKey, int? triggeredByUserId, DateTime utcNow)
    {
        ProjectId = projectId;
        PeriodKey = periodKey;
        TriggeredByUserId = triggeredByUserId;
        StartedAt = utcNow;
        Status = "Running";
    }

    public int ProjectId { get; private set; }

    /// <summary><c>null</c> — повний рік; інакше конкретний період.</summary>
    public int? PeriodKey { get; private set; }

    /// <summary><c>null</c> — запуск за розкладом, не людиною.</summary>
    public int? TriggeredByUserId { get; private set; }

    /// <summary>Рядок, а не enum — за DDL (`nvarchar(32)`).</summary>
    public string Status { get; private set; } = null!;

    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    /// <summary>Текст помилки при <c>Failed</c>; порожній при успіху.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Профіль по модулях: скільки тривав кожен. Це не діагностика заради
    /// діагностики — саме з нього видно, звідки брати різницю між 20 і 10
    /// хвилинами річного перерахунку (`J-1`, ПРД-13).
    /// </summary>
    public string? ModulesProfileJson { get; private set; }

    public void Complete(string status, DateTime utcNow, string? profileJson, string? errorMessage)
        => throw new NotImplementedException(
            "TODO: зафіксувати статус ('Succeeded' | 'Failed'), час завершення, " +
            "профіль по модулях і текст помилки. " +
            "Перемикання IsCurrent на результатах — ОКРЕМА транзакція в use-case " +
            "(ФВ-9.11), тут його немає: сутність не бачить інших прогонів.");
}
```

### `src/Ecr.Domain/Entities/Calculations/CalculationResult.cs`
MODULE: domain-calculations | STAGE: 4

```csharp
// src/Ecr.Domain/Entities/Calculations/CalculationResult.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Результат розрахунку. **Не потрапляє в `doc.CellValue`** (ФВ-9.12, D-69):
/// у документ він приходить посиланням через <c>cfg.CalculationBinding</c>.
/// </summary>
/// <remarks>
/// Тип — <c>decimal(28,10)</c>, ніколи <c>float</c> (ФВ-9.11): на мільйонах
/// рядків подвійна точність дає розбіжність, яку неможливо пояснити методологу.
/// </remarks>
public sealed class CalculationResult : Entity<long>
{
    private CalculationResult() { }

    public CalculationResult(long runId, int methodologyVersionId, int periodKey, string outputCode, decimal value, int unitId)
    {
        CalculationRunId = runId;
        MethodologyVersionId = methodologyVersionId;
        PeriodKey = periodKey;
        OutputCode = outputCode;
        Value = value;
        UnitId = unitId;
    }

    public long CalculationRunId { get; private set; }

    /// <summary>Версія, що дала число. Без неї результат неможливо пояснити.</summary>
    public int MethodologyVersionId { get; private set; }

    public int PeriodKey { get; private set; }
    public string OutputCode { get; private set; } = null!;
    public decimal Value { get; private set; }
    public int UnitId { get; private set; }
    public long? SubstanceEntryId { get; private set; }
    public long? SourceRowId { get; private set; }

    /// <summary>Чи це актуальний прогін. Перемикається однією транзакцією.</summary>
    public bool IsCurrent { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Calculations/SubmissionSnapshot.cs`
MODULE: domain-calculations | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Calculations/SubmissionSnapshot.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Іммутабельний зліпок вхідних значень на момент подання (ФВ-5.7).
/// </summary>
/// <remarks>
/// **Саме він, а не трейс, є доказовою базою** (ЗБР-3). Трейс показує, як
/// рахували; зліпок показує, що саме рахували. Через п'ять років на питання
/// «звідки ця цифра» відповідає він — незалежно від того, що відбулося з
/// довідниками, методологіями і самими даними потім.
/// <para>
/// Не редагується ніколи. `Reopen` створює **новий** зліпок, старий лишається
/// (D-67, ФВ-9.17).
/// </para>
/// </remarks>
public sealed class SubmissionSnapshot : Entity<long>
{
    private SubmissionSnapshot() { }

    public SubmissionSnapshot(long documentId, int sheetDefId, int periodKey, int submittedByUserId, DateTime utcNow)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        SubmittedByUserId = submittedByUserId;
        SubmittedAt = utcNow;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public int PeriodKey { get; private set; }
    public int SubmittedByUserId { get; private set; }
    public DateTime SubmittedAt { get; private set; }

    /// <summary>Зліпок значень. Формат — за `02a`; стискається при записі.</summary>
    public byte[] PayloadCompressed { get; private set; } = [];

    /// <summary>Версії, чинні на момент подання: шаблон, методології, реєстри.</summary>
    public string VersionsJson { get; private set; } = null!;

    /// <summary>Контрольна сума — доводить, що зліпок не змінювався.</summary>
    public string Checksum { get; private set; } = null!;
}
```

### `src/Ecr.Domain/Entities/Security/User.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/User.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Користувач. Один із двох провайдерів, але **одна ідентичність застосунку**
/// (ФВ-6.1, ФВ-6.2): нижче рівня входу різниці немає ніде.
/// </summary>
/// <remarks>
/// <see cref="WindowsSid"/> **або** <see cref="PasswordHash"/>, ніколи обидва —
/// це перевіряє <c>CK_User_Provider</c> у базі. Саме тому автором дії в аудиті
/// є <c>Id</c>, а не SID: у локального користувача SID не існує (D-37, D-86).
/// </remarks>
public sealed class User : Entity<int>
{
    private User() { }

    public User(string userName, string displayName, AuthProvider provider)
    {
        UserName = userName;
        DisplayName = displayName;
        Provider = provider;
        SecurityStamp = Guid.NewGuid().ToString("N");
        IsActive = true;
    }

    public string UserName { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string? Email { get; private set; }
    public AuthProvider Provider { get; private set; }

    /// <summary>Лише для доменних. Це **не** авторство, а зіставлення з каталогом.</summary>
    public string? WindowsSid { get; private set; }

    public string? PasswordHash { get; private set; }
    public int? PasswordPolicyId { get; private set; }

    /// <summary>Перевіряється на КОЖЕН запит: відкликання прав діє негайно (ФВ-6.7).</summary>
    public string SecurityStamp { get; private set; } = null!;

    public int FailedAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    /// <summary>Пароль виданий разово; доки прапорець стоїть — лише зміна пароля і вихід (ФВ-6.18).</summary>
    public bool MustChangePassword { get; private set; }

    /// <summary>Технічний запис первинного налаштування (D-97, D-115). Один на систему.</summary>
    public bool IsBootstrapAdmin { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Змінює пароль і **обов'язково** крутить <c>SecurityStamp</c>.</summary>
    public void SetPassword(string passwordHash)
        => throw new NotImplementedException(
            "TODO: PasswordHash = hash; MustChangePassword = false; " +
            "SecurityStamp = новий GUID — інакше старі сесії лишаться дійсними " +
            "після зміни пароля, і це буде тихою дірою (ФВ-6.7).");

    /// <summary>Вимикає bootstrap-запис. **Не видаляє**: він потрібен в аудиті.</summary>
    public void DisableAsBootstrap()
        => throw new NotImplementedException(
            "TODO: IsActive = false, SecurityStamp = новий. IsBootstrapAdmin " +
            "лишити як є — за ним потім видно, звідки взявся перший адміністратор.");
}
```

### `src/Ecr.Domain/Entities/Security/Role.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/Role.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Роль — **дані, а не enum** (ФВ-6.6). Адміністратор створює роль і набирає
/// їй права; новий обов'язок не потребує релізу.
/// </summary>
public sealed class Role : Entity<int>
{
    private Role() { }

    public Role(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Вбудована роль із seed. Видаленню не підлягає, зміні — так.</summary>
    public bool IsBuiltIn { get; private set; }

    public bool IsActive { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Security/Permission.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/Permission.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Функціональне право. Каталог фіксований і приходить із seed — на відміну
/// від ролей, права оголошує код, бо саме він їх перевіряє.
/// </summary>
public sealed class Permission : Entity<string>
{
    private Permission() { }

    public Permission(string code, string group, bool isDangerous)
    {
        Id = code;
        Group = group;
        IsDangerous = isDangerous;
    }

    /// <summary>Група для UI: `Template`, `Document`, `Calculation`, `Security`.</summary>
    public string Group { get; private set; } = null!;

    /// <summary>
    /// Небезпечне право (ФВ-6.12, D-40): у складені ролі **не входить**, seed
    /// лишає ролі порожніми за ним навмисно. Це не пропуск конфігурації.
    /// </summary>
    public bool IsDangerous { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Security/RoleAssignment.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/RoleAssignment.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Призначення ролі. Основний спосіб для доменних користувачів — **на
/// AD-групу** (ФВ-6.15), для локальних — на користувача.
/// </summary>
/// <remarks>
/// Членство в групі береться з токена входу, а не запитом до каталогу на
/// кожну перевірку (ФВ-6.15a): недоступність каталогу не має розривати сеанс
/// уже автентифікованого користувача.
/// </remarks>
public sealed class RoleAssignment : Entity<int>
{
    private RoleAssignment() { }

    public RoleAssignment(int roleId, int? userId, string? principalSid)
    {
        RoleId = roleId;
        UserId = userId;
        PrincipalSid = principalSid;
    }

    public int RoleId { get; private set; }

    /// <summary>Призначення на особу. Взаємовиключне з <see cref="PrincipalSid"/>.</summary>
    public int? UserId { get; private set; }

    /// <summary>SID AD-групи. Це **не** авторство — воно завжди `UserId` (D-86).</summary>
    public string? PrincipalSid { get; private set; }

    /// <summary>Область дії: проєкт, аркуш, період (ФВ-6.14).</summary>
    public string? ScopeJson { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Security/ResourceGrant.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/ResourceGrant.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Ресурсний грант із успадкуванням <c>Project → Sheet → Table → Column</c>.
/// </summary>
/// <remarks>
/// ⚠ <see cref="IsDeny"/> **виграє завжди**, на будь-якому рівні (ФВ-6.6). Це
/// свідома жорсткість: альтернатива «конкретніший рівень перемагає» дає
/// ситуації, де людина має доступ і ніхто не може пояснити чому.
/// <para>
/// Рядкового фільтра тут **немає** (D-92): найдрібніший рівень обмеження —
/// колонка, не рядок.
/// </para>
/// </remarks>
public sealed class ResourceGrant : Entity<int>
{
    private ResourceGrant() { }

    public ResourceGrant(int roleId, ResourceKind resourceKind, int resourceId, GrantLevel level)
    {
        RoleId = roleId;
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        Level = level;
    }

    public int RoleId { get; private set; }
    public ResourceKind ResourceKind { get; private set; }
    public int ResourceId { get; private set; }

    /// <summary>`None` → `Read` → `Write` → `Submit` → `Approve` → `Manage` (ФВ-6.13).</summary>
    public GrantLevel Level { get; private set; }

    /// <summary>Явна заборона. Перекриває будь-який дозвіл будь-якого рівня.</summary>
    public bool IsDeny { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Security/PasswordPolicy.cs`
MODULE: domain-security | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Security/PasswordPolicy.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Політика паролів для локальних облікових записів (ФВ-6.4).
/// </summary>
/// <remarks>
/// У першому релізі діє **базова** політика (ФВ-6.4a, D-104): довжина і
/// блокування після N невдач. Складність, історія і строк дії лишаються
/// полями, але не вмикаються, доки ІБ не дасть формулювання (`P-1`).
/// </remarks>
public sealed class PasswordPolicy : Entity<int>
{
    private PasswordPolicy() { }

    public PasswordPolicy(string code, int minLength, int maxFailedAttempts)
    {
        Code = code;
        MinLength = minLength;
        MaxFailedAttempts = maxFailedAttempts;
    }

    public string Code { get; private set; } = null!;
    public int MinLength { get; private set; }
    public int MaxFailedAttempts { get; private set; }
    public int LockoutMinutes { get; private set; }

    /// <summary>Кількість останніх паролів, які не можна повторити. `0` — не діє.</summary>
    public int HistoryDepth { get; private set; }

    /// <summary>Строк дії в днях. `0` — не діє.</summary>
    public int ExpiryDays { get; private set; }

    public bool RequireComplexity { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Workflow/ApprovalRoute.cs`
MODULE: domain-workflow | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Workflow/ApprovalRoute.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Маршрут погодження: багатоетапне затвердження конфігурується на рівні
/// проєкту (ФВ-5.17).
/// </summary>
/// <remarks>
/// Кроки посилаються на ролі **за ідентифікаторами**, тому перейменування ролі
/// не ламає вже налаштований маршрут.
/// </remarks>
public sealed class ApprovalRoute : Entity<int>
{
    private readonly List<ApprovalStep> _steps = [];

    private ApprovalRoute() { }

    public ApprovalRoute(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public IReadOnlyList<ApprovalStep> Steps => _steps;
}
```

### `src/Ecr.Domain/Entities/Workflow/ApprovalStep.cs`
MODULE: domain-workflow | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Workflow/ApprovalStep.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>Крок маршруту погодження: хто і на якому місці затверджує.</summary>
public sealed class ApprovalStep : Entity<int>
{
    private ApprovalStep() { }

    public ApprovalStep(int approvalRouteId, int ordinal, int roleId)
    {
        ApprovalRouteId = approvalRouteId;
        Ordinal = ordinal;
        RoleId = roleId;
    }

    public int ApprovalRouteId { get; private set; }

    /// <summary>Порядок кроку в маршруті; унікальний у межах маршруту.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Роль за ідентифікатором, не за назвою.</summary>
    public int RoleId { get; private set; }
}
```

### `src/Ecr.Domain/Entities/Workflow/ApprovalState.cs`
MODULE: domain-workflow | STAGE: 3

```csharp
// src/Ecr.Domain/Entities/Workflow/ApprovalState.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Стан робочого процесу на **аркуш × період** (D-38). Це **єдине джерело
/// істини** про статус: скалярного статусу на документі не існує (D-93).
/// </summary>
/// <remarks>
/// 24 аркуші рідко готові одночасно, і чекати найповільніший не має сенсу.
/// Скалярний статус на документі був би другим джерелом, яке рано чи пізно
/// покаже <c>Approved</c> там, де половина аркушів у <c>Draft</c>.
/// </remarks>
public sealed class ApprovalState : Entity<long>
{
    private ApprovalState() { }

    public ApprovalState(long documentId, int sheetDefId, int periodKey)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        Status = DocumentStatus.Draft;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public int PeriodKey { get; private set; }
    public DocumentStatus Status { get; private set; }
    public int? CurrentStepId { get; private set; }

    public DateTime? SubmittedAt { get; private set; }
    public int? SubmittedByUserId { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public int? ApprovedByUserId { get; private set; }
    public string? RejectedReason { get; private set; }

    public DateTime? ReopenedAt { get; private set; }
    public int? ReopenedByUserId { get; private set; }

    /// <summary>Причина `Reopen` — обов'язкова, це перевіряє і база (D-67).</summary>
    public string? ReopenReason { get; private set; }

    /// <summary>
    /// Повернення в <c>Draft</c> для правки поданого. Створює потребу в
    /// **новому** зрізі; старий лишається `Submitted` назавжди (ФВ-5.20a).
    /// </summary>
    public void Reopen(int userId, string reason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: 1) Status має бути Submitted або Approved;\n" +
            "2) reason обов'язковий і непорожній;\n" +
            "3) Status = Draft, зафіксувати автора, час і причину;\n" +
            "4) ⚠ перевірку стану ПЕРІОДУ тут НЕ робити — вона в use-case, бо " +
            "вимагає UPDLOCK на doc.Period проти гонки з PeriodStateJob (ФВ-1.10a). " +
            "При Closed періоді use-case поверне ECR-PRD-4223.");
}
```

### `src/Ecr.Domain/Entities/External/DataSource.cs`
MODULE: domain-external | STAGE: 5

```csharp
// src/Ecr.Domain/Entities/External/DataSource.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Зовнішнє джерело даних. Транспорт — **поле, не гілка коду** (ФВ-11.2):
/// різні джерела можуть використовувати RTQP і Web API одночасно.
/// </summary>
/// <remarks>
/// <see cref="SecretName"/> — **лише ім'я** секрету, ніколи значення
/// (ФВ-6.11). Пароль сервісного облікового запису живе у сховищі секретів; у
/// нашій базі від нього є тільки посилання.
/// </remarks>
public sealed class DataSource : Entity<int>
{
    private DataSource() { }

    public DataSource(EcrCode code, LocalizedText name, ExternalTransport transport)
    {
        Code = code.Value;
        NameL10n = name;
        Transport = transport;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public ExternalTransport Transport { get; private set; }
    public string? Endpoint { get; private set; }

    /// <summary>Ім'я секрету. ⛔ Ніколи не значення.</summary>
    public string? SecretName { get; private set; }

    public bool IsActive { get; private set; }
}
```

### `src/Ecr.Domain/Entities/External/SourceEntity.cs`
MODULE: domain-external | STAGE: 5

```csharp
// src/Ecr.Domain/Entities/External/SourceEntity.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Сутність збору: що саме тягнемо з джерела. <see cref="SourceKind"/>
/// визначає, **хто master** (ФВ-8.9) — і перемикається лише поза відкритим
/// періодом, із періодом подвійної звірки (D-49).
/// </summary>
public sealed class SourceEntity : Entity<int>
{
    private SourceEntity() { }

    public SourceEntity(int dataSourceId, string sourcePath, RegistrySourceKind sourceKind)
    {
        DataSourceId = dataSourceId;
        SourcePath = sourcePath;
        SourceKind = sourceKind;
        IsActive = true;
    }

    public int DataSourceId { get; private set; }

    /// <summary>Шлях у джерелі: шаблон AF, вʼюха, процедура.</summary>
    public string SourcePath { get; private set; } = null!;

    public RegistrySourceKind SourceKind { get; private set; }
    public int? TargetRegistryDefId { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Перемикання master. Дозволене лише поза відкритим періодом.</summary>
    public void SwitchSourceKind(RegistrySourceKind kind)
        => throw new NotImplementedException(
            "TODO: змінити SourceKind. Перевірку «немає відкритого періоду» " +
            "робить use-case (ECR-REG-0422) — сутність про періоди не знає. " +
            "Період подвійної звірки після перемикання — регламент, не код.");
}
```

### `src/Ecr.Domain/Entities/External/EntityFieldMap.cs`
MODULE: domain-external | STAGE: 5

```csharp
// src/Ecr.Domain/Entities/External/EntityFieldMap.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Мапінг поля джерела на наше поле, разом із **одиницями обох боків**
/// (ФВ-16.10, D-79).
/// </summary>
/// <remarks>
/// ⚠ Сирі дані в <c>ext.*</c> зберігаються **в одиниці джерела**, конверсія
/// відбувається при завантаженні і потрапляє в журнал. Інакше повторний
/// перерахунок з архіву дав би інший результат, ніж перший.
/// <para>
/// Зміна одиниці атрибута в джерелі **зупиняє збір** (ФВ-16.9,
/// <c>ECR-INT-0422</c>) — не конвертує «як здається». Це найчастіше джерело
/// мовчазних розбіжностей у числах.
/// </para>
/// </remarks>
public sealed class EntityFieldMap : Entity<int>
{
    private EntityFieldMap() { }

    public EntityFieldMap(int sourceEntityId, string sourceField, string targetField)
    {
        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        TargetField = targetField;
        IsActive = true;
    }

    public int SourceEntityId { get; private set; }
    public string SourceField { get; private set; } = null!;
    public string TargetField { get; private set; } = null!;

    /// <summary>Одиниця в джерелі, як її оголосив постачальник даних.</summary>
    public int? SourceUnitId { get; private set; }

    /// <summary>Одиниця, в якій значення потрібне нам.</summary>
    public int? TargetUnitId { get; private set; }

    public bool IsActive { get; private set; }
}
```

### `src/Ecr.Domain/Entities/External/CollectionSchedule.cs`
MODULE: domain-external | STAGE: 5

```csharp
// src/Ecr.Domain/Entities/External/CollectionSchedule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Розклад збору — **на сутність**, не один на систему (ФВ-13.15).
/// </summary>
/// <remarks>
/// Стосується **лише** адаптера до PI AF (D-106): дані з наших веб-форм не
/// опитуються взагалі — вони приходять записом і одразу запускають
/// інкрементний перерахунок. Заводити розклад для власних форм означало б
/// опитувати власну базу про те, що ми самі щойно в неї поклали.
/// <para>
/// <see cref="Watermark"/> — **оптимізація, не стан**: втрата позначки має
/// призводити до повторного збору інтервалу, а не до його пропуску. Збір
/// ідемпотентний (ФВ-11.3), тому повтор безпечний, а пропуск — ні.
/// </para>
/// </remarks>
public sealed class CollectionSchedule : Entity<int>
{
    private CollectionSchedule() { }

    public CollectionSchedule(int sourceEntityId, string cronExpression)
    {
        SourceEntityId = sourceEntityId;
        CronExpression = cronExpression;
        IsActive = true;
    }

    public int SourceEntityId { get; private set; }
    public string CronExpression { get; private set; } = null!;

    /// <summary>Вікно збору в хвилинах: скільки назад від моменту запуску.</summary>
    public int LookbackMinutes { get; private set; }

    /// <summary>Позначка останнього успішного збору. Оптимізація, не стан.</summary>
    public DateTime? Watermark { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Просуває позначку після успішного збору інтервалу.</summary>
    public void AdvanceWatermark(DateTime to)
        => throw new NotImplementedException(
            "TODO: рухати позначку лише ВПЕРЕД і лише після підтвердженого " +
            "запису інтервалу. Відкат назад дозволений явною адміністративною " +
            "дією — це штатний спосіб перезібрати період (catch-up, ФВ-11.3).");
}
```
