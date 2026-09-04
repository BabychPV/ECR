# 02 — Контракти. Єдине джерело правди

> **Це найважливіший файл пакета.** Усе, що тут написано, реалізується
> **дослівно**. Змінювати контракт заборонено (`08-workflow.md` §5): потрібна
> зміна → `questions.md` і зупинка.
>
> Контракт складається з чотирьох файлів:
>
> | Файл | Що містить |
> |---|---|
> | **`02-contracts.md`** (цей) | enum'и, доменні сутності, порти, DTO, доступ, помилки, конвенції API, спостережуваність |
> | [`02a-db-schema.md`](02a-db-schema.md) | повний DDL усіх схем БД |
> | [`02b-expressions.md`](02b-expressions.md) | граматика виразів, каталог функцій, семантика |
> | [`02c-fixtures.md`](02c-fixtures.md) | тестові фікстури з очікуваними результатами |

## Зміст

| Якір | Розділ |
|---|---|
| [`#conventions`](#conventions) | Наскрізні конвенції коду |
| [`#enums`](#enums) | Усі перелічення |
| [`#value-objects`](#value-objects) | Значеннєві типи |
| [`#ports`](#ports) | Порти застосунку (інтерфейси) |
| [`#access-contract`](#access-contract) | `IAccessDecisionService`, `AccessProfile` |
| [`#dto`](#dto) | DTO запитів і відповідей |
| [`#api-conventions`](#api-conventions) | REST, пагінація, версійність |
| [`#api-endpoints`](#api-endpoints) | Перелік ендпоінтів |
| [`#error-model`](#error-model) | Формат помилки |
| [`#error-codes`](#error-codes) | Каталог кодів помилок |
| [`#observability`](#observability) | Логи, метрики, трасування |
| [`#health`](#health) | Health-ендпоінти |
| [`#migrations`](#migrations) | Міграції і SQL поза EF |
| [`#seed-data`](#seed-data) | Обов'язковий seed |

---

<a id="conventions"></a>
## 1. Наскрізні конвенції коду

```
C# 13 / net10.0
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<ImplicitUsings>enable</ImplicitUsings>
file-scoped namespaces
```

**Правила, обов'язкові в кожному файлі:**

1. Ідентифікатори — англійською; XML-doc і коментарі — **українською**.
2. **Жодного `DateTime.Now` / `DateTime.UtcNow`** у доменному й прикладному коді —
   лише `IClock`. Це вимога тестованості: інакше поведінка періодів невідтворювана.
3. **Жодного `.Result` / `.Wait()` / `async void`.**
4. У `Ecr.Application` **немає `.ToList()` без `Take()`** — перевіряється
   архітектурним тестом.
5. Усі публічні асинхронні методи приймають `CancellationToken ct` **останнім**
   параметром і мають суфікс `Async`.
6. Доменні сутності — з приватними сеттерами; зміна стану лише через методи.
7. Ідентифікатори типів: `int` для метаданих і довідників, `long` для рядків і
   аудиту, `Guid` не використовується як PK ніде.
8. Гроші/емісії — `decimal`; `float`/`double` **заборонені** в результатних
   структурах (`D-30`).

```csharp
// src/Ecr.Domain/Abstractions/IClock.cs
namespace Ecr.Domain.Abstractions;

/// <summary>
/// Єдине джерело поточного часу. Прямі звернення до <see cref="DateTime"/>
/// у доменному і прикладному шарі заборонені — інакше поведінка періодів,
/// offsets і <c>IsLateEdit</c> стає невідтворюваною в тестах.
/// </summary>
public interface IClock
{
    /// <summary>Поточний момент у UTC. Завжди <see cref="DateTimeKind.Utc"/>.</summary>
    DateTime UtcNow { get; }
}
```

---

<a id="enums"></a>
## 2. Перелічення

```csharp
// src/Ecr.Domain/Enums/Enums.cs
namespace Ecr.Domain.Enums;

/// <summary>Стан версії шаблону.</summary>
public enum TemplateVersionStatus : byte
{
    Draft = 0,
    Published = 1,
    Deprecated = 2
}

/// <summary>Розкладка таблиці: як періоди лягають на структуру.</summary>
public enum TableLayoutKind : byte
{
    /// <summary>Місяці в колонках (сумісність із чинним Excel).</summary>
    MonthsInColumns = 0,
    /// <summary>Місяці в рядках (сумісність із чинним Excel).</summary>
    MonthsInRows = 1,
    /// <summary>Статична таблиця, не залежить від періоду.</summary>
    Static = 2,
    /// <summary>Окремий екземпляр таблиці на кожен період. Рекомендований для нових шаблонів.</summary>
    PerPeriodInstance = 3
}

/// <summary>Спосіб формування рядків таблиці.</summary>
public enum TableRowMode : byte
{
    /// <summary>Рядки визначені в шаблоні (<c>cfg.RowDef</c>).</summary>
    Fixed = 0,
    /// <summary>Рядки додає користувач.</summary>
    Dynamic = 1,
    /// <summary>Фіксовані рядки плюс можливість додавати свої.</summary>
    Mixed = 2
}

/// <summary>Роль рядка у звіті.</summary>
public enum RowKind : byte
{
    Group = 0,
    Item = 1,
    Balance = 2,
    Note = 3,
    Header = 4
}

/// <summary>
/// Тип даних колонки. Розширюваний: нові значення додаються без міграції схеми
/// (ФВ-2.4). Зберігається як <c>tinyint</c>.
/// </summary>
public enum CellDataType : byte
{
    String = 0,
    Int = 1,
    Decimal = 2,
    Bool = 3,
    Date = 4,
    /// <summary>Значення з реєстру; у комірці — <c>ValueRegistryEntryId</c>.</summary>
    Lookup = 5,
    /// <summary>Обчислюється формулою шаблону; матеріалізується з <c>IsCalculated = 1</c>.</summary>
    Formula = 6,
    /// <summary>Одиниця вимірювання на рядок; у комірці — <c>ValueUnitId</c> (R-A4).</summary>
    Unit = 7,
    /// <summary>Результат методології; у комірку не пишеться, читається через <c>cfg.CalculationBinding</c>.</summary>
    Calculated = 8
}

/// <summary>Область дії формули.</summary>
public enum FormulaScope : byte
{
    Column = 0,
    Row = 1,
    Cell = 2
}

/// <summary>Діалект виразу. Визначає набір дозволених функцій і посилань.</summary>
public enum ExpressionDialect : byte
{
    /// <summary>Формули шаблону: 11 Excel-сумісних функцій, посилання на аркуші й рядки.</summary>
    Template = 0,
    /// <summary>Формули методології: NCalc-діалект, аргументи <c>@Arg</c>, константи <c>CST.</c>.</summary>
    Methodology = 1
}

/// <summary>Рівень результату валідації.</summary>
public enum ValidationSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>Клас структурної зміни (документ 10 «Еволюція схеми»).</summary>
public enum ChangeClass : byte
{
    /// <summary>Підписи, стилі, <c>Ordinal</c>, формати — дозволено в опублікованій версії.</summary>
    Presentation = 0,
    /// <summary>Додавання, що не зачіпає наявні дані.</summary>
    Safe = 1,
    /// <summary>Потребує стратегії міграції і звіту про вплив.</summary>
    Guarded = 2,
    /// <summary>Ламає наявні дані. У версії з документами — відмова операції.</summary>
    Breaking = 3
}

/// <summary>Гранулярність періоду проєкту.</summary>
public enum PeriodKind : byte
{
    Monthly = 0,
    Quarterly = 1,
    Yearly = 2,
    Custom = 3
}

/// <summary>Стан звітного періоду. Обчислює <c>PeriodStateJob</c>, а не запит.</summary>
public enum PeriodState : byte
{
    Scheduled = 0,
    Open = 1,
    Grace = 2,
    Closed = 3
}

/// <summary>Режим визначення поточного періоду проєкту (D-77).</summary>
public enum CurrentPeriodMode : byte
{
    /// <summary>Веде <c>PeriodStateJob</c>: найраніший <c>Open</c>, інакше найпізніший <c>Grace</c>.</summary>
    Auto = 0,
    /// <summary>Зафіксовано адміністратором з обов'язковою причиною.</summary>
    Pinned = 1
}

/// <summary>Стан проєкту.</summary>
public enum ProjectStatus : byte
{
    Draft = 0,
    Active = 1,
    Grace = 2,
    Closed = 3,
    Archived = 4
}

/// <summary>Стан документа у робочому процесі.</summary>
public enum DocumentStatus : byte
{
    Draft = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>Рівень ресурсного гранта. Порядок значень значущий: більше = ширше.</summary>
public enum GrantLevel : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Submit = 3,
    Approve = 4,
    Manage = 5
}

/// <summary>Тип ресурсу, на який видається грант.</summary>
public enum ResourceKind : byte
{
    Project = 0,
    Sheet = 1,
    Table = 2,
    Column = 3
}

/// <summary>Провайдер автентифікації. Нижче рівня входу не використовується.</summary>
public enum AuthProvider : byte
{
    Windows = 0,
    Local = 1
}

/// <summary>Причина відмови в доступі. Повертається замість <c>bool</c> (ФВ-6.8).</summary>
public enum EditDenyReason : byte
{
    None = 0,
    NoGrant = 1,
    PeriodNotOpenYet = 2,
    PeriodClosed = 3,
    OutOfAccessWindow = 4,
    DocumentSubmitted = 5,
    DocumentApproved = 6,
    ColumnReadOnly = 7,
    RowReadOnly = 8,
    CalculatedCell = 9,
    ProjectArchived = 10,
    ArchivingInProgress = 11,
    BusinessRule = 12,
    /// <summary>
    /// Сеанс симуляції «очима користувача» — лише читання (D-96).
    /// Дія відхиляється незалежно від прав того, кого симулюють.
    /// </summary>
    SimulationReadOnly = 13
}

/// <summary>Поведінка поза вікном доступу до періоду (ФВ-2.16).</summary>
public enum OutOfWindowBehavior : byte
{
    Hide = 0,
    ReadOnly = 1,
    Warn = 2
}

/// <summary>Вид зв'язку між таблицями (ФВ-2.12).</summary>
public enum TableRelationKind : byte
{
    Mirror = 0,
    Rollup = 1,
    Reference = 2,
    Cascade = 3,
    Check = 4,
    Copy = 5
}

/// <summary>Хто є master для реєстру (ФВ-8.9).</summary>
public enum RegistrySourceKind : byte
{
    External = 0,
    Hybrid = 1,
    Local = 2
}

/// <summary>Рівень драбини виразності для методології (ФВ-9.2).</summary>
public enum CalculationLevel : byte
{
    Configuration = 1,
    Script = 2,
    Module = 3
}

/// <summary>
/// Арифметичний режим версії методології (ФВ-9.9). <c>Legacy</c> відтворює
/// арифметику чинної системи побітово і використовується лише заради сумісності.
/// </summary>
public enum NumericMode : byte
{
    Legacy = 0,
    Strict = 1
}

/// <summary>
/// Джерело календарних величин періоду (D-78). Різниця конвенцій змінює всі
/// числа при перерахунку в г/с і т/рік.
/// </summary>
public enum CalendarMode : byte
{
    /// <summary>Фактичний календар періоду.</summary>
    Actual = 0,
    /// <summary>Рік = 365 днів незалежно від фактичного.</summary>
    Fixed365 = 1,
    /// <summary>Місяць = 30 днів, рік = 360.</summary>
    Fixed360 = 2
}

/// <summary>Рівень деталізації трейсу розрахунку (ЗБР-3).</summary>
public enum TraceLevel : byte
{
    Off = 0,
    ErrorsOnly = 1,
    Full = 2
}

/// <summary>Статус зрізу звітності (D-65).</summary>
public enum SnapshotStatus : byte
{
    Draft = 0,
    Approved = 1,
    Submitted = 2
}

/// <summary>Транспорт до зовнішнього джерела (ФВ-11.2).</summary>
public enum ExternalTransport : byte
{
    PiWebApi = 0,
    PiSqlClient = 1
}

/// <summary>Режим роботи з редакцією SQL Server (АРХ-7).</summary>
public enum SqlEditionMode : byte
{
    Auto = 0,
    Standard = 1,
    Enterprise = 2
}

/// <summary>Фізична модель зберігання комірок таблиці (D-21).</summary>
public enum CellStorageMode : byte
{
    Normalized = 0,
    Hybrid = 1
}
```

---

<a id="value-objects"></a>
## 3. Значеннєві типи

```csharp
// src/Ecr.Domain/ValueObjects/PeriodKey.cs
namespace Ecr.Domain.ValueObjects;

using Ecr.Domain.Enums;

/// <summary>
/// Ключ партиціонування: <c>Year * 100 + Sequence</c> (R-A6).
/// Для місячних періодів збігається з <c>YYYYMM</c>, для решти — ні,
/// тому виводити місяць арифметикою заборонено.
/// </summary>
public readonly record struct PeriodKey(int Value)
{
    /// <summary>Рік періоду.</summary>
    public int Year => Value / 100;

    /// <summary>Порядковий номер періоду в році (1-based).</summary>
    public int Sequence => Value % 100;

    /// <summary>Створює ключ із року і порядкового номера.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Рік поза 1900..9999 або номер поза 1..99.</exception>
    public static PeriodKey Create(int year, int sequence)
    {
        if (year is < 1900 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(year), year, "Рік має бути в межах 1900..9999.");
        if (sequence is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Номер періоду має бути в межах 1..99.");
        return new PeriodKey(year * 100 + sequence);
    }

    /// <summary>Перший і останній ключ року — межі діапазону партицій для архівації.</summary>
    public static (PeriodKey From, PeriodKey To) YearRange(int year, PeriodKind kind) => kind switch
    {
        PeriodKind.Monthly => (Create(year, 1), Create(year, 12)),
        PeriodKind.Quarterly => (Create(year, 1), Create(year, 4)),
        PeriodKind.Yearly => (Create(year, 1), Create(year, 1)),
        // Custom: верхню межу знає лише календар проєкту — повертаємо максимум,
        // виклик зобов'язаний звузити його фактичною кількістю періодів.
        _ => (Create(year, 1), Create(year, 99))
    };

    public override string ToString() => Value.ToString();
}
```

```csharp
// src/Ecr.Domain/ValueObjects/CellAddress.cs
namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Адреса комірки: період, рядок, колонка. Позиційних координат не існує —
/// це і є головна відмінність від чинного рішення.
/// </summary>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="TableRowId">Ідентифікатор рядка (<c>doc.TableRow.Id</c>).</param>
/// <param name="ColumnDefId">Ідентифікатор колонки (<c>cfg.ColumnDef.Id</c>).</param>
public readonly record struct CellAddress(PeriodKey PeriodKey, long TableRowId, int ColumnDefId);
```

```csharp
// src/Ecr.Domain/ValueObjects/CellValueData.cs
namespace Ecr.Domain.ValueObjects;

using Ecr.Domain.Enums;

/// <summary>
/// Типізоване значення комірки. Рівно одне з полів <c>Value*</c> заповнене,
/// або жодне — тоді <c>IsEmpty = true</c> (явна порожнеча, R-B4).
/// </summary>
public sealed record CellValueData
{
    public string? ValueString { get; init; }
    public decimal? ValueNumeric { get; init; }
    public DateTime? ValueDate { get; init; }
    public bool? ValueBool { get; init; }
    public int? ValueRegistryEntryId { get; init; }

    /// <summary>Одиниця вимірювання; лише для <see cref="CellDataType.Unit"/> (R-A4).</summary>
    public int? ValueUnitId { get; init; }

    /// <summary>Значення обчислене формулою шаблону, а не введене користувачем.</summary>
    public bool IsCalculated { get; init; }

    /// <summary>Явна порожнеча: користувач свідомо лишив комірку порожньою.</summary>
    public bool IsEmpty { get; init; }

    /// <summary>Порожня комірка як явний стан.</summary>
    public static CellValueData Empty { get; } = new() { IsEmpty = true };

    /// <summary>Перевіряє, що заповнене рівно одне поле значення (або жодного при <c>IsEmpty</c>).</summary>
    public bool IsWellFormed()
    {
        var filled = 0;
        if (ValueString is not null) filled++;
        if (ValueNumeric is not null) filled++;
        if (ValueDate is not null) filled++;
        if (ValueBool is not null) filled++;
        if (ValueRegistryEntryId is not null) filled++;
        if (ValueUnitId is not null) filled++;
        return IsEmpty ? filled == 0 : filled == 1;
    }
}
```

```csharp
// src/Ecr.Domain/ValueObjects/LocalizedText.cs
namespace Ecr.Domain.ValueObjects;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Локалізований текст. Зберігається однією колонкою <c>…L10n</c> у форматі JSON
/// <c>{"en":"…","ru":"…","kz":"…"}</c>. Додати мову = додати запис у
/// <c>sys.Language</c>, а не колонку в двадцяти таблицях (ФВ-2.2).
/// </summary>
public sealed class LocalizedText
{
    private readonly Dictionary<string, string> _values;

    [JsonConstructor]
    public LocalizedText(Dictionary<string, string>? values = null)
        => _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Значення для мови; якщо немає — для <paramref name="fallback"/>; якщо і його немає — перше наявне.</summary>
    public string? Get(string language, string fallback = "en")
    {
        if (_values.TryGetValue(language, out var v)) return v;
        if (_values.TryGetValue(fallback, out var f)) return f;
        return _values.Count > 0 ? _values.Values.First() : null;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Серіалізація у формат зберігання.</summary>
    public string ToJson() => JsonSerializer.Serialize(_values);

    /// <summary>Десеріалізація зі стовпця <c>…L10n</c>.</summary>
    public static LocalizedText FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new LocalizedText()
            : new LocalizedText(JsonSerializer.Deserialize<Dictionary<string, string>>(json));
}
```

```csharp
// src/Ecr.Domain/ValueObjects/EcrCode.cs
namespace Ecr.Domain.ValueObjects;

using System.Text.RegularExpressions;

/// <summary>
/// Код сутності конфігурації. Обмеження продиктоване лексером виразів:
/// код вживається всередині <c>[...]</c> без екранування (R-B6).
/// </summary>
public readonly partial record struct EcrCode
{
    /// <summary>Регулярний вираз допустимого коду.</summary>
    public const string Pattern = "^[A-Za-z][A-Za-z0-9_]{0,63}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Validator();

    public string Value { get; }

    private EcrCode(string value) => Value = value;

    /// <summary>Створює код або кидає виняток.</summary>
    /// <exception cref="ArgumentException">Код не відповідає <see cref="Pattern"/>.</exception>
    public static EcrCode Create(string value)
        => TryCreate(value, out var code)
            ? code
            : throw new ArgumentException($"Код '{value}' не відповідає шаблону {Pattern}.", nameof(value));

    public static bool TryCreate(string? value, out EcrCode code)
    {
        if (!string.IsNullOrEmpty(value) && Validator().IsMatch(value))
        {
            code = new EcrCode(value);
            return true;
        }
        code = default;
        return false;
    }

    public override string ToString() => Value;
    public static implicit operator string(EcrCode c) => c.Value;
}
```

```csharp
// src/Ecr.Domain/ValueObjects/RowKey.cs
namespace Ecr.Domain.ValueObjects;

using System.Text.RegularExpressions;

/// <summary>
/// Стабільна бізнес-ідентичність рядка. Для <c>RowMode = Fixed</c> береться з
/// <c>cfg.RowDef.RowKey</c>, для <c>Dynamic</c> — GUID у форматі "N" (ФВ-2.5).
/// На відміну від <see cref="EcrCode"/> допускає цифрові ключі («7001001»).
/// </summary>
public readonly partial record struct RowKey
{
    public const string Pattern = @"^[A-Za-z0-9_.\-]{1,100}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Validator();

    public string Value { get; }

    private RowKey(string value) => Value = value;

    public static RowKey Create(string value)
        => TryCreate(value, out var key)
            ? key
            : throw new ArgumentException($"RowKey '{value}' не відповідає шаблону {Pattern}.", nameof(value));

    public static bool TryCreate(string? value, out RowKey key)
    {
        if (!string.IsNullOrEmpty(value) && Validator().IsMatch(value))
        {
            key = new RowKey(value);
            return true;
        }
        key = default;
        return false;
    }

    /// <summary>Новий ключ для динамічного рядка.</summary>
    public static RowKey NewDynamic() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
    public static implicit operator string(RowKey k) => k.Value;
}
```

---

<a id="access-contract"></a>
## 4. Контракт доступу

> Це **єдина** точка прийняття рішень про доступ. Прямі перевірки ролей поза
> нею заборонені й перевіряються архітектурним тестом (ФВ-6.8).

```csharp
// src/Ecr.Application/Security/EditDecision.cs
namespace Ecr.Application.Security;

using Ecr.Domain.Enums;

/// <summary>
/// Рішення про доступ. Повертає <b>причину</b>, а не <c>bool</c>: користувач має
/// розуміти, чому комірка сіра, інакше він піде до адміністратора, а той —
/// до розробника.
/// </summary>
/// <param name="IsAllowed">Чи дозволена дія.</param>
/// <param name="Reason">Причина відмови; <see cref="EditDenyReason.None"/> при дозволі.</param>
/// <param name="Detail">Уточнення для UI (напр. дата закриття періоду). Не для логіки.</param>
public readonly record struct EditDecision(bool IsAllowed, EditDenyReason Reason, string? Detail = null)
{
    public static EditDecision Allow() => new(true, EditDenyReason.None);
    public static EditDecision Deny(EditDenyReason reason, string? detail = null) => new(false, reason, detail);
}
```

```csharp
// src/Ecr.Application/Security/AccessProfile.cs
namespace Ecr.Application.Security;

using Ecr.Domain.Enums;

/// <summary>
/// Ефективні права користувача, обчислені <b>раз на сесію</b> (ФВ-6.10).
/// Резолвити права на кожну комірку — гарантована смерть продуктивності:
/// бюджет відкриття таблиці 500×60 дає на права 50 мс на весь запит.
/// </summary>
public sealed class AccessProfile
{
    /// <summary>Ключ кешу: змінюється при зміні ролей або пароля.</summary>
    public required string CacheKey { get; init; }

    public required int UserId { get; init; }
    public required string SecurityStamp { get; init; }

    /// <summary>Функціональні права (<c>sec.Permission.Code</c>).</summary>
    public required IReadOnlySet<string> Permissions { get; init; }

    /// <summary>
    /// Ресурсні гранти: ключ — <c>"{ResourceKind}:{ResourceId}"</c>.
    /// Успадкування <c>Project → Sheet → Table → Column</c> уже розгорнуте.
    /// </summary>
    public required IReadOnlyDictionary<string, GrantLevel> Grants { get; init; }

    /// <summary>Явні заборони. <c>IsDeny</c> виграє завжди, на будь-якому рівні (ФВ-6.6).</summary>
    public required IReadOnlySet<string> Denies { get; init; }

    /// <summary>
    /// Профіль побудований у сеансі симуляції «очима користувача» (D-96).
    /// Права беруться повністю від <see cref="SimulatedForUserId"/>, але
    /// <b>будь-яка</b> дія запису відхиляється з
    /// <see cref="EditDenyReason.SimulationReadOnly"/>. Клієнт зобов'язаний
    /// показувати банер увесь час, поки прапорець стоїть.
    /// </summary>
    public bool IsSimulation { get; init; }

    /// <summary>Чиїми очима; <c>null</c> поза симуляцією.</summary>
    public int? SimulatedForUserId { get; init; }

    /// <summary>Хто симулює. Автор в аудиті — саме він, не суб'єкт.</summary>
    public int? SimulationActorUserId { get; init; }

    /// <summary>Чи має користувач функціональне право.</summary>
    public bool Has(string permissionCode) => Permissions.Contains(permissionCode);

    /// <summary>Ефективний рівень гранта на ресурс з урахуванням заборон.</summary>
    public GrantLevel LevelFor(ResourceKind kind, int resourceId)
    {
        var key = $"{kind}:{resourceId}";
        if (Denies.Contains(key)) return GrantLevel.None;
        return Grants.TryGetValue(key, out var level) ? level : GrantLevel.None;
    }
}
```

```csharp
// src/Ecr.Application/Security/IAccessDecisionService.cs
namespace Ecr.Application.Security;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
```

---

<a id="ports"></a>
## 5. Порти застосунку

> Контракти фіксуються **до** реалізацій. Порт — це межа, за якою починається
> технологія; use-case не знає, що саме за нею.

```csharp
// src/Ecr.Application/Ports/ICellStore.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.ValueObjects;

/// <summary>
/// Доступ до комірок. Ховає фізичну модель зберігання: нормалізовану або
/// гібридну (D-21). Use-case про різницю не знає.
/// </summary>
public interface ICellStore
{
    /// <summary>
    /// Зріз таблиці за період. Порожні комірки <b>не повертаються</b> — клієнт
    /// бере <c>ColumnDef.DefaultValue</c> (ФВ-3.8).
    /// Бюджет: p95 &lt; 600 мс на 500×60 (tz/08 §8.2).
    /// </summary>
    Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>Значення конкретних комірок.</summary>
    Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Застосовує набір змін однією транзакцією. Часткове застосування
    /// заборонене: або весь батч, або нічого (B04 §2.3).
    /// Бюджет: p95 &lt; 150 мс на 100 комірок.
    /// </summary>
    Task ApplyAsync(CellChangeSet changes, CancellationToken ct);

    /// <summary>Масове завантаження через <c>SqlBulkCopy</c>: імпорт, генератор, міграція.</summary>
    Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct);
}

/// <summary>Комірка з адресою і значенням.</summary>
public sealed record CellRecord(CellAddress Address, int TableDefId, CellValueData Value);

/// <summary>
/// Набір змін комірок в одній транзакції.
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="Upserts">Комірки для запису або оновлення.</param>
/// <param name="Deletes">Комірки для видалення (<c>"value": null</c>, R-B4).</param>
/// <param name="TouchedRowIds">Рядки, яким треба підняти <c>ModifiedAt</c> — інакше
/// <c>RowVersion</c> не зміниться і оптимістичне блокування тихо не працює (B04 §2.4).</param>
/// <param name="ChangedByUserId">Автор зміни (R-A2).</param>
/// <param name="IsLateEdit">Зміна в стані <c>Grace</c> або після <c>Reopen</c> (D-70).</param>
public sealed record CellChangeSet(
    long TableInstanceId,
    IReadOnlyList<CellRecord> Upserts,
    IReadOnlyList<CellAddress> Deletes,
    IReadOnlyList<long> TouchedRowIds,
    int ChangedByUserId,
    bool IsLateEdit);
```

```csharp
// src/Ecr.Application/Ports/IMetadataCache.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Entities.Configuration;

/// <summary>
/// Кеш метаданих шаблону. Опублікована версія структурно незмінна, тому ключ
/// <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна правка
/// створює новий ключ, а не псує старий (D-16). Це прибирає когерентність кешу
/// між інстансами як клас проблеми.
/// </summary>
public interface IMetadataCache
{
    /// <summary>Повна структура версії шаблону.</summary>
    Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Скидає запис. Потрібно лише після <c>Publish</c> або міграції.</summary>
    Task InvalidateAsync(int templateVersionId, CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IFormulaEngine.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

/// <summary>
/// Рушій виразів. Один парсер на обидва діалекти; NCalc використовується як
/// обчислювач, а не як парсер нашої мови (D-19, D-20).
/// Граматика — <see href="02b-expressions.md">02b-expressions.md</see>.
/// </summary>
public interface IFormulaEngine
{
    /// <summary>Розбирає вираз. Помилка синтаксису — результат, а не виняток.</summary>
    ParseResult Parse(string expression, ExpressionDialect dialect);

    /// <summary>
    /// Витягує залежності виразу. Діапазони рядків розкриваються в явний список
    /// <c>RowKey</c> на момент <c>Publish</c> — у рантаймі діапазонів не існує (B03 §4).
    /// </summary>
    IReadOnlyList<FormulaDependencyRef> ExtractDependencies(ParsedExpression expression, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);

    /// <summary>
    /// Топологічний порядок обчислення. Цикл повертається як помилка публікації,
    /// а не як тихо неправильне число (ФВ-9.4).
    /// </summary>
    OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
}
```

```csharp
// src/Ecr.Application/Ports/ICalculationModule.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Модуль розрахунку емісій. <b>Окрема точка розширення від</b>
/// <see cref="IFormulaEngine"/>: не всі обчислення є формулами (ФВ-9.2).
/// </summary>
public interface ICalculationModule
{
    /// <summary>Код модуля, унікальний у системі.</summary>
    string Code { get; }

    /// <summary>Рівень драбини виразності, який реалізує модуль.</summary>
    Ecr.Domain.Enums.CalculationLevel Level { get; }

    /// <summary>Чи здатний модуль обробити цю методологію.</summary>
    bool CanHandle(MethodologyDescriptor methodology);

    /// <summary>Виконує розрахунок. Не пише в БД — повертає результат.</summary>
    Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IExternalDataSource.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;

/// <summary>
/// Читання із зовнішнього джерела. PI AF — <b>виключно джерело</b>: система в
/// нього нічого не пише (D-44), тому парного <c>IExternalDataSink</c> не існує.
/// </summary>
public interface IExternalDataSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    ExternalTransport Transport { get; }

    /// <summary>Каталог сутностей джерела — для конфігуратора, щоб не вводити імена руками.</summary>
    Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct);

    /// <summary>
    /// Читає діапазон. Ідемпотентно: повторний запуск того самого діапазону не
    /// дублює даних (ФВ-11.3).
    /// </summary>
    Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IBackgroundJobScheduler.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Планувальник фонових задач. Порт існує, щоб заміна реалізації
/// (Quartz ↔ Hangfire) коштувала день, а не рефакторинг (D-09): допустимість
/// LGPL — відкрите питання до ІБ.
/// </summary>
public interface IBackgroundJobScheduler
{
    /// <summary>Ставить задачу в чергу негайно.</summary>
    Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Планує задачу за cron-виразом.</summary>
    Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Скасовує задачу.</summary>
    Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>Стан виконання для UI прогресу.</summary>
    Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);
}

/// <summary>Фонова задача.</summary>
public interface IBackgroundJob
{
    /// <summary>Виконує задачу. Має бути ідемпотентною і відновлюваною.</summary>
    Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct);
}

/// <summary>Канал прогресу для довгих операцій (усе довше ~5 с — у фон).</summary>
public interface IJobProgress
{
    Task ReportAsync(int percent, string? message, CancellationToken ct);
}

/// <summary>Стан фонової задачі.</summary>
public sealed record JobStatus(string JobId, string State, int Percent, string? Message, string? Error);
```

```csharp
// src/Ecr.Application/Ports/ISqlCapabilities.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;

/// <summary>
/// Можливості СУБД, визначені при старті (АРХ-7). Редакція впливає <b>лише</b>
/// на операційні стратегії — ніколи на модель даних, семантику чи числа.
/// Тому в бізнес-коді звертатися сюди заборонено: тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>.
/// </summary>
public interface ISqlCapabilities
{
    SqlEditionMode EffectiveMode { get; }
    string EditionName { get; }
    int ProductMajorVersion { get; }
    bool IsReadCommittedSnapshotOn { get; }

    /// <summary>Перебудова індексів без блокування (<c>ONLINE = ON</c>).</summary>
    bool SupportsOnlineIndexRebuild { get; }

    /// <summary>Resource Governor для ізоляції фонових задач від інтерактивного піку.</summary>
    bool SupportsResourceGovernor { get; }

    /// <summary>Розмір батча архівації, підібраний під редакцію.</summary>
    int ArchiveBatchSize { get; }
}
```

```csharp
// src/Ecr.Application/Ports/IUnitOfWork.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): архівація, міграція
/// документа і масовий імпорт виконуються батчами з окремим commit — інакше
/// version store під RCSI росте необмежено.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IUiStringCatalog.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Каталог рядків інтерфейсу (<c>ФВ-14.9</c>, <c>D-95</c>). Тут живуть і підписи
/// UI, і тексти помилок під ключами <c>err.&lt;код&gt;</c> (<c>ФВ-14.9a</c>,
/// <c>D-111</c>) — механізм локалізації один, не два.
/// </summary>
public interface IUiStringCatalog
{
    /// <summary>
    /// Весь каталог мови плюс версія для <c>ETag</c>. Відсутній ключ у мові
    /// підмінюється мовою за замовчуванням; ключа немає ніде — повертається
    /// сам ключ. Одна забута локалізація не має ламати екран.
    /// </summary>
    Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct);

    /// <summary>Поточна версія каталогу. Змінюється будь-яким записом.</summary>
    Task<int> GetRevisionAsync(CancellationToken ct);
}

/// <param name="LanguageCode">Мова зрізу.</param>
/// <param name="Revision">Версія каталогу; слугує <c>ETag</c>.</param>
/// <param name="Strings">Ключ → текст, уже з розгорнутим fallback.</param>
public sealed record UiStringCatalog(
    string LanguageCode,
    int Revision,
    IReadOnlyDictionary<string, string> Strings);
```

```csharp
// src/Ecr.Application/Ports/ISimulationService.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Симуляція «очима користувача» (<c>ФВ-6.16a</c>, <c>D-96</c>).
/// **Лише читання**: будь-який запис під нею відхиляється з
/// <see cref="EditDenyReason.SimulationReadOnly"/> незалежно від прав суб'єкта.
/// </summary>
public interface ISimulationService
{
    /// <summary>
    /// Починає сеанс і **одразу** пише <c>aud.SimulationSession</c>. Без
    /// запису «подивитися очима» стало б способом безслідно переглянути чужі
    /// дані, тому запис не відкладається і не батчиться.
    /// </summary>
    Task<long> StartAsync(int actorUserId, int subjectUserId, string reason, CancellationToken ct);

    Task EndAsync(long sessionId, CancellationToken ct);

    /// <summary>
    /// Профіль суб'єкта для активного сеансу. **Не кешується** (`ФВ-6.16a` п. 4):
    /// покладений під ключ суб'єкта, він дістався б справжньому користувачеві
    /// з прапорцем <c>IsSimulation</c>. Симуляція рідкісна — перебудова дешева.
    /// </summary>
    Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IOrphanScanner.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Обчислення ознаки <c>doc.TableRow.IsOrphaned</c> (<c>ФВ-8.13a</c>,
/// <c>D-98</c>). Викликається з нічної перевірки інваріантів (<c>ФВ-7.7</c>)
/// і при зміні вікна дії запису реєстру — **ніколи при читанні зрізу**:
/// перевірка чинності на кожен рядок зруйнувала б бюджет 400 мс.
/// </summary>
public interface IOrphanScanner
{
    /// <summary>Повний прохід. Повертає кількість змінених рядків.</summary>
    Task<int> ScanAllAsync(CancellationToken ct);

    /// <summary>
    /// Точковий перерахунок після зміни вікна дії запису. **Знімає** ознаку
    /// так само, як ставить: інакше виправлення довідника не розблокувало б
    /// <c>Submit</c>.
    /// </summary>
    Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct);
}
```

---

<a id="error-model"></a>
## 6. Формат помилки

Усі помилки API повертаються як `application/problem+json`
(RFC 9457) з нашими розширеннями.

```csharp
// src/Ecr.Api/Errors/EcrProblemDetails.cs
namespace Ecr.Api.Errors;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Помилка API. Розширює стандартний <see cref="ProblemDetails"/> кодом і
/// машинними подробицями: клієнт має розрізняти причини, а не парсити текст.
/// </summary>
public sealed class EcrProblemDetails : ProblemDetails
{
    /// <summary>Код із каталогу <see href="02-contracts.md#error-codes">#error-codes</see>.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Наскрізний ідентифікатор запиту для звірки з логами.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Подробиці, специфічні для коду: конфлікти, перелік заборонених комірок тощо.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions2 { get; init; }
}
```

```csharp
// src/Ecr.Application/Errors/EcrException.cs
namespace Ecr.Application.Errors;

/// <summary>
/// Помилка прикладного рівня з кодом. Використовується замість голих
/// <see cref="InvalidOperationException"/>: код потрапляє в API і в логи.
/// </summary>
public class EcrException : Exception
{
    public string ErrorCode { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }

    public EcrException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
    }
}

/// <summary>Порушення бізнес-правила. HTTP 422.</summary>
public sealed class BusinessRuleException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Відмова в доступі з причиною. HTTP 403.</summary>
public sealed class AccessDeniedException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Конфлікт паралельного редагування. HTTP 409.</summary>
public sealed class ConcurrencyConflictException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Сутність не знайдена. HTTP 404.</summary>
public sealed class NotFoundException(string errorCode, string message)
    : EcrException(errorCode, message);
```

---

<a id="error-codes"></a>
## 7. Каталог кодів помилок

Формат: `ECR-<ДОМЕН>-<HTTP><порядковий>`. Код **стабільний**: клієнт і тести
покладаються на нього, тому змінювати текст можна, код — ні.

| Код | HTTP | Коли |
|---|---|---|
| `ECR-AUTH-0401` | 401 | немає автентифікації |
| `ECR-AUTH-0403` | 403 | немає функціонального права |
| `ECR-AUTH-0423` | 423 | обліковий запис заблоковано |
| `ECR-ACCS-0403` | 403 | відмова `IAccessDecisionService`; у `Extensions2.reason` — `EditDenyReason` |
| `ECR-TMPL-0404` | 404 | шаблон або версія не знайдені |
| `ECR-TMPL-0409` | 409 | спроба структурної зміни в опублікованій версії (ФВ-7.1) |
| `ECR-TMPL-0422` | 422 | публікація не проходить валідацію цілісності |
| `ECR-TMPL-4221` | 422 | цикл у графі формул |
| `ECR-TMPL-4222` | 422 | посилання на неіснуючий аркуш/таблицю/колонку/рядок |
| `ECR-TMPL-4223` | 422 | несумісні одиниці без явного `CONVERT` (ФВ-16.7) |
| `ECR-SCHM-0409` | 409 | `Breaking`-зміна у версії з документами (ФВ-7.4) |
| `ECR-SCHM-0422` | 422 | `Guarded`-зміна без стратегії міграції |
| `ECR-DOC-0404` | 404 | документ не знайдено |
| `ECR-DOC-0409` | 409 | документ подано; потрібен `Reopen` (D-67) |
| `ECR-DOC-0422` | 422 | склад документа порушує `SheetGroupRule` |
| `ECR-ROW-0404` | 404 | рядок не знайдено |
| `ECR-ROW-0409` | 409 | рядок із таким `RowKey` уже існує в цьому екземплярі |
| `ECR-CELL-0409` | 409 | конфлікт `baseVersion`; у `Extensions2.conflicts` — перелік |
| `ECR-CELL-0422` | 422 | значення не відповідає типу, обов'язковості або довіднику |
| `ECR-CELL-4221` | 422 | спроба записати в обчислену комірку |
| `ECR-CELL-4222` | 422 | значення поза межами реєстру або дії дозволу |
| `ECR-PRD-0409` | 409 | період закрито; **або** спроба змінити `TimeZoneId` після відкриття першого періоду (ФВ-1.1a) |
| `ECR-PRD-0422` | 422 | період поза межами проєкту (ФВ-1.11) |
| `ECR-PRD-4223` | 422 | `Reopen` документа при закритому періоді |
| `ECR-PRD-4224` | 422 | `Sequence` поза діапазоном `1…12` (ФВ-1.5a, D-108) |
| `ECR-SUB-4221` | 422 | `Submit` при наявності рядків `IsOrphaned` (ФВ-8.13) |
| `ECR-SIM-0403` | 403 | спроба запису в сеансі симуляції (`SimulationReadOnly`, ФВ-6.16a) |
| `ECR-SIM-0422` | 422 | симуляція самого себе або без причини |
| `ECR-PWD-0428` | 428 | потрібна зміна пароля: доки `MustChangePassword`, доступні лише зміна пароля і вихід (ФВ-6.18) |
| `ECR-REG-0404` | 404 | запис реєстру не знайдено |
| `ECR-REG-0409` | 409 | видалення запису, на який посилаються дані (ФВ-8.6) |
| `ECR-REG-0422` | 422 | перемикання `SourceKind` у відкритому періоді (ФВ-8.9) |
| `ECR-UOM-0422` | 422 | конверсія між різними розмірностями (ФВ-16.3) |
| `ECR-UOM-4221` | 422 | контекстний коефіцієнт у `uom.Conversion` (ФВ-16.5) |
| `ECR-CALC-0409` | 409 | публікація методології автором останньої правки (D-40) |
| `ECR-CALC-0422` | 422 | публікація без зеленого тесту (ФВ-9.12) |
| `ECR-CALC-4221` | 422 | перерахунок закритого періоду без окремого погодження (ФВ-9.7) |
| `ECR-IMP-0422` | 422 | імпорт xlsx: структура файлу не відповідає шаблону |
| `ECR-INT-0503` | 503 | зовнішнє джерело недоступне; збір перейде в catch-up |
| `ECR-INT-0422` | 422 | UOM атрибута джерела змінився — збір зупинено (ФВ-16.9) |
| `ECR-SYS-0500` | 500 | необроблена помилка; у логах — `CorrelationId` |
| `ECR-SYS-0503` | 503 | система в стані архівації (`IsArchiving`) |

---

<a id="api-conventions"></a>
## 8. Конвенції API

**База:** `/api/v1`. Версія в шляху; ламкі зміни — нова версія, стара живе до
міграції клієнтів.

**Формати:** `application/json`, UTF-8, camelCase. Дати — ISO 8601 в UTC
(`2026-09-03T10:15:33Z`). `decimal` — рядком, щоб не втратити точність у JS.

**Пагінація** — курсорна для великих наборів, offset для довідкових:

```jsonc
// GET /api/v1/documents?limit=50&cursor=eyJpZCI6MTIzfQ
{
  "items": [ /* ... */ ],
  "nextCursor": "eyJpZCI6MTczfQ",   // null, якщо більше немає
  "totalCount": null                 // null, якщо підрахунок дорогий
}
```

* `limit` — за замовчуванням 50, максимум 500. Більше — `400`.
* Ендпоінтів, що повертають «усе», не існує — перевіряється архітектурним тестом.

**Фільтрація:** плоскі параметри — `?projectId=5&status=Draft&sheetCode=Water_07`.
Складні фільтри — `POST /search` із тілом.

**Сортування:** `?sort=code,-createdAt` (мінус = спадання). Дозволені поля
перелічені для кожного ендпоінта; інше — `400`.

**Ідемпотентність:** мутації, які можна повторити (імпорт, запуск задачі),
приймають заголовок `Idempotency-Key`.

**Конкурентність:** `If-Match` з `ETag` для структурних змін; `baseVersion`
на рядку для комірок (B04 §2.2).

**Довгі операції:** повертають `202 Accepted` з тілом
`{ "jobId": "...", "statusUrl": "/api/v1/jobs/{jobId}" }`.

---

<a id="api-endpoints"></a>
## 9. Ендпоінти

| Метод | Шлях | Право | Етап |
|---|---|---|---|
| `POST` | `/api/v1/login/windows` | — | 1 |
| `POST` | `/api/v1/login/local` | — | 3 |
| `POST` | `/api/v1/logout` | — | 1 |
| `GET` | `/api/v1/me` | — | 1 |
| `GET` | `/api/v1/templates` | `Template.View` | 1 |
| `POST` | `/api/v1/templates` | `Template.Edit` | 1 |
| `GET` | `/api/v1/templates/{id}/versions` | `Template.View` | 1 |
| `POST` | `/api/v1/templates/{id}/versions` | `Template.Edit` | 1 |
| `POST` | `/api/v1/template-versions/{id}/clone` | `Template.Edit` | 1 |
| `POST` | `/api/v1/template-versions/{id}/publish` | `Template.Publish` | 1 |
| `GET` | `/api/v1/template-versions/{id}/diff/{otherId}` | `Template.View` | 1 |
| `PATCH` | `/api/v1/template-versions/{id}/presentation` | `Template.Edit` | 1 |
| `GET` | `/api/v1/template-versions/{id}/structure` | `Template.View` | 1 |
| `GET` | `/api/v1/projects` | `Document.View` | 1 |
| `POST` | `/api/v1/projects` | `Project.Manage` | 1 |
| `POST` | `/api/v1/projects/{id}/clone` | `Project.Manage` | 3 |
| `PUT` | `/api/v1/projects/{id}/current-period` | `Period.Configure` | 3 |
| `GET` | `/api/v1/projects/{id}/periods` | `Document.View` | 3 |
| `POST` | `/api/v1/periods/{id}/reopen` | `Period.Reopen` | 3 |
| `GET` | `/api/v1/documents` | `Document.View` | 1 |
| `POST` | `/api/v1/documents` | `Document.Create` | 1 |
| `GET` | `/api/v1/documents/{id}` | `Document.View` | 1 |
| `GET` | `/api/v1/documents/{id}/tables/{tableInstanceId}` | `Document.View` | 1 |
| `PATCH` | `/api/v1/documents/{id}/cells` | — (через `IAccessDecisionService`) | 1 |
| `POST` | `/api/v1/documents/{id}/rows` | — | 1 |
| `POST` | `/api/v1/documents/{id}/validate` | `Document.View` | 2 |
| `POST` | `/api/v1/documents/{id}/recalculate` | `Calculation.Recalculate` | 2 |
| `POST` | `/api/v1/documents/{id}/submit` | — | 3 |
| `POST` | `/api/v1/documents/{id}/approve` | — | 3 |
| `POST` | `/api/v1/documents/{id}/reopen` | `Document.Reopen` | 3 |
| `POST` | `/api/v1/documents/{id}/export` | `Document.Export` | 5 |
| `POST` | `/api/v1/documents/{id}/import/preview` | `Document.Import` | 5 |
| `POST` | `/api/v1/documents/{id}/import/apply` | `Document.Import` | 5 |
| `GET` | `/api/v1/registries` | `Registry.View` | 4 |
| `GET` | `/api/v1/registries/{code}/entries` | `Registry.View` | 4 |
| `POST` | `/api/v1/registries/{code}/entries` | `Registry.EditData` | 4 |
| `GET` | `/api/v1/units` | — | 4 |
| `POST` | `/api/v1/units/convert` | — | 4 |
| `GET` | `/api/v1/methodologies` | `Calculation.View` | 4 |
| `POST` | `/api/v1/methodologies/{id}/versions/{vid}/publish` | `Calculation.Publish` | 4 |
| `POST` | `/api/v1/methodologies/{id}/simulate` | `Calculation.View` | 4 |
| `GET` | `/api/v1/roles` | `Security.ManageRoles` | 3 |
| `POST` | `/api/v1/roles` | `Security.ManageRoles` | 3 |
| `GET` | `/api/v1/users` | `Security.ManageUsers` | 3 |
| `POST` | `/api/v1/users` | `Security.ManageUsers` | 3 |
| `GET` | `/api/v1/audit/cells` | `Security.ViewAudit` | 3 |
| `GET` | `/api/v1/jobs/{jobId}` | `System.ViewHealth` | 5 |
| `POST` | `/api/v1/sources/{id}/collect` | `Integration.Manage` | 5 |
| `GET` | `/api/v1/reports/snapshots` | `Report.ViewRegulatory` | 5 |
| `POST` | `/api/v1/reports/{code}/build` | `Report.BuildSnapshot` | 5 |
| `GET` | `/api/v1/ui-strings/{lang}?scope=public` | — (анонімний) | 3 |
| `GET` | `/api/v1/ui-strings/{lang}?scope=private` | — (будь-який автентифікований) | 3 |
| `PUT` | `/api/v1/ui-strings/{lang}/{key}` | `System.ManageLocalization` | 3 |
| `POST` | `/api/v1/security/simulation` | `Security.Simulate` | 3 |
| `DELETE` | `/api/v1/security/simulation` | — (власний сеанс) | 3 |
| `POST` | `/api/v1/auth/change-password` | — (власний пароль) | 3 |
| `POST` | `/api/v1/registries/{code}/entries/{id}/validity` | `Registry.EditData` | 4 |

> **Каталог розділений на дві області** (`D-114`). `scope=public` анонімний —
> сторінка входу потребує підписів кнопок раніше, ніж хтось автентифікований;
> туди входять форма входу, загальний chrome і помилки автентифікації.
> `scope=private` — усе інше, лише після входу: підписи адміністративних
> областей і назви прав не мають бути видимі тому, хто ще не увійшов
> (`ФВ-14.2`). Кожна область має **власний** `ETag = revision`; на
> `If-None-Match` — `304`. Тексти помилок (`err.<код>`, `ФВ-14.9a`) розподілені
> між областями за тим самим правилом: помилки входу — публічні, решта — ні.

---

<a id="dto"></a>
## 10. DTO

```csharp
// src/Ecr.Application/Templates/Dto/TemplateDiffDto.cs
namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Diff двох версій шаблону. Зіставлення — **за ідентичністю** (`Code`,
/// `RowKey`), не за позицією: інакше будь-яке перевпорядкування дало б
/// «змінено все».
/// </summary>
/// <param name="Changes">Зміни з класифікацією за ризиком (`ФВ-7.3`).</param>
/// <param name="AffectedDocumentCount">Скільки документів прив'язано до вихідної версії.</param>
public sealed record TemplateDiffDto(
    IReadOnlyList<TemplateChangeDto> Changes,
    int AffectedDocumentCount);

/// <param name="ElementPath">Шлях: <c>Sheet.Table.Column</c> або <c>Sheet.Table.RowKey</c>.</param>
/// <param name="Kind">`Added` / `Removed` / `Modified` / `Presentation`.</param>
/// <param name="ChangeClass">Клас ризику; `Breaking` у версії з документами — відмова.</param>
public sealed record TemplateChangeDto(
    string ElementPath,
    string Kind,
    ChangeClass ChangeClass,
    string? OldValue,
    string? NewValue);
```

```csharp
// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs
namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Структура опублікованої версії — те, що віддається клієнту й кешується за
/// ключем <c>v{id}:r{rev}</c> (`ФВ-2.5`).
/// </summary>
public sealed record TemplateStructureDto(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDto> Sheets);

public sealed record SheetDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    IReadOnlyList<TableDto> Tables);

public sealed record TableDto(
    int Id, string Code, TableLayoutKind LayoutKind, TableRowMode RowMode,
    int? MaxDynamicRows,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows);
```

```csharp
// src/Ecr.Application/Registries/Dto/RegistryEntryDto.cs
namespace Ecr.Application.Registries.Dto;

/// <summary>
/// Запис довідника для UI і резолвінгу. У комірці зберігається
/// <see cref="Id"/>, а не <see cref="Display"/> (`ФВ-8.8`) — саме тому
/// перейменування не змінює історичні дані.
/// </summary>
public sealed record RegistryEntryDto(
    long Id,
    string Code,
    string Display,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

/// <param name="Values">Значення полів: код поля → значення відповідного типу.</param>
public sealed record RegistryEntryUpsertDto(
    long? Id,
    int RegistryDefId,
    string Code,
    LocalizedText Display,
    long? ParentEntryId,
    IReadOnlyDictionary<string, object?> Values);
```

```csharp
// src/Ecr.Application/Calculations/Dto/SimulationResultDto.cs
namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Результат прогону методології **без запису** (`ФВ-13.5`): що вийде, якщо
/// опублікувати.
/// </summary>
/// <param name="Outputs">Код виходу → значення й одиниця.</param>
/// <param name="DiffWithPublished">
/// Різниця з чинною опублікованою версією. Порожня — версія нічого не змінює;
/// саме це і треба бачити перед публікацією (`ФВ-9.6`).
/// </param>
/// <param name="Trace">Покроковий журнал; у симуляції завжди повний.</param>
public sealed record SimulationResultDto(
    IReadOnlyDictionary<string, decimal> Outputs,
    IReadOnlyDictionary<string, decimal> DiffWithPublished,
    IReadOnlyList<string> Trace);
```

```csharp
// src/Ecr.Application/Documents/Dto/PatchCellsRequest.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Пакетна зміна комірок. Часткове застосування заборонене: конфлікт у
/// будь-якому рядку відхиляє весь батч (B04 §2.3).
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="Origin">Джерело зміни: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>.</param>
/// <param name="Rows">Рядки зі змінами.</param>
public sealed record PatchCellsRequest(
    long TableInstanceId,
    int PeriodKey,
    string Origin,
    IReadOnlyList<PatchRow> Rows);

/// <summary>
/// Рядок у пакетній зміні.
/// </summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="BaseVersion">
/// Версія рядка, від якої відштовхується клієнт (hex <c>rowversion</c>).
/// <c>null</c> означає <b>створення</b> нового рядка (R-B2).
/// </param>
/// <param name="Cells">Зміни комірок.</param>
public sealed record PatchRow(
    string RowKey,
    string? BaseVersion,
    IReadOnlyList<PatchCell> Cells);

/// <summary>
/// Зміна однієї комірки. Три різні операції (R-B4):
/// значення — записати; <c>Value = null</c> — стерти (рядок видаляється);
/// <c>IsEmpty = true</c> — явна порожнеча; поле відсутнє в запиті — не чіпати.
/// </summary>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Value">Значення; <c>null</c> = стерти.</param>
/// <param name="IsEmpty">Явна порожнеча.</param>
public sealed record PatchCell(
    string ColumnCode,
    object? Value,
    bool IsEmpty = false);
```

```csharp
// src/Ecr.Application/Documents/Dto/PatchCellsResponse.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>Результат пакетної зміни.</summary>
/// <param name="AppliedCells">Скільки комірок записано.</param>
/// <param name="RowVersions">Нові версії зачеплених рядків: <c>RowKey</c> → hex.</param>
/// <param name="Validation">Результати валідації рівнів, які не блокують запис (R-B3).</param>
public sealed record PatchCellsResponse(
    int AppliedCells,
    IReadOnlyDictionary<string, string> RowVersions,
    IReadOnlyList<ValidationMessageDto> Validation);

/// <summary>Повідомлення валідації.</summary>
/// <param name="Severity">Рівень: <c>Info</c>/<c>Warning</c>/<c>Error</c>.</param>
/// <param name="RuleCode">Код правила з <c>cfg.ValidationRule</c>.</param>
/// <param name="Message">Локалізований текст.</param>
/// <param name="RowKey">Рядок, якого стосується; <c>null</c> — рівень таблиці.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — рівень рядка.</param>
public sealed record ValidationMessageDto(
    string Severity,
    string RuleCode,
    string Message,
    string? RowKey,
    string? ColumnCode);
```

```csharp
// src/Ecr.Application/Documents/Dto/CellConflictDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Конфлікт паралельного редагування. Повертається в
/// <c>Extensions2.conflicts</c> при <c>ECR-CELL-0409</c>.
/// «Перезаписати мовчки» не є опцією: користувач має побачити розбіжність.
/// </summary>
public sealed record CellConflictDto(
    string RowKey,
    string ColumnCode,
    object? YourValue,
    object? TheirValue,
    string TheirUser,
    DateTime TheirChangedAt,
    string CurrentVersion);
```

```csharp
// src/Ecr.Application/Documents/Dto/TableSliceDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Зріз таблиці для grid. Порожні комірки не передаються — клієнт бере
/// <c>DefaultValue</c> з опису колонки (ФВ-3.8).
/// Бюджет усієї операції: p95 1.5 с на 500×60 (tz/08 §8.2).
/// </summary>
public sealed record TableSliceDto(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows,
    IReadOnlyDictionary<string, string> CellPermissions);

/// <summary>Опис колонки для клієнта.</summary>
public sealed record ColumnDto(
    int Id,
    string Code,
    string Header,
    string DataType,
    int Ordinal,
    bool IsReadOnly,
    bool IsRequired,
    string? DisplayFormat,
    string? DefaultValue,
    int? LookupRegistryDefId,
    int? UnitId,
    string? UnitSymbol);

/// <summary>Рядок зі значеннями. Ключ у <paramref name="Cells"/> — код колонки.</summary>
public sealed record RowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string RowVersion,
    IReadOnlyDictionary<string, object?> Cells);
```

---

<a id="observability"></a>
## 11. Спостережуваність

**Логи** — вбудований `ILogger<T>`, структуровані. Обов'язкові поля:
`CorrelationId`, `UserId`, `DocumentId`/`TemplateVersionId` де доречно.
**Секрети і паролі не логуються ніколи** (ФВ-6.11) — це перевіряється тестом.

**`CorrelationId`** — заголовок `X-Correlation-Id`; якщо клієнт не передав,
генерується. Повертається у відповіді і в тілі помилки.

**Метрики** (`System.Diagnostics.Metrics`, лічильники з префіксом `ecr.`):

| Метрика | Тип | Навіщо |
|---|---|---|
| `ecr.cells.read` | Histogram (ms) | бюджет п.3 |
| `ecr.cells.write` | Histogram (ms) | бюджет п.5 |
| `ecr.formula.evaluate` | Histogram (ms) | бюджет п.7 |
| `ecr.access.profile.build` | Histogram (ms) | має бути раз на сесію |
| `ecr.job.duration` | Histogram (s) | фонові задачі |
| `ecr.calc.full_year` | Histogram (s) | **бюджет ≤ 10 хв** (ПРД-13) |
| `ecr.conflict.count` | Counter | конкурентність |
| `ecr.consistency.issues` | Counter | знахідки `ConsistencyCheckJob` |

**Правило:** якщо ендпоінт є в таблиці бюджету `tz/08` §8.2 — він **зобов'язаний**
мати метрику. Інакше твердження «вкладаємося» нічим не перевірити.

---

<a id="health"></a>
## 12. Health

| Шлях | Що перевіряє |
|---|---|
| `/health/live` | процес живий; без звернень до БД |
| `/health/ready` | БД доступна, міграції застосовані, метадані прогріті |
| `/health/db` | редакція, версія, RCSI, файлові групи, запас партицій |
| `/health/jobs` | планувальник живий, немає задач у стані `Failed` понад поріг |
| `/health/sources` | доступність зовнішніх джерел і журнал покриття |

`/health/db` **обов'язково** повідомляє поточний режим редакції і **що система
втрачає** в цьому режимі — наприклад «перебудова індексів потребує вікна
обслуговування» (АРХ-7 п. 5).

---

<a id="migrations"></a>
## 13. Міграції і SQL поза EF

**EF Core міграції** створюють: таблиці, ключі, FK, індекси, `CHECK`-обмеження,
`SEQUENCE`. Це артефакт розгортання, який накочує конвеєр окремим обліковим
записом (`D-14`).

**Окремими SQL-скриптами** (`src/Ecr.Infrastructure/Persistence/Sql/`), бо їх
виконує SQL Agent під окремим principal (`D-66`) — застосунок не має DDL-прав:

| Скрипт | Що |
|---|---|
| `01-filegroups.sql` | `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT`, `INDEXES` |
| `02-partitions.sql` | `pf_ByPeriodKey`, `ps_ByPeriodKey`, `pf_AuditByMonth`, `ps_AuditByMonth` |
| `03-archive-proc.sql` | процедура архівації: `INSERT…TABLOCK` → звірка сум → `TRUNCATE … WITH (PARTITIONS)` |
| `04-partition-maintenance.sql` | `SPLIT` наступних партицій на 6 місяців уперед |
| `05-rpt-views.sql` | генеровані вʼюхи `rpt.v_*` |
| `06-rcsi.sql` | `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` |

**Стартовий режим** (`Schema:StartupMode`): `Validate` у прод (є незастосована
міграція → фатально), `Migrate` у dev/test із `sp_getapplock`, щоб два інстанси
не мігрували одночасно.

---

<a id="seed-data"></a>
## 14. Seed чистої БД

Ідемпотентний: повторний запуск не створює дублікатів. Без нього застосунок
не стартує. Повні набори — [`02a-db-schema.md#seed`](02a-db-schema.md#seed).

| Набір | Склад |
|---|---|
| `sys.Language` | `en`, `ru`, `kz` |
| `sec.Permission` | повний каталог функціональних прав (див. `tz/07` §7.2) |
| `sec.Role` | `SystemAdministrator`, `TemplateAdministrator`, `PeriodAdministrator`, `DataEntry`, `Approver`, `Viewer`, `Auditor` |
| `sec.PasswordPolicy` | мінімум 12 символів, блокування після 5 спроб |
| `uom.Dimension` | `Mass`, `Volume`, `Energy`, `Time`, `Temperature`, `Amount`, `Dimensionless`, `MassFlow`, `MassPerMass`, `MassPerEnergy`, `MassPerVolume` |
| `uom.Unit` | базові: `kg`, `m3`, `J`, `s`, `K`, `mol`; похідні: `t`, `g`, `mg`, `l`, `GJ`, `MWh`, `h`, `min`, `day`, `year`, `degC`, `g_per_s`, `t_per_year`, `kg_per_t`, `g_per_GJ`, `mg_per_m3`, `kg_per_m3` |
| `doc.PeriodPolicy` | `ECR-Standard`: `Open=0`, `Grace=15`, `HardClose=45`, `YearGrace=45` |
| `sec.User` | `admin` з роллю `SystemAdministrator`; пароль задається змінною оточення при першому старті |

> **Одиниці — не «довідник за смаком».** Набір вище фіксований, бо на нього
> посилаються фікстури і тести конверсій. Додавати можна, змінювати
> `FactorToBase` наявних — ні.
