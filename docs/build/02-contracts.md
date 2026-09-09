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
    public DateTime UtcNow { get; }
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
    // Grace = 2 і Closed = 3 прибрані (D-123): вони мають сенс лише для
    // ПЕРІОДУ. Значення 4 збережено, щоб не переписувати збережені рядки.
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
    SimulationReadOnly = 13,

    /// <summary>
    /// Місяць поза вікном дії запису довідника, на який посилається рядок
    /// (<c>ФВ-5.20</c>, правило <c>SourceWindow</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Дозвіл на викид виданий на строк. Число за місяць, у якому він не
    /// діяв, — не «зайві дані», а заявлений викид без дозволу: рівно те, за
    /// що штрафує регулятор. Чинне рішення блокувало такі місяці процедурою
    /// <c>ApplyPermitMonthLocks</c>; тут це рішення про доступ, як і решта.
    ///
    /// ⚠ Причину я вже додавав був раніше і ВІДКОТИВ: механізму, здатного
    /// її поставити, не існувало, а причина відмови, яку ніщо не повертає,
    /// — це той самий дефект, що весь <c>A7</c>. Тепер механізм є
    /// (<c>PeriodAccessRuleKind.SourceWindow</c>).
    /// </remarks>
    OutsidePermitWindow = 14
}

/// <summary>
/// Вид правила доступу до періоду (<c>ФВ-2.15</c>).
/// </summary>
/// <remarks>
/// ⛔ Шість типів, а не «потрібні зараз». Перелік, половина якого кидає
/// <c>NotSupported</c>, — це та сама мертва гілка, з якою боровся весь
/// <c>A7</c>: механізм оголошений і недосяжний.
///
/// ⚠ Кожен тип заміняє конкретний шматок VBA чинного рішення. Це не
/// абстракція «на майбутнє»: без них кнопка <c>Protect</c> не переноситься.
/// </remarks>
public enum PeriodAccessRuleKind : byte
{
    /// <summary>Завжди лише читання. Заміняє <c>Range(...).Locked = True</c>.</summary>
    AlwaysReadOnly = 0,

    /// <summary>Рядки-заголовки заблоковані. Заміняє масиви номерів у <c>Protection.bas</c>.</summary>
    HeaderRows = 1,

    /// <summary>
    /// Редагується лише період у стані <c>Open</c> або <c>Grace</c>.
    /// Основна логіка <c>ToggleProtection</c>; поведінка за замовчуванням.
    /// </summary>
    EditablePeriodOnly = 2,

    /// <summary>Вікно «поточний період ± N». Заміняє «попередній місяць за cutoff day».</summary>
    RelativeWindow = 3,

    /// <summary>
    /// Вікно береться з довідника: дати дії запису, на який посилається рядок.
    /// </summary>
    /// <remarks>
    /// Заміняє <c>ApplyPermitMonthLocks</c> і є механізмом <c>ФВ-5.20</c>:
    /// дозвіл із вікном дії блокує місяці поза цим вікном.
    /// </remarks>
    SourceWindow = 4,

    /// <summary>Довільна умова над значеннями рядка; діалект шаблонів.</summary>
    Expression = 5
}

/// <summary>Поведінка поза вікном доступу до періоду (ФВ-2.16).</summary>
/// <remarks>
/// ⚠ <c>ФВ-2.16</c> називає три поведінки: заборонити, дозволити з
/// позначкою, дозволити після явного підтвердження. Перші дві були тут із
/// самого початку під іменами <c>ReadOnly</c> і <c>Warn</c>; третьої
/// бракувало, і саме її вимагає перенос <c>LockAndClear</c> чинного
/// рішення — там користувач підтверджував очищення місяця.
///
/// ⛔ Числа НЕ переставлені: вони лежать у <c>tinyint</c> у базі, і зсув
/// значень перетворив би «заборонити» на «попередити» в кожному наявному
/// правилі мовчки.
/// </remarks>
public enum OutOfWindowBehavior : byte
{
    /// <summary>Сховати аркуш або таблицю цілком.</summary>
    Hide = 0,

    /// <summary>Заборонити ввід — «заборонити» з <c>ФВ-2.16</c>.</summary>
    ReadOnly = 1,

    /// <summary>Дозволити з позначкою — правка лишається, але помічена.</summary>
    Warn = 2,

    /// <summary>Дозволити після ЯВНОГО підтвердження користувача.</summary>
    AllowWithConfirmation = 3
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
/// Арифметичний режим версії методології (ФВ-9.9). <c>Legacy</c> рахує в
/// <c>double</c> із семантикою NCalc 1.3.8 і банківським округленням —
/// заради сумісності чисел, а не заради побітової рівності (E-6).
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

using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Domain.ValueObjects;

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
        {
                throw new ArgumentOutOfRangeException(nameof(year), year, "Рік має бути в межах 1900..9999.");
        }
        if (sequence is < 1 or > 99)
        {
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Номер періоду має бути в межах 1..99.");
        }
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

    /// <remarks>
    /// ⚠ <see cref="CultureInfo.InvariantCulture"/> обов'язково: ключ їде в
    /// SQL, у ключі кешу <c>v{id}:r{rev}</c> і в URL. Локаль сервера не має
    /// права на нього впливати (`docs/tz/08-nfr.md` §90, `Q-040`).
    /// </remarks>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
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

using Ecr.Domain.Enums;

namespace Ecr.Domain.ValueObjects;

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
        if (ValueString is not null)
        {
            filled++;
        }
        if (ValueNumeric is not null)
        {
            filled++;
        }
        if (ValueDate is not null)
        {
            filled++;
        }
        if (ValueBool is not null)
        {
            filled++;
        }
        if (ValueRegistryEntryId is not null)
        {
            filled++;
        }
        if (ValueUnitId is not null)
        {
            filled++;
        }
        return IsEmpty ? filled == 0 : filled == 1;
    }
}
```

```csharp
// src/Ecr.Domain/ValueObjects/LocalizedText.cs

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Domain.ValueObjects;

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
        if (_values.TryGetValue(language, out var v))
        {
            return v;
        }
        if (_values.TryGetValue(fallback, out var f))
        {
            return f;
        }
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

using System.Text.RegularExpressions;

namespace Ecr.Domain.ValueObjects;

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

using System.Text.RegularExpressions;

namespace Ecr.Domain.ValueObjects;

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

using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

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

using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

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
        if (Denies.Contains(key))
        {
            return GrantLevel.None;
        }
        return Grants.TryGetValue(key, out var level) ? level : GrantLevel.None;
    }
}
```

```csharp
// src/Ecr.Application/Security/IAccessDecisionService.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    public Task<EditDecision> CanApproveAsync(
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

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

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
    public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>Значення конкретних комірок.</summary>
    public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Застосовує набір змін однією транзакцією. Часткове застосування
    /// заборонене: або весь батч, або нічого (B04 §2.3).
    /// Бюджет: p95 &lt; 150 мс на 100 комірок.
    /// </summary>
    public Task ApplyAsync(CellChangeSet changes, CancellationToken ct);

    /// <summary>Масове завантаження через <c>SqlBulkCopy</c>: імпорт, генератор, міграція.</summary>
    public Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct);
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

using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Кеш метаданих шаблону. Опублікована версія структурно незмінна, тому ключ
/// <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна правка
/// створює новий ключ, а не псує старий (D-16). Це прибирає когерентність кешу
/// між інстансами як клас проблеми.
/// </summary>
public interface IMetadataCache
{
    /// <summary>Повна структура версії шаблону.</summary>
    public Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Скидає запис. Потрібно лише після <c>Publish</c> або міграції.</summary>
    public Task InvalidateAsync(int templateVersionId, CancellationToken ct);
}
```

```csharp
// src/Ecr.Application/Ports/IFormulaEngine.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Ports;

/// <summary>
/// Рушій виразів. Один парсер на обидва діалекти; NCalc використовується як
/// обчислювач, а не як парсер нашої мови (D-19, D-20).
/// Граматика — <see href="02b-expressions.md">02b-expressions.md</see>.
/// </summary>
public interface IFormulaEngine
{
    /// <summary>Розбирає вираз. Помилка синтаксису — результат, а не виняток.</summary>
    public ParseResult Parse(string expression, ExpressionDialect dialect);

    /// <summary>
    /// Витягує залежності виразу. Діапазони рядків розкриваються в явний список
    /// <c>RowKey</c> на момент <c>Publish</c> — у рантаймі діапазонів не існує (B03 §4).
    /// </summary>
    /// <remarks>
    /// ⛔ Знімок приходить ПАРАМЕТРОМ, а не з кешу метаданих (<c>H-3</c>,
    /// директива №06 §1): і редактор виразів, і публікація працюють над
    /// чернеткою, якої в кеші немає за побудовою, — тому доти методом порту не
    /// міг скористатися ніхто. <c>null</c> означає «структури немає»: у
    /// діалекті методологій посилань на комірки не буває.
    /// </remarks>
    public DependencyExtraction ExtractDependencies(
        ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);

    /// <summary>
    /// Топологічний порядок обчислення. Цикл повертається як помилка публікації,
    /// а не як тихо неправильне число (ФВ-9.4).
    /// </summary>
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// Оголошені поруч із портом — за конвенцією самого пакета (пор. IBackgroundJobScheduler.cs,
// де в тому самому файлі живуть IBackgroundJob, IJobProgress і JobStatus).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Розкрита залежність формули — контрактна проєкція <c>cfg.FormulaDependency</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Поля дослівно повторюють колонки
/// <c>cfg.FormulaDependency</c> (`02a-db-schema.md` рядок 401) і тип
/// <c>Ecr.Expressions.Binding.ExtractedDependency</c> з `05d`, а
/// <c>FormulaEngine.ExtractDependencies</c> у своєму <c>TODO</c> прямо каже
/// «делегувати dependencyExtractor і <b>спроєктувати в контрактний тип</b>».
/// Колонки <c>Id</c>, <c>SourceKind</c>, <c>FormulaDefId</c>, <c>BindingId</c>
/// сюди не входять: їх проставляє той, хто зберігає залежність, а не той, хто
/// її витягує з виразу.
/// </remarks>
/// <param name="DependsOnKind">0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</param>
/// <param name="TableDefId">Таблиця, на яку вказує залежність.</param>
/// <param name="RowKey"><b>Конкретний</b> рядок; <c>null</c> для предиката (B03 §4).</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="FilterJson">Предикат для <c>RowMode = Dynamic</c>.</param>
/// <param name="PeriodOffset"><c>[Period:-1]</c> → −1.</param>
/// <param name="SortOrder">Позиція в розкритому діапазоні.</param>
public sealed record FormulaDependencyRef(
    byte DependsOnKind,
    int? TableDefId,
    string? RowKey,
    int? ColumnDefId,
    string? FilterJson,
    short? PeriodOffset,
    int SortOrder);

/// <summary>
/// Результат витягування: залежності і зауваження, здобуті одним обходом.
/// </summary>
/// <remarks>
/// ⛔ Зауваження повертаються РАЗОМ із залежностями: резолвінг посилань і є той
/// самий обхід — він або дає залежність, або пояснює, чому не дав. Два обходи
/// дали б два переліки зауважень, які розходяться, а на їхній тотожності
/// тримається <c>ФВ-9.15a</c>.
/// </remarks>
/// <param name="Dependencies">Розкриті залежності виразу.</param>
/// <param name="Diagnostics">Що не резолвилося; порожньо — усе резолвилося.</param>
public sealed record DependencyExtraction(
    IReadOnlyList<FormulaDependencyRef> Dependencies,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);

/// <summary>
/// Контекст витягування залежностей: те, чого немає в самому виразі, але без
/// чого скорочені форми посилань не резолвляться (02b §3.1).
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Поля дослівно повторюють параметри
/// <c>DependencyExtractor.Extract(AstNode, int currentTableDefId,
/// string? currentRowKey, …, int? currentColumnDefId)</c> і
/// <c>ReferenceResolver.Resolve(...)</c> з `05d`.
///
/// ⚠ Поля <c>TemplateVersionId</c> тут БІЛЬШЕ НЕМАЄ (<c>H-3</c>): воно існувало
/// заради того, щоб порт сам дістав знімок із кешу, а знімок тепер приходить
/// параметром і сам несе свою версію.
/// </remarks>
/// <param name="CurrentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
/// <param name="CurrentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
/// <param name="CurrentColumnDefId">
/// Колонка, яку підставляє плейсхолдер <c>{Month}</c>; <c>null</c> — формула не
/// прив'язана до місячної колонки.
/// </param>
public sealed record DependencyContext(
    int CurrentTableDefId,
    string? CurrentRowKey,
    int? CurrentColumnDefId);

/// <summary>
/// Вузол графа обчислення для <see cref="IFormulaEngine.BuildEvaluationOrder"/> —
/// контрактна проєкція <c>cfg.FormulaDef</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде</b> (спершу було «слабке»; уточнено за
/// схемою після рев'ю Етапу 0). Поля відповідають колонкам
/// <c>cfg.FormulaDef</c> (<c>02a</c> рядок 374): формула ідентифікується
/// <c>Id</c>, прив'язана до <c>TableDefId</c>, а її <c>Scope</c> визначає,
/// котре з <c>ColumnDefId</c>/<c>RowDefId</c> заповнене — це закріплено
/// перевіркою <c>CK_Formula_Scope</c>. Саме тому вузол оперує
/// <c>RowDefId</c>, а не <c>RowKey</c>: формула належить <b>визначенню</b>
/// рядка, а не його ключу.
/// Результат сортування лягає в <c>cfg.FormulaDef.EvaluationOrder</c> — воно
/// «обчислюється при <c>Publish</c>, не в рантаймі» (ФВ-9.4).
/// </remarks>
/// <param name="FormulaDefId">Ідентифікатор формули — він же вузол графа.</param>
/// <param name="TableDefId">Таблиця, якій належить формула.</param>
/// <param name="Scope">Рівень: колонка, рядок або комірка.</param>
/// <param name="ColumnDefId">Колонка; заповнена для <c>Column</c> і <c>Cell</c>.</param>
/// <param name="RowDefId">Рядок; заповнений для <c>Row</c> і <c>Cell</c>.</param>
/// <param name="DependsOnFormulaDefIds">Формули, від яких залежить ця.</param>
public sealed record FormulaNode(
    int FormulaDefId,
    int TableDefId,
    FormulaScope Scope,
    int? ColumnDefId,
    int? RowDefId,
    IReadOnlyList<int> DependsOnFormulaDefIds);
```

```csharp
// src/Ecr.Application/Ports/ICalculationModule.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Модуль розрахунку емісій. <b>Окрема точка розширення від</b>
/// <see cref="IFormulaEngine"/>: не всі обчислення є формулами (ФВ-9.2).
/// </summary>
public interface ICalculationModule
{
    /// <summary>Код модуля, унікальний у системі.</summary>
    public string Code { get; }

    /// <summary>Рівень драбини виразності, який реалізує модуль.</summary>
    public CalculationLevel Level { get; }

    /// <summary>Чи здатний модуль обробити цю методологію.</summary>
    public bool CanHandle(MethodologyDescriptor methodology);

    /// <summary>Виконує розрахунок. Не пише в БД — повертає результат.</summary>
    public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Опис версії методології — те, за чим модуль вирішує, чи здатний він її
/// обробити, і за чим рушій знає, як саме рахувати.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Склад полів визначений двома
/// джерелами. По-перше, <c>GenericCalculationModule.CanHandle</c> у своєму
/// <c>TODO</c> вимагає <c>methodology.Level == Configuration</c> — отже
/// <see cref="Level"/> обов'язковий. По-друге, <c>MethodologyVersion</c>
/// (`05b`) називає три режими, кожен з яких <b>визначає числа</b>:
/// <c>NumericMode</c> (момент округлення, ФВ-9.9), <c>CalendarMode</c>
/// (тривалість періоду, ФВ-16.11) і <c>TraceLevel</c> (обсяг журналу, ФВ-9.13).
/// Модуль не може рахувати, не знаючи їх, і читати сутність сам він не має
/// права — тому вони тут.
/// Опис <b>не</b> містить формул, констант і речовин: їх модуль бере через
/// власні залежності, а descriptor лишається легким — його передають на
/// кожен рядок.
/// </remarks>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyVersionId">Версія — те, що реально рахує.</param>
/// <param name="Code">Код методології.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Level">Рівень драбини виразності.</param>
/// <param name="NumericMode">Арифметика; <c>Legacy</c> відтворює числа чинної системи.</param>
/// <param name="CalendarMode">Джерело тривалості періоду.</param>
/// <param name="TraceLevel">Скільки писати в <c>calc.CalculationStep</c>.</param>
public sealed record MethodologyDescriptor(
    int MethodologyId,
    int MethodologyVersionId,
    string Code,
    string VersionNumber,
    CalculationLevel Level,
    NumericMode NumericMode,
    CalendarMode CalendarMode,
    TraceLevel TraceLevel);

/// <summary>
/// Вхід розрахунку — <b>один рядок документа</b> з усіма аргументами.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Гранульованість «один рядок»
/// задана <c>CalculationInputBuilder.BuildAsync</c>: «для кожного рядка зібрати
/// CalculationInput» — і тим, що метод повертає
/// <c>IReadOnlyList&lt;CalculationInput&gt;</c> на набір <c>rowKeys</c>.
/// Склад <see cref="CalculationArgument"/> дослівно повторює колонки
/// <c>calc.CalculationInput</c> (`02a-db-schema.md` рядок 1158):
/// <c>ArgumentCode</c>, <c>Value</c>, <c>ValueString</c>, <c>UnitId</c>.
/// <c>DocumentId</c> і <c>SourceRowKey</c> — теж колонки тієї таблиці.
/// <see cref="TableInstanceId"/> і <see cref="PeriodKey"/> додано мною:
/// без них модуль не має календарного контексту, а <c>CalendarMode</c> без
/// періоду не працює (D-78).
/// </remarks>
/// <param name="Methodology">Версія методології, яку виконують.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableInstanceId">Таблиця документа, з якої взято рядок.</param>
/// <param name="PeriodKey">Період — потрібен для календарного контексту.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> для розрахунку рівня таблиці.</param>
/// <param name="Arguments">Аргументи в одиницях джерела.</param>
public sealed record CalculationInput(
    MethodologyDescriptor Methodology,
    long DocumentId,
    long TableInstanceId,
    PeriodKey PeriodKey,
    string? SourceRowKey,
    IReadOnlyList<CalculationArgument> Arguments);

/// <summary>Один аргумент розрахунку — рядок <c>calc.CalculationInput</c>.</summary>
/// <remarks>
/// Значення зберігається <b>в одиниці джерела</b>: конверсія на межі, а не в
/// сховищі, інакше повторний перерахунок з архіву дасть інший результат (ФВ-16.9).
/// </remarks>
/// <param name="ArgumentCode">Ім'я аргументу — те, на що посилається <c>@Arg</c>.</param>
/// <param name="Value">Числове значення; <c>null</c> — порожньо.</param>
/// <param name="ValueString">Текстове значення для нечислових аргументів.</param>
/// <param name="UnitId">Одиниця значення; <c>null</c> — безрозмірне.</param>
public sealed record CalculationArgument(
    string ArgumentCode,
    decimal? Value,
    string? ValueString,
    int? UnitId);

/// <summary>
/// Результат розрахунку одного рядка: <b>усі</b> виходи методології плюс трейс.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Контейнер, а не один рядок, бо
/// <c>GenericCalculationModule.ExecuteAsync</c> повертає <b>один</b>
/// <c>CalculationOutput</c> на вхід, а рахувати має «для КОЖНОЇ речовини
/// методології … виходи (tons, gsec)» — тобто кілька значень.
/// <see cref="CalculationOutputValue"/> лягає 1:1 на <c>calc.CalculationResult</c>
/// (`02a` рядок 1131), <see cref="CalculationTraceStep"/> — на
/// <c>calc.CalculationStep</c> (`02a` рядок 1179).
/// Модуль у БД не пише (D-69) — запис робить реалізація
/// <c>ICalculationResultStore</c>.
/// </remarks>
/// <param name="DocumentId">Документ.</param>
/// <param name="SourceRowKey">Рядок документа.</param>
/// <param name="Values">Обчислені виходи.</param>
/// <param name="Trace">Кроки трейсу; порожній список, якщо <c>TraceLevel = Off</c>.</param>
public sealed record CalculationOutput(
    long DocumentId,
    string? SourceRowKey,
    IReadOnlyList<CalculationOutputValue> Values,
    IReadOnlyList<CalculationTraceStep> Trace);

/// <summary>Один обчислений вихід — рядок <c>calc.CalculationResult</c>.</summary>
/// <param name="MethodologyVersionId">Версія, що дала число.</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> для виходів без речовини.</param>
/// <param name="OutputCode">Код виходу з <c>calc.MethodologyOutput</c>.</param>
/// <param name="Value">Значення. <c>float</c> заборонений (D-30).</param>
/// <param name="UnitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
public sealed record CalculationOutputValue(
    int MethodologyVersionId,
    int? SubstanceEntryId,
    string OutputCode,
    decimal Value,
    int UnitId);

/// <summary>Крок трейсу — рядок <c>calc.CalculationStep</c>.</summary>
/// <remarks>
/// Обсяг трейсу керується <c>TraceLevel</c> версії: керуємо тим, <b>що</b>
/// пишемо, а не скільки зберігаємо (ЗБР-3).
/// </remarks>
/// <param name="StepOrder">Порядок кроку.</param>
/// <param name="StepCode">Код кроку — зазвичай код формули або виходу.</param>
/// <param name="Expression">Вираз як його бачив рушій.</param>
/// <param name="Value">Значення кроку.</param>
/// <param name="TraceJson">Довільна деталізація: підставлені аргументи, константи.</param>
public sealed record CalculationTraceStep(
    int StepOrder,
    string StepCode,
    string? Expression,
    decimal? Value,
    string? TraceJson);
```

```csharp
// src/Ecr.Application/Ports/IExternalDataSource.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання із зовнішнього джерела. PI AF — <b>виключно джерело</b>: система в
/// нього нічого не пише (D-44), тому парного <c>IExternalDataSink</c> не існує.
/// </summary>
public interface IExternalDataSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    public ExternalTransport Transport { get; }

    /// <summary>Каталог сутностей джерела — для конфігуратора, щоб не вводити імена руками.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct);

    /// <summary>
    /// Читає діапазон. Ідемпотентно: повторний запуск того самого діапазону не
    /// дублює даних (ФВ-11.3).
    /// </summary>
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Елемент каталогу джерела — те, що конфігуратор бачить у списку і з чого
/// створює <c>ext.SourceEntity</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове</b> (спершу «слабке»; уточнено після
/// рев'ю Етапу 0). Поля дослівно відповідають колонкам <c>ext.SourceEntity</c>
/// (<c>02a</c> рядок 1342): <c>Code</c>, <c>DisplayName</c>, <c>EntityPath</c> —
/// саме їх заповнює «каталог сутностей джерела — для конфігуратора, щоб не
/// вводити імена руками». Адаптер при цьому <b>не створює артефактів у базі
/// джерела</b> (ФВ-11.2): <c>Discover</c> лише читає.
/// <see cref="SourceUnitSymbol"/> додано мною, бо обидві реалізації
/// <c>DiscoverAsync</c> у своїх <c>TODO</c> пишуть «збирати атрибути
/// <b>з їхнім UOM</b>»: одиниця джерела — «найчастіше джерело мовчазних
/// розбіжностей у числах» (ФВ-16.9), і побачити її треба вже в каталозі.
/// <c>Id</c>, <c>DataSourceId</c>, <c>RegistryDefId</c>, <c>IsActive</c> сюди
/// не входять: це наші поля, а не поля джерела.
/// </remarks>
/// <param name="Code">Унікальний у межах джерела код.</param>
/// <param name="DisplayName">Людська назва.</param>
/// <param name="EntityPath">Шлях в ієрархії AF.</param>
/// <param name="SourceUnitSymbol">UOM атрибута в термінах джерела; <c>null</c> — безрозмірний.</param>
/// <param name="DataType">Тип значення в термінах джерела.</param>
public sealed record SourceEntityDescriptor(
    string Code,
    string? DisplayName,
    string? EntityPath,
    string? SourceUnitSymbol,
    string? DataType);

/// <summary>Запит на читання діапазону з джерела.</summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — слабке (здогадка).</b> Форма виведена з
/// <c>CollectionRunner.RunAsync(int sourceEntityId, DateTime from, DateTime to, …)</c>
/// і з таблиці <c>itg.CollectionRun</c> (<c>SourceEntityId</c>,
/// <c>RangeFrom</c>, <c>RangeTo</c>). <see cref="SourcePath"/> потрібен, бо
/// природний ключ <c>ext.RawDataPoint</c> — це
/// <c>(SourceEntityId, SourcePath, Timestamp)</c>, а одна сутність джерела може
/// мати кілька атрибутів. <see cref="MaxPoints"/> додано мною: обидві
/// реалізації <c>ReadAsync</c> у <c>TODO</c> вимагають «батчі обмеженого розміру».
/// </remarks>
/// <param name="DataSourceId">Джерело — визначає транспорт і облікові дані.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourcePath">Шлях атрибута; частина природного ключа точки.</param>
/// <param name="FromUtc">Початок діапазону, включно.</param>
/// <param name="ToUtc">Кінець діапазону, виключно.</param>
/// <param name="MaxPoints">Обмеження розміру батча.</param>
public sealed record CollectionRequest(
    int DataSourceId,
    int SourceEntityId,
    string SourcePath,
    DateTime FromUtc,
    DateTime ToUtc,
    int MaxPoints);

/// <summary>Прочитане з джерела плюс те, що прочитати не вдалося.</summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Наявність
/// <see cref="FailedIntervals"/> — не прикраса, а пряма вимога <c>TODO</c>
/// обох реалізацій: «часткова відмова батча — це НЕ загальний провал: успішні
/// точки зберегти, невдалі повернути в catch-up». Без цього поля адаптер може
/// повідомити лише «все добре» або «все погано», і журнал покриття
/// (<c>itg.CollectionCoverage</c>) стане неправдивим.
/// </remarks>
/// <param name="Points">Точки в <b>одиниці джерела</b> (ФВ-16.9).</param>
/// <param name="FailedIntervals">Інтервали, які треба дозібрати.</param>
/// <param name="ErrorCode">Код помилки джерела (<c>ECR-INT-0503</c>); <c>null</c> — відмов не було.</param>
public sealed record CollectionResult(
    IReadOnlyList<SourceDataPoint> Points,
    IReadOnlyList<TimeInterval> FailedIntervals,
    string? ErrorCode);

/// <summary>Одна прочитана точка — рядок <c>ext.RawDataPoint</c> до збереження.</summary>
/// <param name="SourcePath">Шлях атрибута.</param>
/// <param name="Timestamp">Мітка часу точки.</param>
/// <param name="ValueNumeric">Числове значення в одиниці джерела.</param>
/// <param name="ValueString">Текстове значення для нечислових тегів.</param>
/// <param name="SourceUnitSymbol">UOM джерела; конверсія — на межі, із записом у журнал.</param>
/// <param name="Quality">Якість у термінах джерела.</param>
public sealed record SourceDataPoint(
    string SourcePath,
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    string? SourceUnitSymbol,
    string? Quality);

/// <summary>Часовий інтервал; кінець виключно.</summary>
public sealed record TimeInterval(DateTime FromUtc, DateTime ToUtc);
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
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Планує задачу за cron-виразом.</summary>
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Скасовує задачу.</summary>
    public Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>Стан виконання для UI прогресу.</summary>
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);
}

/// <summary>Фонова задача.</summary>
public interface IBackgroundJob
{
    /// <summary>Виконує задачу. Має бути ідемпотентною і відновлюваною.</summary>
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct);
}

/// <summary>Канал прогресу для довгих операцій (усе довше ~5 с — у фон).</summary>
public interface IJobProgress
{
    public Task ReportAsync(int percent, string? message, CancellationToken ct);
}

/// <summary>Стан фонової задачі.</summary>
public sealed record JobStatus(string JobId, string State, int Percent, string? Message, string? Error);

/// <summary>
/// Маркер задачі перерахунку.
/// </summary>
/// <remarks>
/// ⚠ Потрібен тому, що <see cref="IBackgroundJobScheduler.EnqueueAsync{TJob}"/>
/// обмежений <c>where TJob : IBackgroundJob</c>, а конкретні задачі живуть в
/// <c>Ecr.Infrastructure</c>, якого <c>Ecr.Application</c> не бачить і бачити
/// не має. Маркер дає use-case назвати задачу, не знаючи її реалізації.
/// </remarks>
public interface IRecalculationJob : IBackgroundJob;
```

```csharp
// src/Ecr.Application/Ports/ISqlCapabilities.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Можливості СУБД, визначені при старті (АРХ-7). Редакція впливає <b>лише</b>
/// на операційні стратегії — ніколи на модель даних, семантику чи числа.
/// Тому в бізнес-коді звертатися сюди заборонено: тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>.
/// </summary>
public interface ISqlCapabilities
{
    public SqlEditionMode EffectiveMode { get; }
    public string EditionName { get; }
    public int ProductMajorVersion { get; }
    public bool IsReadCommittedSnapshotOn { get; }

    /// <summary>Перебудова індексів без блокування (<c>ONLINE = ON</c>).</summary>
    public bool SupportsOnlineIndexRebuild { get; }

    /// <summary>Resource Governor для ізоляції фонових задач від інтерактивного піку.</summary>
    public bool SupportsResourceGovernor { get; }

    /// <summary>Розмір батча архівації, підібраний під редакцію.</summary>
    public int ArchiveBatchSize { get; }
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
    public Task<int> SaveChangesAsync(CancellationToken ct);
    public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);
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
    public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct);

    /// <summary>Поточна версія каталогу. Змінюється будь-яким записом.</summary>
    public Task<int> GetRevisionAsync(CancellationToken ct);
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

using Ecr.Application.Security;
using Ecr.Domain.Enums;

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
    public Task<long> StartAsync(int actorUserId, int subjectUserId, string reason, CancellationToken ct);

    public Task EndAsync(long sessionId, CancellationToken ct);

    /// <summary>
    /// Профіль суб'єкта для активного сеансу. **Не кешується** (`ФВ-6.16a` п. 4):
    /// покладений під ключ суб'єкта, він дістався б справжньому користувачеві
    /// з прапорцем <c>IsSimulation</c>. Симуляція рідкісна — перебудова дешева.
    /// </summary>
    public Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct);
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
    public Task<int> ScanAllAsync(CancellationToken ct);

    /// <summary>
    /// Точковий перерахунок після зміни вікна дії запису. **Знімає** ознаку
    /// так само, як ставить: інакше виправлення довідника не розблокувало б
    /// <c>Submit</c>.
    /// </summary>
    public Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct);
}
```

---



> Тип живе в `Ecr.Expressions`, а не в `Ecr.Application`: він належить рушієві виразів, а порт лише його повертає.

```csharp
// src/Ecr.Expressions/Evaluation/EvaluationResult.cs
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Результат обчислення виразу: значення плюс діагностики.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Файл оголошений у дереві
/// `05-skeleton.md` §1, але секції з вмістом у `05d` немає (Q-015). Форма
/// виведена з <c>FormulaEngine.Evaluate</c>, чий <c>TODO</c> каже дослівно:
/// «делегувати evaluator; <b>загорнути результат і діагностики</b>».
/// <c>Evaluator.Evaluate</c> повертає <see cref="ExpressionValue"/>, а
/// діагностики в пакеті мають рівно один тип —
/// <see cref="ExpressionDiagnostic"/> (02b §11).
///
/// Помилка обчислення — це <b>значення</b> всередині
/// <see cref="ExpressionValue"/> (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>,
/// <c>#UNIT</c>, <c>#CYCLE</c>), а не запис у <see cref="Diagnostics"/>:
/// одна зіпсована комірка не валить перерахунок таблиці (02b §6.4).
/// У <see cref="Diagnostics"/> потрапляє те, що стосується <b>виразу</b>, а не
/// його значення — нерезолвлене посилання, невідома функція.
/// </remarks>
/// <param name="Value">Обчислене значення; може бути <c>null</c>-значенням або помилкою.</param>
/// <param name="Diagnostics">Діагностики виразу; порожній список — усе гаразд.</param>
public sealed record EvaluationResult(
    ExpressionValue Value,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);
```

> Уведений за `Q-018` (варіант B) — щоб `Ecr.Calculations` не залежав від EF Core.

```csharp
// src/Ecr.Application/Ports/IMethodologyStore.cs

using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання конфігурації методологій зі сховища.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.MethodologyResolver</c> був типізований напряму на
/// <c>Ecr.Infrastructure.Persistence.EcrDbContext</c>, чого не передбачає
/// <c>05-skeleton.md</c> §4. Порт лишає <b>логіку</b> підбору версії і
/// зіставлення рядків у <c>Ecr.Calculations</c>, а сховище — в
/// <c>Ecr.Infrastructure</c>: інакше проєкт, у якому живуть числа викидів,
/// неможливо було б протестувати без бази.
/// </remarks>
public interface IMethodologyStore
{
    /// <summary>
    /// Опубліковані версії методології. Вибір чинної на дату робить викликач:
    /// правило «максимальний <c>EffectiveFrom</c> ≤ дата» — це домен, не сховище.
    /// </summary>
    public Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsAsync(int methodologyId, CancellationToken ct);

    /// <summary>Активні правила прив'язки версії, впорядковані за <c>Priority</c>.</summary>
    public Task<IReadOnlyList<MethodologyRule>> GetRulesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Формули версії в порядку обчислення.</summary>
    public Task<IReadOnlyList<MethodologyFormula>> GetFormulasAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Речовини версії: для кожної рахуються власні виходи.</summary>
    public Task<IReadOnlyList<MethodologySubstance>> GetSubstancesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Оголошені виходи версії — з обов'язковими одиницями (ФВ-16.6).</summary>
    public Task<IReadOnlyList<MethodologyOutput>> GetOutputsAsync(int methodologyVersionId, CancellationToken ct);
}
```

> Уведений за `Q-018`. Віддає **кандидатів**, а не значення: вибір за категорією, речовиною і датою — правило предметної області (`ФВ-16.5`), і воно лишається в `Ecr.Calculations`.

```csharp
// src/Ecr.Application/Ports/IConstantStore.cs

using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання констант методології зі сховища.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b>
/// Порт віддає <b>кандидатів</b>, а не готове значення: звуження за категорією
/// і речовиною та вибір темпорального інтервалу — це правила предметної
/// області (ФВ-16.5), і живуть вони в
/// <c>Ecr.Calculations.ConstantResolver</c>. Зокрема правило «кілька кандидатів
/// на одну дату — помилка конфігурації, а не привід узяти перший» неможливо
/// перевірити, якщо сховище вже вибрало один запис.
/// </remarks>
public interface IConstantStore
{
    /// <summary>
    /// Усі константи версії з цим кодом — разом із темпоральними варіантами
    /// та варіантами за категорією і речовиною.
    /// </summary>
    public Task<IReadOnlyList<MethodologyConstant>> GetCandidatesAsync(
        int methodologyVersionId, string code, CancellationToken ct);
}
```

> Уведений за `Q-018`. Пише лише в `calc.CalculationResult` і `calc.CalculationStep`; у `doc.CellValue` результати методологій не потрапляють ніколи (`D-69`).

```csharp
// src/Ecr.Application/Ports/ICalculationResultStore.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис результатів прогону розрахунку.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.CalculationOutputWriter</c> був типізований напряму на
/// <c>EcrDbContext</c> і <c>BulkCellLoader</c>.
///
/// Пише <b>тільки</b> в <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69):
/// інакше нічний перерахунок писав би десятки мільйонів рядків у партиції
/// документів і роздував <c>aud.CellChange</c>.
/// </remarks>
public interface ICalculationResultStore
{
    /// <summary>
    /// Резервує діапазон ідентифікаторів із <c>calc.CalculationResultSeq</c>
    /// одним викликом <c>sp_sequence_get_range</c>.
    /// </summary>
    public Task<long> ReserveResultIdRangeAsync(int count, CancellationToken ct);

    /// <summary>
    /// Пише результати пакетно (<c>SqlBulkCopy</c>). <c>SaveChanges</c> у циклі
    /// заборонений: бюджет річного перерахунку — 10 хвилин (ПРД-13).
    /// </summary>
    public Task WriteResultsAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct);

    /// <summary>
    /// Пише трейс — лише те, що передбачає <paramref name="traceLevel"/>.
    /// Керуємо тим, <b>що</b> пишемо, а не скільки зберігаємо (ЗБР-3).
    /// </summary>
    public Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct);

    /// <summary>Інвалідує залежні зрізи <c>rpt.*</c> після завершення прогону.</summary>
    public Task InvalidateReportSnapshotsAsync(long calculationRunId, CancellationToken ct);

    /// <summary>Числа <b>актуального</b> прогону для документа й періоду (`W6`).</summary>
    public Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct);
}
```

> Уведений за директивою №09, `W6`. Прив'язку результату методології до колонки
> документа (`cfg.CalculationBinding`, `D-69`) доти не створювало **ніщо** — ні
> обробник, ні контролер, ні тест, — і наслідок був повністю мовчазний:
> `RecalculationJob.BindingsAsync` віддавав порожній перелік, оркестратор
> одразу повертав порожній профіль, задача завершувалася `Succeeded` і не
> рахувала нічого. Порожній набір прив'язок помилкою не є, тож ані стан задачі,
> ані журнал про це не казали.
>
> ⛔ Порт окремий від `IMethodologyDraftStore`, і межа тут за ВЛАСНИКОМ, а не за
> зручністю: усе, що вміє той порт, належить ВЕРСІЇ методології і живе в схемі
> `calc`, а прив'язка належить ШАБЛОНУ — вона посилається на `cfg.ColumnDef`,
> переживає всі версії методології одразу (ключ `MethodologyId`, не
> `MethodologyVersionId`) і клонуванням версії не копіюється взагалі.

```csharp
// src/Ecr.Application/Ports/ICalculationBindingStore.cs

using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

public interface ICalculationBindingStore
{
    /// <summary>Прив'язка за трійкою <c>UQ_CalculationBinding</c>; відстежувана.</summary>
    public Task<CalculationBinding?> FindAsync(
        int columnDefId, int methodologyId, string outputCode, CancellationToken ct);

    /// <summary>Усі прив'язки методології, включно з вимкненими.</summary>
    public Task<IReadOnlyList<CalculationBinding>> ListAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>Таблиця колонки; <c>TableDefId</c> прив'язки виводиться, а не приймається.</summary>
    public Task<int?> FindTableOfColumnAsync(int columnDefId, CancellationToken ct);

    /// <summary>Ставить прив'язку в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    public void Add(CalculationBinding binding);
}
```

> Уведений за `Q-018` — щоб `Ecr.Adapters.PiAf` не залежав від EF Core. Джерело істини про покриття — `itg.CollectionCoverage`, а не `Watermark` (`ER-I-03`).

```csharp
// src/Ecr.Application/Ports/ICollectionStore.cs

using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>
/// Стан і результати збору із зовнішніх джерел.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Adapters.PiAf.CollectionRunner</c> і <c>CatchUpPlanner</c> були
/// типізовані напряму на <c>EcrDbContext</c>, хоча <c>05-skeleton.md</c> §4
/// дозволяє адаптерам знати лише <c>Domain</c> і <c>Application</c>.
///
/// Джерело істини щодо того, за які інтервали дані вже є, — це
/// <c>itg.CollectionCoverage</c>, а не <c>Watermark</c> у розкладі:
/// watermark — оптимізація, а не стан, і його втрата не має коштувати даних
/// (ER-I-03).
/// </remarks>
public interface ICollectionStore
{
    /// <summary>Сутність джерела; <c>null</c>, якщо її немає або вона вимкнена.</summary>
    public Task<SourceEntity?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Джерело — воно визначає транспорт. Вибір транспорту це налаштування, не гілка коду (ФВ-11.2).</summary>
    public Task<DataSource?> FindDataSourceAsync(int dataSourceId, CancellationToken ct);

    /// <summary>Створює <c>itg.CollectionRun</c> і повертає його ідентифікатор.</summary>
    public Task<long> StartRunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc,
        bool isCatchUp, int? triggeredByUserId, CancellationToken ct);

    /// <summary>Завершує прогін. Відмова джерела — теж завершення, зі статусом і кодом.</summary>
    public Task FinishRunAsync(
        long collectionRunId, string status, int pointsRetrieved,
        string? errorMessage, CancellationToken ct);

    /// <summary>
    /// Upsert точок за природним ключем <c>(SourceEntityId, SourcePath, Timestamp)</c> —
    /// повторний запуск того самого діапазону не дублює даних (ФВ-11.3).
    /// Значення зберігаються <b>в одиниці джерела</b> (ФВ-16.9).
    /// </summary>
    /// <returns>Скільки точок фактично записано.</returns>
    public Task<int> UpsertRawPointsAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points, CancellationToken ct);

    /// <summary>Записує покриті інтервали в <c>itg.CollectionCoverage</c>.</summary>
    public Task WriteCoverageAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<TimeInterval> covered, CancellationToken ct);

    /// <summary>
    /// Покриті інтервали від <paramref name="notBefore"/> — основа для пошуку
    /// прогалин. Ознака здоров'я інтеграції — саме журнал покриття, а не тиша (ІНТ-3.3).
    /// </summary>
    public Task<IReadOnlyList<TimeInterval>> GetCoverageAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct);
}
```

---



> Уведений за `Q-032`: `R-B7` вимагає атомарного інкременту `PresentationRevision` одним statement із `OUTPUT`, а через `IRepository` це не виразити.

```csharp
// src/Ecr.Application/Ports/ITemplateVersionStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через
/// <see cref="IRepository{T,TId}"/>, бо вони мусять бути атомарними в базі.
/// </summary>
/// <remarks>
/// ⚠ Порт уведений за тією самою причиною, що й порти <c>Q-018</c>: обробник
/// не має права знати про SQL, але <c>R-B7</c> вимагає саме атомарної
/// операції.
///
/// <b>Чому не read-modify-write у застосунку.</b> Інстансів застосунку
/// щонайменше два (<c>D-32</c>). Якби ревізію читали, додавали одиницю і
/// записували, два одночасні патчі дали б однакове нове значення, і другий
/// мовчки затер би перший — при цьому ключ кешу <c>v{id}:r{rev}</c> у клієнтів
/// збігся б із застарілою структурою. Тому інкремент робиться одним
/// <c>UPDATE … SET PresentationRevision = PresentationRevision + 1 OUTPUT
/// inserted.PresentationRevision</c>, і застосунок дізнається результат, а не
/// призначає його.
/// </remarks>
public interface ITemplateVersionStore
{
    /// <summary>
    /// Інкрементує <c>PresentationRevision</c> одним statement і повертає
    /// <b>нове</b> значення з <c>OUTPUT</c>.
    /// </summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Нова ревізія.</returns>
    public Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Чи існують документи, прив'язані до цієї версії.
    /// </summary>
    /// <remarks>
    /// Від відповіді залежить класифікація структурної зміни (ФВ-7.4):
    /// та сама зміна коду колонки без документів <c>Safe</c>, з документами —
    /// <c>Breaking</c> і відмова операції.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct);
}
```

> Уведений за `Q-032`: `ICellStore` віддає **значення** комірок, а для звірки `baseVersion` потрібні **версії рядків** (`B04` §2.3). Розширювати `ICellStore` означало б змінити контракт.

```csharp
// src/Ecr.Application/Ports/IRowStore.cs

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Рядки таблиці документа: ідентичність, версія, створення.
/// </summary>
/// <remarks>
/// ⚠ Порт додано, а не вбудовано в <see cref="ICellStore"/>: той віддає
/// <b>значення</b> комірок, а тут потрібні <b>версії рядків</b>. Розширювати
/// <c>ICellStore</c> означало б змінити контракт (`02-contracts.md` §5), тоді
/// як додавання сусіднього порту нічого не ламає.
///
/// Без цього порту неможливо виконати найважчу вимогу запису: звірити
/// <c>baseVersion</c> кожного зачепленого рядка і відхилити <b>весь</b> батч
/// при розбіжності (B04 §2.3). Читати версії разом зі значеннями не можна —
/// зріз повертає лише непорожні комірки, а рядок може бути зачеплений і
/// таким, у якого всі комірки порожні.
/// </remarks>
public interface IRowStore
{
    /// <summary>
    /// Ідентичність екземпляра таблиці: до якого документа і якої версії
    /// шаблону він належить.
    /// </summary>
    /// <remarks>
    /// Потрібно, бо <c>PatchCellsRequest</c> несе лише <c>TableInstanceId</c>,
    /// а щоб резолвити коди колонок у <c>ColumnDefId</c>, обробнику потрібен
    /// знімок структури — тобто <c>TemplateVersionId</c>. Класти його в запит
    /// не можна: клієнт не має диктувати, за якою версією тлумачити дані.
    /// </remarks>
    public Task<TableInstanceRef> ResolveTableInstanceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>
    /// Поточні версії рядків таблиці: <c>RowKey</c> → hex <c>rowversion</c>.
    /// </summary>
    /// <remarks>
    /// Один виклик на батч, не на рядок: бюджет запису — 300 мс на 100 комірок,
    /// і N запитів у нього не вкладаються.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, string>> GetRowVersionsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Ідентифікатори рядків за ключами: <c>RowKey</c> → <c>TableRow.Id</c>.
    /// </summary>
    public Task<IReadOnlyDictionary<string, long>> GetRowIdsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Створює рядок і повертає його <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// <c>Id</c> береться з <c>SEQUENCE</c> <b>до</b> вставки — це те, що
    /// дозволяє вантажити <c>TableRow</c> і <c>CellValue</c> одним проходом
    /// <c>SqlBulkCopy</c>. З <c>IDENTITY</c> довелося б вставляти рядки,
    /// зчитувати ключі й лише потім комірки.
    /// </remarks>
    public Task<long> CreateRowAsync(
        long tableInstanceId, PeriodKey periodKey, RowKey rowKey, int ordinal, CancellationToken ct);

    /// <summary>
    /// Піднімає <c>ModifiedAt</c> зачеплених рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме це змінює <c>RowVersion</c>. Забути — означає зламати
    /// оптимістичне блокування <b>тихо</b>: наступний запис зі застарілою
    /// <c>baseVersion</c> пройде як коректний, і чужа правка зникне без сліду
    /// (B04 §2.4).
    /// </remarks>
    public Task TouchRowsAsync(IReadOnlyList<long> rowIds, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Збережені ознаки «осиротілості» рядків: <c>TableRow.Id</c> → <c>IsOrphaned</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме <b>читання збереженого поля</b>, а не обчислення. Перевіряти
    /// чинність записів реєстру на кожен зріз означало б додати запит на
    /// кожен рядок і вийти за бюджет 400 мс (ФВ-8.13, <c>D-98</c>).
    /// Ознаку ставить <c>OrphanScanJob</c> уночі.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, bool>> GetOrphanFlagsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);
}

/// <summary>Ідентичність екземпляра таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="DocumentId">Документ, якому він належить.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="TemplateVersionId">Версія шаблону — ключ знімка метаданих.</param>
/// <param name="PeriodKey">Період екземпляра; він же ключ партиції.</param>
public sealed record TableInstanceRef(
    long TableInstanceId, long DocumentId, int TableDefId, int TemplateVersionId, int PeriodKey);
```

---

<a id="error-model"></a>

### Порти, додані етапами 1–5

> ⚠ Розділ не «дельта», а продовження переліку вище: ці порти з'явилися
> разом з етапами, і без них контракт описує систему, якої вже немає.
> Форма стисліша — оголошення без коментарів реалізації: повний текст із
> обґрунтуванням кожного рішення живе у файлі порту, і дублювати його
> дослівно означало б завести два джерела, які розійдуться.
>
> ⛔ Що перелік не відстане знову, стежить архітектурний тест
> `Кожен_порт_застосунку_названий_у_контракті`.

#### `IAuditReader`

Зміна комірки в журналі, як її бачить читач аудиту. Момент зміни в UTC. Звітний період. Документ. Ключ рядка — щоб журнал читався без join. Колонка. Старе значення. Нове значення. Автор — UserId, не SID (R-A2, D-86). Звідки зміна: правка, імпорт, перерахунок, міграція. Зміна в Grace або після Reopen (D-70). public sealed record CellChangeView( DateTime ChangedAt, int PeriodKey, long DocumentId, string RowKey, int ColumnDefId, string? OldValue, string? NewValue, int ChangedByUserId, string Origin, bool IsLateEdit); Читання аудиту. Журнал **тільки читається**: методів зміни тут немає і не буде — журнал, який можна відредагувати, не є доказом.

```csharp
public interface IAuditReader
{
    public interface IAuditReader
    public Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
}
```

#### `IAuditWriter`

Запис аудиту. Пакетний **навмисно**: окремий INSERT на кожну комірку не вкладається в бюджет збереження діапазону (300 мс на 100 комірок).

```csharp
public interface IAuditWriter
{
    public interface IAuditWriter
    public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct);
    public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct);
    public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct);
    public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct);
}
```

#### `ICalculationRunner`

Виконавець прогону розрахунку.

```csharp
public interface ICalculationRunner
{
    public interface ICalculationRunner
    public Task<ModuleProfile> RunAsync(
}
```

#### `ICellPatcher`

Запис комірок від імені інтеграції (D-118).

```csharp
public interface ICellPatcher
{
    public interface ICellPatcher
    public Task<IntegrationWriteResult> ApplyIntegrationAsync(
    public interface ICoverageJournal
    public Task RecordAsync(
}
```

#### `ICollectionRunner`

Виконавець збору із зовнішнього джерела.

```csharp
public interface ICollectionRunner
{
    public interface ICollectionRunner
    public Task RunAsync(
}
```

#### `IDocumentStore`

Документ у переліку. ⛔ Статусу тут немає (D-93). Зведений стан рахується запитом до wf.ApprovalState і віддається окремим полем : скалярний статус був би другим джерелом істини і рано чи пізно показав би Approved на документі, половина аркушів якого ще в Draft. Ідентифікатор. Проєкт. Бізнес-ключ, унікальний у межах проєкту. Момент створення. Скільки аркушів у складі. Стан робочого процесу: аркуш → статус. public sealed record DocumentSummary( long Id, int ProjectId, string BusinessKey, DateTime CreatedAt, int SheetCount, IReadOnlyDictionary SheetStates); Порушення правила складу документа. Група аркушів. Вид правила: 0 RequiresAll, 1 RequiresOne, 2 Optional. Що саме не так. public sealed record CompositionViolation(string SheetGroup, byte RuleKind, string Detail); Читання і створення документів. public interface IDocumentStore { Додає документ разом зі складом аркушів. public Task AddAsync(Document document, CancellationToken ct); Документ за ідентифікатором; null — не існує. public Task FindAsync(long documentId, PeriodKeyFilter period, CancellationToken ct); Сторінка документів проєкту. public Task> ListAsync( int? projectId, PeriodKeyFilter period, CursorRequest page, CancellationToken ct); Перевіряє склад за SheetGroupRule (ФВ-3.2).

```csharp
public interface IDocumentStore
{
    public interface IDocumentStore
    public Task AddAsync(Document document, CancellationToken ct);
    public Task<DocumentSummary?> FindAsync(long documentId, PeriodKeyFilter period, CancellationToken ct);
    public Task<PagedResult<DocumentSummary>> ListAsync(
    public Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
    public Task<string> NextBusinessKeyAsync(int projectId, int templateVersionId, CancellationToken ct);
}
```

#### `IExcelExporter`

Експорт документа у .xlsx. public interface IExcelExporter { Формує книгу. Довга операція — виконується у фоні з прогресом (бюджет 10 с p95, tz/08 §8.2).

```csharp
public interface IExcelExporter
{
    public interface IExcelExporter
    public Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct);
}
```

#### `IExcelImporter`

Імпорт із .xlsx — завжди через попередній перегляд diff (ФВ-4.3). public interface IExcelImporter { Розбирає файл і будує diff **без застосування**. Показує, що зміниться, що конфліктує і що буде відхилено правами або станом періоду.

```csharp
public interface IExcelImporter
{
    public interface IExcelImporter
    public Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct);
    public Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct);
}
```

#### `IExportStore`

Готові книги .xlsx, побудовані фоновою задачею.

```csharp
public interface IExportStore
{
    public interface IExportStore
    public Task SaveAsync(string exportId, byte[] content, TimeSpan lifetime, CancellationToken ct);
    public Task<byte[]?> FindAsync(string exportId, CancellationToken ct);
}
```

#### `IImportPreviewStore`

Тимчасове сховище побудованих diff-ів імпорту.

```csharp
public interface IImportPreviewStore
{
    public interface IImportPreviewStore
    public Task SaveAsync(string token, string payloadJson, TimeSpan lifetime, CancellationToken ct);
    public Task<string?> FindAsync(string token, CancellationToken ct);
    public Task RemoveAsync(string token, CancellationToken ct);
}
```

#### `IJobProgressStore`

Сховище прогресу фонових задач (itg.JobProgress).

```csharp
public interface IJobProgressStore
{
    public interface IJobProgressStore
    public Task QueueAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct);
    public Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct);
    public Task ReportAsync(string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct);
    public Task FinishAsync(
    public Task<JobStatus?> FindAsync(string jobId, CancellationToken ct);
}
```

#### `IMethodologyDraftStore`

Сховище **редагованої** частини методології (`ФВ-9.15`): усі версії, включно з чернетками, і формули, які в чернетці правлять. Порт окремий від `IMethodologyStore` навмисно — той обслуговує розрахунок і показує лише опубліковане, бо рахувати чернеткою не можна ніколи. Клон версії переносить **весь** вміст джерела; за повнотою переліку стежить архітектурний сторож.

```csharp
public interface IMethodologyDraftStore
{
    public Task<Methodology?> FindAsync(int methodologyId, CancellationToken ct);
    public Task<IReadOnlyList<MethodologyVersion>> GetAllVersionsAsync(int methodologyId, CancellationToken ct);
    public Task<MethodologyVersion?> FindVersionAsync(int methodologyVersionId, CancellationToken ct);
    public Task<MethodologyFormula?> FindFormulaAsync(int methodologyVersionId, string code, CancellationToken ct);
    public void Add(MethodologyFormula formula);
    public void Remove(MethodologyFormula formula);
    public Task<int> SaveDraftAsync(MethodologyVersion draft, int? copyFromVersionId, CancellationToken ct);
}
```

#### `INotificationOutbox`

Черга сповіщень (itg.NotificationOutbox).

```csharp
public interface INotificationOutbox
{
    public interface INotificationOutbox
    public Task EnqueueAsync(
}
```

#### `INotificationSender`

Доставка сповіщення.

```csharp
public interface INotificationSender
{
    public interface INotificationSender
    public bool IsConfigured { get; }
    public Task SendAsync(
}
```

#### `IPeriodStore`

Доступ до проєктів і їхніх періодів для календаря і адміністративних операцій над періодами.

```csharp
public interface IPeriodStore
{
    public interface IPeriodStore
    public Task<Project?> FindProjectAsync(int projectId, CancellationToken ct);
    public Task<PeriodPolicy> GetPolicyAsync(int periodPolicyId, CancellationToken ct);
    public Task<Period?> LockAsync(int periodId, CancellationToken ct);
    public void AddRange(IEnumerable<Period> periods);
    public Task AddProjectAsync(Project project, CancellationToken ct);
    public Task<IReadOnlyList<PeriodStateRef>> GetPeriodStatesAsync(
    public Task<PeriodBounds?> FindPeriodBoundsAsync(
}
```

#### `IProjectStore`

```csharp
public interface IProjectStore
{
    public interface IProjectStore
    public Task<PagedResult<ProjectSummary>> ListAsync(CursorRequest page, CancellationToken ct);
}
```

#### `IRegistryEntryCache`

Кеш резолвлених списків довідника. Ключ несе DataRevision, тому інвалідація не потрібна — так само, як із метаданими (D-16).

```csharp
public interface IRegistryEntryCache
{
    public interface IRegistryEntryCache
    public Task<IReadOnlyList<RegistryEntry>> GetOrAddAsync(
}
```

#### `IRegistryStore`

Доступ до довідників: визначення, записи, зв'язки і — окремо — перевірка посилань на запис.

```csharp
public interface IRegistryStore
{
    public interface IRegistryStore
    public Task<RegistryDef?> FindDefinitionAsync(string code, CancellationToken ct);
    public Task<RegistryDef?> FindDefinitionByIdAsync(int registryDefId, CancellationToken ct);
    public Task<IReadOnlyList<RegistryDef>> ListDefinitionsAsync(CancellationToken ct);
    public Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(int registryDefId, CancellationToken ct);
    public Task<RegistryEntry?> FindEntryAsync(long registryEntryId, CancellationToken ct);
    public Task<RegistryEntry?> FindEntryByCodeAsync(int registryDefId, string code, CancellationToken ct);
    public Task<IReadOnlyList<RegistryEntryLink>> ListInboundLinksAsync(
    public Task<int> CountReferencesAsync(long registryEntryId, CancellationToken ct);
    public Task<bool> HasOpenPeriodAsync(CancellationToken ct);
    public Task<IReadOnlyList<RegistryValue>> ListValuesAsync(long registryEntryId, CancellationToken ct);
    public void Add(RegistryEntry entry);
    public void AddValue(RegistryValue value);
}
```

#### `IReportDefinitionStore`

Описи звітів (rpt.ReportDef) та їхні версії.

```csharp
public interface IReportDefinitionStore
{
    public interface IReportDefinitionStore
    public Task<int?> FindCurrentVersionIdAsync(string code, CancellationToken ct);
}
```

#### `IReportSnapshotBuilder`

Побудова зрізу звітності. rpt.* — **зріз без логіки**: агрегації робить сервіс тут, вʼюха лише проєктує (ФВ-0.3).

```csharp
public interface IReportSnapshotBuilder
{
    public interface IReportSnapshotBuilder
    public Task<long> BuildAsync(int reportVersionId, int projectId, PeriodKey? periodKey,
    public Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct);
    public Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct);
    public Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
}
```

#### `ISecretProvider`

Значення секрету за його іменем.

```csharp
public interface ISecretProvider
{
    public interface ISecretProvider
    public string? Find(string secretName);
}
```

#### `IStyleCatalog`

Стилі версії шаблону (cfg.StyleDef) за їхніми ідентифікаторами.

```csharp
public interface IStyleCatalog
{
    public interface IStyleCatalog
    public Task<IReadOnlyDictionary<int, StyleDef>> GetAsync(int templateVersionId, CancellationToken ct);
}
```

#### `ITemplateStructure`

Синхронний доступ до вже завантаженого знімка структури версії.

```csharp
public interface ITemplateStructure
{
    public interface ITemplateStructure
}
```

#### `IUnitCatalog`

Довідник одиниць uom.* у формі, потрібній перевірці публікації.

```csharp
public interface IUnitCatalog
{
    public interface IUnitCatalog
    public Task<UnitCatalogSnapshot> GetAsync(CancellationToken ct);
}
```

#### `IUserStore`

Доступ до облікових записів для use-cases безпеки.

```csharp
public interface IUserStore
{
    public interface IUserStore
    public Task<User?> FindBootstrapAdminAsync(CancellationToken ct);
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken ct);
    public Task<User?> FindByIdAsync(int userId, CancellationToken ct);
    public Task<bool> HasActiveDomainAdminAsync(string permissionCode, CancellationToken ct);
    public Task<User?> FindByWindowsSidAsync(string sid, CancellationToken ct);
    public void Add(User user);
    public void RecordAttempt(LoginAttempt attempt);
    public Task GrantRoleAsync(User user, string roleCode, CancellationToken ct);
    public Task<Common.PagedResult<Security.UserView>> ListAsync(
    public Task<IReadOnlyList<Security.RoleView>> ListRolesAsync(CancellationToken ct);
    public Task<int> AddRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct);
    public Task<IReadOnlyList<Security.ResourceGrantDto>> ListGrantsAsync(int roleId, CancellationToken ct);
    public Task ReplaceGrantsAsync(
    public Task<int> RotateStampsForRoleAsync(int roleId, CancellationToken ct);
    public Task<IReadOnlyList<string>> FilterUnknownAsync(
    public Task<IReadOnlyList<string>> FilterDangerousAsync(
    public Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct);
}
```

#### `IValidationResultStore`

```csharp
public interface IValidationResultStore
{
    public interface IValidationResultStore
    public Task SaveAsync(ValidationSummary summary, CancellationToken ct);
    public Task<ValidationSummary?> GetLatestAsync(long documentId, int periodKey, CancellationToken ct);
}
```

#### `IWorkflowStore`

Доступ до стану робочого процесу і періоду для операцій подання, затвердження і повернення в роботу.

```csharp
public interface IWorkflowStore
{
    public interface IWorkflowStore
    public Task<ApprovalState> GetOrCreateAsync(
    public Task<IReadOnlyList<ApprovalState>> GetSheetsAsync(
    public Task<Period> LockPeriodAsync(long documentId, PeriodKey periodKey, CancellationToken ct);
    public Task<long> SaveSnapshotAsync(SubmissionSnapshotRecord snapshot, CancellationToken ct);
    public Task<IReadOnlyList<SubmissionSnapshotRecord>> GetSnapshotsAsync(
    public Task<bool> HasSubmittedSheetsAsync(int projectId, PeriodKey periodKey, CancellationToken ct);
}
```

## 6. Формат помилки

Усі помилки API повертаються як `application/problem+json`
(RFC 9457) з нашими розширеннями.

```csharp
// src/Ecr.Api/Errors/EcrProblemDetails.cs

using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Errors;

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

⛔ **Домен коду — це маршрут на клієнті, а не оздоба.** Клієнт розрізняє
причини за кодом і віддає помилку тому обробникові, чия родина в коді
названа. Родину підбирають за **суб'єктом відмови**: помилка облікового
запису з родиною `ROW` потрапляла в обробку помилок рядка сітки, де для неї
немає ні місця, ні тексту.

⚠ **Кількість кодів — наслідок, а не ціль.** Фрази «каталог закритий на N
кодах» тут немає навмисно: вона тричі розійшлася з кодом і жодного разу цього
не показала. Таблицю звіряє з `src/` сторож
`Каталог_кодів_помилок_збігається_з_контрактом_в_обидва_боки` — і в бік «код
кидається, а рядка немає», і в бік «рядок є, а не кидає ніхто».

| Код | HTTP | Коли |
|---|---|---|
| `ECR-AUTH-0401` | 401 | немає автентифікації |
| `ECR-AUTH-0403` | 403 | немає функціонального права |
| `ECR-AUTH-0423` | 423 | обліковий запис заблоковано |
| `ECR-ACCS-0403` | 403 | відмова `IAccessDecisionService`; у `Extensions2.reason` — `EditDenyReason` |
| `ECR-SEC-0404` | 404 | користувача або ролі не існує (або роль вимкнена) |
| `ECR-USR-0422` | 422 | дані облікового запису не проходять перевірку: алерти без пошти, доменний запис без SID, локальний без разового пароля |
| `ECR-USR-0409` | 409 | обліковий запис із таким іменем уже існує |
| `ECR-TMPL-0404` | 404 | шаблон або версія не знайдені |
| `ECR-TMPL-0409` | 409 | спроба структурної зміни в опублікованій версії (ФВ-7.1) |
| `ECR-TMPL-0422` | 422 | публікація не проходить валідацію цілісності |
| `ECR-TMPL-4221` | 422 | цикл у графі формул |
| `ECR-TMPL-4222` | 422 | посилання на неіснуючий аркуш/таблицю/колонку/рядок |
| `ECR-TMPL-4223` | 422 | несумісні одиниці без явного `CONVERT` (ФВ-16.7) |
| `ECR-TMPL-4224` | 422 | правила однієї області дії з різними рівнями (ФВ-5.10) |
| `ECR-TMPL-4225` | 422 | обов'язкова колонка без правила і без формули (ФВ-5.11) |
| `ECR-CFG-0422` | 422 | код або `RowKey` не відповідає шаблону — помилка введення, не збій |
| `ECR-CFG-4221` | 422 | `Project.TimeZoneId` не є відомим ідентифікатором IANA: порожньо, невідомий пояс, Windows-ідентифікатор (`Central Asia Standard Time`) або зсув (`+05:00`) |
| `ECR-REQ-0422` | 422 | параметр самого запиту поза межами: розмір сторінки, ширина або напрям вікна аудиту |
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
| `ECR-PRD-0404` | 404 | періоду з таким ключем у проєкті немає |
| `ECR-PRD-0422` | 422 | період поза межами проєкту (ФВ-1.11) |
| `ECR-PRD-4223` | 422 | `Reopen` документа при закритому періоді |
| `ECR-PRD-4224` | 422 | `Sequence` поза діапазоном `1…12` (ФВ-1.5a, D-108); **або** кількість періодів `Custom` не ділить рік нарівно |
| `ECR-PRD-4225` | 422 | політика періодів: пільговий строк довший за жорстке закриття, або річний пільговий строк від'ємний (T6/#37) |
| `ECR-PRD-4091` | 409 | політика періодів із таким кодом уже існує (`UQ_PeriodPolicy`, T6/#37) |
| `ECR-SUB-4221` | 422 | `Submit` при наявності рядків `IsOrphaned` (ФВ-8.13) |
| `ECR-SIM-0403` | 403 | спроба запису в сеансі симуляції (`SimulationReadOnly`, ФВ-6.16a) |
| `ECR-SIM-0422` | 422 | симуляція самого себе або без причини |
| `ECR-PWD-0428` | 428 | потрібна зміна пароля: доки `MustChangePassword`, доступні лише зміна пароля і вихід (ФВ-6.18) |
| `ECR-PWD-0422` | 422 | новий пароль не відповідає політиці; у `Extensions2` — які саме вимоги |
| `ECR-REG-0404` | 404 | запис реєстру не знайдено |
| `ECR-REG-0409` | 409 | видалення запису, на який посилаються дані (ФВ-8.6) |
| `ECR-REG-0422` | 422 | перемикання `SourceKind` у відкритому періоді (ФВ-8.9) |
| `ECR-UOM-0404` | 404 | одиниці з таким кодом немає в довіднику |
| `ECR-UOM-0422` | 422 | конверсія між різними розмірностями (ФВ-16.3) |
| `ECR-UOM-4221` | 422 | контекстний коефіцієнт у `uom.Conversion` (ФВ-16.5) |
| `ECR-CALC-0404` | 404 | версії методології не існує |
| `ECR-CALC-0409` | 409 | публікація методології автором останньої правки (D-40) |
| `ECR-CALC-0422` | 422 | публікація без зеленого тесту (ФВ-9.12) |
| `ECR-CALC-0431` | 422 | `^` у діалекті методологій — це XOR, а не степінь |
| `ECR-CALC-0432` | 422 | токен `@Arg` у виразі, якого немає в оголошеному списку аргументів формули: збірка його не підставить (директива ПК-1 №05 §7, пастка 2) |
| `ECR-CALC-0433` | 422 | функція ярусу `Extension` у версії з `NumericMode = Legacy`: відтворювати їй нічого (`02b` §8) |
| `ECR-PRJ-0422` | 422 | активація проєкту, який уже не чернетка або не має періодів (`A7-25`) |
| `ECR-PRJ-0404` | 404 | проєкту з таким ідентифікатором не існує |
| `ECR-CALC-4221` | 422 | перерахунок закритого періоду без окремого погодження (ФВ-9.7) |
| `ECR-IMP-0422` | 422 | імпорт xlsx: структура файлу не відповідає шаблону |
| `ECR-INT-0503` | 503 | зовнішнє джерело недоступне; збір перейде в catch-up |
| `ECR-INT-0422` | 422 | UOM атрибута джерела змінився — збір зупинено (ФВ-16.9) |
| `ECR-INT-0404` | 404 | сутності зовнішнього джерела немає або вона вимкнена |
| `ECR-INT-0502` | 502 | джерело **відмовило в автентифікації**: збір зупинено, у наздоганяння НЕ йде (`H-20`) |
| `ECR-RPT-0404` | 404 | звіту з таким кодом немає або жодну версію не опубліковано |
| `ECR-RPT-0409` | 409 | зріз подано або версію звіту вже опубліковано: обидва іммутабельні, потрібен новий (ФВ-9.17) |
| `ECR-RPT-4091` | 409 | опис звіту з таким кодом уже є (`UQ_ReportDef`); код і є адресою побудови |
| `ECR-RPT-0422` | 422 | опис звіту не складається: порожня назва, немає колонок, невідомий тип колонки чи джерело рядків |
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
| `POST` | `/api/v1/template-versions/{id}/deprecate` | `Template.Publish` | 1 |
| `GET` | `/api/v1/template-versions/{id}/diff/{otherId}` | `Template.View` | 1 |
| `PATCH` | `/api/v1/template-versions/{id}/presentation` | `Template.Edit` | 1 |
| `GET` | `/api/v1/template-versions/{id}/structure` | `Template.View` | 1 |
| `GET` | `/api/v1/template-versions/{id}/access-matrix` | `Template.View` | 3 |
| `GET` | `/api/v1/template-versions/{id}/relations` | `Template.View` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/relations/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/relations/{code}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/sheets/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/sheets/{code}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/sheets/{sheetCode}/tables/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/sheets/{sheetCode}/tables/{code}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/tables/{tableId}/columns/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/tables/{tableId}/columns/{code}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/tables/{tableId}/rows/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/tables/{tableId}/rows/{code}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/tables/{tableDefId}/formulas/{scope}/{target}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/tables/{tableDefId}/formulas/{scope}/{target}` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/tables/{tableId}/validation-rules/{code}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/tables/{tableId}/validation-rules/{code}` | `Template.Edit` | 7 |
| `POST` | `/api/v1/template-versions/{id}/period-access-rules` | `Template.Edit` | 7 |
| `PUT` | `/api/v1/template-versions/{id}/period-access-rules/{ruleId}` | `Template.Edit` | 7 |
| `DELETE` | `/api/v1/template-versions/{id}/period-access-rules/{ruleId}` | `Template.Edit` | 7 |
| `GET` | `/api/v1/projects` | `Document.View` | 1 |
| `POST` | `/api/v1/projects` | `Project.Manage` | 1 |
| `GET` | `/api/v1/projects/period-policies` | `Project.Manage` | 1 |
| `POST` | `/api/v1/projects/period-policies` | `Project.Manage` | 8 |
| `PUT` | `/api/v1/projects/period-policies/{id}` | `Project.Manage` | 8 |
| `GET` | `/api/v1/projects/{id}/approval-route` | `Project.Manage` | 3 |
| `PUT` | `/api/v1/projects/{id}/approval-route` | `Project.Manage` | 3 |
| `POST` | `/api/v1/projects/{id}/activate` | `Project.Manage` | 1 |
| `POST` | `/api/v1/projects/{id}/archive` | `Project.Manage` | 1 |
| `POST` | `/api/v1/projects/{id}/clone` | `Project.Manage` | 3 |
| `PUT` | `/api/v1/projects/{id}/current-period` | `Period.Configure` | 3 |
| `GET` | `/api/v1/projects/{id}/periods` | `Document.View` | 3 |
| `POST` | `/api/v1/projects/{id}/recalculate` | `Calculation.Recalculate` | 3 |
| `POST` | `/api/v1/periods/{id}/reopen` | `Period.Reopen` | 3 |
| `GET` | `/api/v1/documents` | `Document.View` | 1 |
| `POST` | `/api/v1/documents` | `Document.Create` | 1 |
| `GET` | `/api/v1/documents/{id}` | `Document.View` | 1 |
| `GET` | `/api/v1/documents/{id}/tables/{tableInstanceId}` | `Document.View` | 1 |
| `PATCH` | `/api/v1/documents/{id}/cells` | — (через `IAccessDecisionService`) | 1 |
| `POST` | `/api/v1/documents/{id}/rows` | — | 1 |
| `POST` | `/api/v1/documents/{id}/validate` | `Document.View` | 2 |
| `GET` | `/api/v1/documents/{id}/validation` | `Document.View` | 2 |
| `POST` | `/api/v1/documents/{id}/recalculate` | `Calculation.Recalculate` | 2 |
| `POST` | `/api/v1/documents/{id}/submit` | — | 3 |
| `POST` | `/api/v1/documents/{id}/approve` | — | 3 |
| `POST` | `/api/v1/documents/{id}/reopen` | `Document.Reopen` | 3 |
| `GET` | `/api/v1/documents/{id}/tables` | `Document.View` | 6 |
| `POST` | `/api/v1/documents/{id}/export` | `Document.Export` | 5 |
| `GET` | `/api/v1/documents/{id}/export/{exportId}` | `Document.Export` | 5 |
| `POST` | `/api/v1/documents/{id}/import/preview` | `Document.Import` | 5 |
| `POST` | `/api/v1/documents/{id}/import/apply` | `Document.Import` | 5 |
| `GET` | `/api/v1/registries` | `Registry.View` | 4 |
| `GET` | `/api/v1/registries/{code}/entries` | `Registry.View` | 4 |
| `POST` | `/api/v1/registries/{code}/entries` | `Registry.EditData` | 4 |
| `PUT` | `/api/v1/registries/source-kind` | `Integration.Manage` | 4 |
| `GET` | `/api/v1/users/{id}/roles` | `Security.ManageUsers` | 3 |
| `PUT` | `/api/v1/users/{id}/roles` | `Security.ManageUsers` | 3 |
| `PUT` | `/api/v1/users/{id}/email` | `Security.ManageUsers` | 3 |
| `GET` | `/api/v1/units` | — | 4 |
| `POST` | `/api/v1/units/convert` | — | 4 |
| `GET` | `/api/v1/methodologies` | `Calculation.View` | 4 |
| `POST` | `/api/v1/methodologies` | `Calculation.EditFormula` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions` | `Calculation.View` | 7 |
| `POST` | `/api/v1/methodologies/{id}/versions` | `Calculation.EditFormula` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions/{vid}/formulas` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/formulas/{code}` | `Calculation.EditFormula` | 7 |
| `DELETE` | `/api/v1/methodologies/{id}/versions/{vid}/formulas/{code}` | `Calculation.EditFormula` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions/{vid}/constants` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/constants/{code}` | `Calculation.EditConstant` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions/{vid}/rules` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/rules/{code}` | `Calculation.EditRule` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions/{vid}/outputs` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/outputs/{code}` | `Calculation.EditFormula` | 7 |
| `GET` | `/api/v1/methodologies/{id}/versions/{vid}/tests` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/tests/{code}` | `Calculation.EditFormula` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/versions/{vid}/modes` | `Calculation.EditFormula` | 7 |
| `GET` | `/api/v1/methodologies/{id}/bindings` | `Calculation.View` | 7 |
| `PUT` | `/api/v1/methodologies/{id}/bindings/{columnDefId}/{outputCode}` | `Calculation.EditRule` | 7 |
| `GET` | `/api/v1/documents/{id}/calculation-results` | `Calculation.View` | 7 |
| `POST` | `/api/v1/methodologies/{id}/versions/{vid}/publish` | `Calculation.Publish` | 4 |
| `POST` | `/api/v1/methodologies/{id}/simulate` | `Calculation.View` | 4 |
| `POST` | `/api/v1/expressions/validate` | `Calculation.View` | 4 |
| `GET` | `/api/v1/expressions/metadata` | `Calculation.View` | 4 |
| `GET` | `/api/v1/roles` | `Security.ManageRoles` | 3 |
| `POST` | `/api/v1/roles` | `Security.ManageRoles` | 3 |
| `GET` | `/api/v1/users` | `Security.ManageUsers` | 3 |
| `GET` | `/api/v1/roles/{id}/grants` | `Security.ManageRoles` | 3 |
| `PUT` | `/api/v1/roles/{id}/grants` | `Security.ManageRoles` | 3 |
| `POST` | `/api/v1/users` | `Security.ManageUsers` | 3 |
| `PUT` | `/api/v1/users/{id}/alerts` | `Security.ManageUsers` | 5 |
| `GET` | `/api/v1/audit/cells` | `Security.ViewAudit` | 3 |
| `GET` | `/api/v1/jobs` | `System.ViewHealth` | 5 |
| `GET` | `/api/v1/jobs/{jobId}` | `System.ViewHealth` | 5 |
| `GET` | `/api/v1/sources` | `Integration.Manage` | 5 |
| `POST` | `/api/v1/sources/{id}/collect` | `Integration.Manage` | 5 |
| `GET` | `/api/v1/sources/{id}/mapping/preview` | `Integration.Manage` | 5 |
| `GET` | `/api/v1/reports/snapshots` | `Report.ViewRegulatory` | 5 |
| `POST` | `/api/v1/reports/{code}/build` | `Report.BuildSnapshot` | 5 |
| `GET` | `/api/v1/languages` | — (будь-який автентифікований) | 3 |
| `GET` | `/api/v1/ui-strings/{lang}?scope=public` | — (анонімний) | 3 |
| `GET` | `/api/v1/ui-strings/{lang}?scope=private` | — (будь-який автентифікований) | 3 |
| `PUT` | `/api/v1/ui-strings/{lang}/{key}` | `System.ManageLocalization` | 3 |
| `POST` | `/api/v1/security/simulation` | `Security.Simulate` | 3 |
| `DELETE` | `/api/v1/security/simulation` | — (власний сеанс) | 3 |
| `GET` | `/api/v1/security/my-groups` | — (власний сеанс) | 3 |
| `GET` | `/api/v1/security/users/{id}/groups` | `Security.ManageUsers` | 3 |
| `POST` | `/api/v1/auth/change-password` | — (власний пароль) | 3 |
| `POST` | `/api/v1/registries/{code}/entries/{id}/validity` | `Registry.EditData` | 4 |
| `GET` | `/api/v1/registries/{code}/definition` | `Registry.View` | 8 |
| `PUT` | `/api/v1/registries/{code}/definition` | `Registry.EditDefinition` | 8 |
| `GET` | `/api/v1/registries/{code}/history` | `Registry.View` | 8 |
| `GET` | `/api/v1/reports` | `Report.ViewRegulatory` | 5 |
| `POST` | `/api/v1/reports` | `Report.EditDefinition` | 5 |
| `POST` | `/api/v1/reports/{id}/versions` | `Report.EditDefinition` | 5 |
| `POST` | `/api/v1/reports/{id}/versions/{vid}/publish` | `Report.EditDefinition` | 5 |

> **Опис звіту — дані, а не конструктор звітів** (`ФВ-10.4`, `ФВ-10.6`,
> директива №09 `W7`). Веб-переглядач і конструктор звітів ТЗ виносить за
> обсяг: рендеринг лишається в SSRS (`D-52`). Ці чотири маршрути не роблять
> звіту — вони заводять РЯДОК, без якого `POST /reports/{code}/build` не має
> за що зачепитися.
>
> ⛔ Причина, чому вони знадобилися, вимірювана: `rpt.ReportDef` і
> `rpt.ReportVersion` не створювало НІЩО — ні код, ні seed, ні тести. Тобто
> побудова зрізу існувала, була доступна з інтерфейсу і не могла завершитися
> успіхом жодного разу: версія резолвиться за кодом, а кодів у базі не було.
>
> ⛔ `POST /reports` створює опис РАЗОМ із першою версією-чернеткою. Два кроки
> означали б стан, у якому в переліку є звіт, що мовчки відмовляє на кожну
> побудову: `ReportDef` без версії побудувати не можна взагалі.
>
> ⛔ Правити версію не можна — лише завести НОВУ (`POST …/versions`), як у
> методології (`ФВ-9.1`). Опублікована версія незмінна, бо на її колонки
> посилаються вже побудовані зрізи, які читає SSRS.
>
> ⚠ Публікація (`POST …/publish`) стоїть під тим самим правом, що й
> редагування, а не під власним. Це НЕ те саме, що публікація методології
> (`Calculation.Publish`, `D-40`, правило чотирьох очей): та тихо змінює числа
> у вже поданих формах, а ця не змінює жодного побудованого зрізу — зріз
> незмінний і назавжди прив'язаний до версії, за якою його побудували
> (`ФВ-9.17`). Наступна побудова візьме нову версію і дасть НОВИЙ зріз із
> власною контрольною сумою.
>
> ⚠ `Report.EditDefinition` — небезпечне право (`IsDangerous = 1`), і не через
> ризик втратити дані. Вбудована роль `Approver` має шаблон `Report.%`, виданий
> тоді, коли вся родина означала «дивитися, будувати, подавати,
> вивантажувати». Авторство державної форми — інша річ, і роздати його кожному
> погоджувачу правкою одного рядка каталогу було б зміною повноважень людей
> без жодного рішення (`ФВ-6.12`, `D-40`).

> **Опис довідника і його записи — різні маршрути** (`ФВ-8.12`).
> `GET /registries` віддає перелік для вибору: десятки довідників, самі
> метадані. `GET /registries/{code}/definition` віддає ОДИН довідник у
> повноті — поля, зв'язки, правила, мапінг, — тобто те, що конструктор показує
> чотирма вкладками, а зібрати можна лише з чотирьох таблиць. Класти це в
> перелік означало б робити три зайві запити на кожне відкриття сторінки
> довідників.
>
> ⛔ Чотири області приходять ОДНІЄЮ відповіддю. Вони описують один об'єкт і
> читаються разом: поле, наповнюване ззовні, без мапінгу поруч виглядає як
> звичайне, а правило `CrossRegistry` без переліку зв'язків не має контексту.
> Чотири запити давали б чотири різні моменти часу на одному екрані.
>
> ⛔ `PUT` стоїть під `Registry.EditDefinition`, а не `Registry.EditData`: це
> різні люди. Той, хто заводить речовину, і той, хто вирішує, що в довіднику
> речовин узагалі є поле «клас небезпеки», — не одна роль (`ФВ-8.12`).
>
> ⚠ Видів правил довідника **чотири** (директива №06 `H-10`): `RequiredWhen`,
> `UniqueWithin`, `Expression`, `CrossRegistry`. П'ятого — `ValidityWindow` —
> немає навмисно: вікно чинності це **поля запису** `ValidFrom`/`ValidTo`
> (`ФВ-8.5`), а не правило, і правило-дублер дало б два джерела істини про
> чинність, які розійшлися б мовчки на межі вікна.

> **Методології читаються двома різними маршрутами, і це не дублювання**
> (`ФВ-9.15`). `GET /methodologies` віддає те, чим **рахують**: лише
> опубліковані версії, з обчисленою межею вікна дії. `GET
> /methodologies/{id}/versions` віддає те, що **правлять**: усі версії, включно
> з чернетками, і без вікна — у чернетки його немає.
>
> ⛔ Один маршрут із параметром «показати й чернетки» мав би дві відповіді на
> питання, чи можна взяти цю версію в розрахунок. Помилка тут не має симптому:
> незавершена версія порахувала б числа, і побачити це можна було б лише за
> розбіжністю в поданому звіті.
>
> ⚠ Формула адресується **кодом**, а не ключем: код — те, чим на неї
> посилаються вирази (`!Name`), і саме тому `PUT` створює її й змінює однією
> дією. Порядок обчислення (`evaluationOrder`) віддається, але не приймається:
> він топологічний і рахується при публікації (`ФВ-9.4`).

> **Каталог розділений на дві області** (`D-114`). `scope=public` анонімний —
> сторінка входу потребує підписів кнопок раніше, ніж хтось автентифікований;
> туди входять форма входу, загальний chrome і помилки автентифікації.
> `scope=private` — усе інше, лише після входу: підписи адміністративних
> областей і назви прав не мають бути видимі тому, хто ще не увійшов
> (`ФВ-14.2`). Кожна область має **власний** `ETag = revision`; на
> `If-None-Match` — `304`. Тексти помилок (`err.<код>`, `ФВ-14.9a`) розподілені
> між областями за тим самим правилом: помилки входу — публічні, решта — ні.

> **Діагностика доступу — два маршрути, а не один із параметром** (`H-21`).
> `my-groups` віддає **власні** групи і не потребує права: людина, яка після
> входу бачить порожні екрани, має отримати відповідь «чому» без звернення до
> адміністратора, а власні SID вона й так бачить у своєму квитку.
> `users/{id}/groups` віддає те саме про **чужий** запис і тому стоїть під
> `Security.ManageUsers`.
>
> ⚠ Один маршрут із `?userId=` мав би дві різні відповіді на питання «яке
> право потрібне» — тобто рядок у цій таблиці, який не можна заповнити чесно.
>
> ⛔ Членство в групах приходить **із квитка** (`ФВ-6.15a`), а квитка чужої
> сесії в нас немає (`P-02`). Тому відповідь про чужого користувача називає
> `groupsFromTicket: false` **явно**: порожній перелік груп там означає «ми не
> знаємо», а не «людина в жодній групі не перебуває», і сплутати ці два стани
> — рівно той дефект, заради якого маршрути й заведені.

> **Зв'язки таблиць живуть під ВЕРСІЄЮ, а не під шаблоном** (`ФВ-2.12`,
> `ФВ-2.13`). Зв'язок посилається на `cfg.TableDef.Id`, а таблиці належать
> версії; маршрут під шаблоном мусив би питати «якої версії таблиця», тобто
> той самий ідентифікатор іншим шляхом. Версія в адресі ще й задає межу
> правки: `PUT` і `DELETE` приймає лише **чернетка** — опублікована версія
> структурно заморожена (`ФВ-7.1`, `ECR-TMPL-0409`), і зв'язок є структурою,
> бо від нього залежить, звідки в таблиці беруться числа.
>
> ⚠ Зв'язок адресується **кодом** (`UQ_TableRelationDef`), як і формула
> методології (`D2-147`): код задає викликач, тому `PUT` створює зв'язок і
> змінює його однією дією, а повторний запит із тим самим тілом дає той самий
> стан.
>
> ⛔ Окремого маршруту «перелік таблиць версії» немає навмисно: таблиці для
> вибору джерела й приймача бере `GET …/structure`. Другий перелік тих самих
> таблиць розійшовся б із першим на першій же зміні структури, і форма
> пропонувала б вибрати таблицю, якої у версії вже немає.

---

<a id="dto"></a>
## 10. DTO

```csharp
// src/Ecr.Application/Templates/Dto/TemplateDiffDto.cs

using Ecr.Domain.Enums;

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
/// <param name="OldValue">Значення до зміни; <c>null</c> для <c>Added</c>.</param>
/// <param name="NewValue">Значення після зміни; <c>null</c> для <c>Removed</c>.</param>
public sealed record TemplateChangeDto(
    string ElementPath,
    string Kind,
    ChangeClass ChangeClass,
    string? OldValue,
    string? NewValue);
```

```csharp
// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs

using Ecr.Application.Documents.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Структура опублікованої версії — те, що віддається клієнту й кешується за
/// ключем <c>v{id}:r{rev}</c> (`ФВ-2.5`).
/// </summary>
public sealed record TemplateStructureDto(
    int TemplateVersionId,
    int PresentationRevision,
    bool IsEditable,
    IReadOnlyList<SheetDto> Sheets);

public sealed record SheetDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    string? SheetGroup, bool IsMandatory, bool IsVisible,
    IReadOnlyList<TableDto> Tables);

public sealed record TableDto(
    int Id, string Code, TableLayoutKind LayoutKind, TableRowMode RowMode,
    int? MaxDynamicRows,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<TemplateRowDto> Rows);

/// <summary>
/// Рядок у СТРУКТУРІ шаблону — опис, а не дані.
/// </summary>
/// <remarks>
/// ⚠ Окремий тип від <see cref="RowDto"/> (`Q-012`). Той описує рядок
/// ДОКУМЕНТА і несе <c>Cells</c>, <c>RowVersion</c> та <c>IsOrphaned</c> — усе
/// три належать <c>doc.TableRow</c> і в структурі шаблону не існують:
/// значень там немає, версії рядка немає, а осиротіти може лише посилання в
/// даних. Спільний тип означав би, що половина полів відповіді завжди
/// порожня, і клієнт не міг би відрізнити «немає значення» від «тут значень
/// не буває».
/// </remarks>
/// <param name="RowKey">Стабільна бізнес-ідентичність (`R-B6`).</param>
/// <param name="Ordinal">Порядок відображення; презентаційне поле.</param>
/// <param name="RowKind">Вид рядка: <c>Item</c>, <c>Group</c>, <c>Balance</c>, <c>Note</c>.</param>
/// <param name="Label">Локалізований підпис.</param>
/// <param name="ParentRowKey">Батьківський рядок в ієрархії; <c>null</c> — корінь.</param>
/// <param name="IsReadOnly">Рядок недоступний для введення.</param>
public sealed record TemplateRowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string? ParentRowKey,
    bool IsReadOnly);
```

```csharp
// src/Ecr.Application/Registries/Dto/RegistryEntryDto.cs

using Ecr.Domain.ValueObjects;

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

/// <summary>Створення або оновлення запису довідника.</summary>
/// <param name="Id"><c>null</c> — створення нового запису; інакше — оновлення наявного.</param>
/// <param name="RegistryDefId">Довідник, до якого належить запис.</param>
/// <param name="Code">Стабільний код; не змінюється при перейменуванні (`ФВ-8.8`).</param>
/// <param name="Display">Локалізована назва для показу.</param>
/// <param name="ParentEntryId">Батьківський запис в ієрархії; <c>null</c> — корінь.</param>
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
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="Ordinal">Позиція.</param>
/// <param name="RowKind">Режим рядків таблиці.</param>
/// <param name="Label">Підпис для фіксованих рядків.</param>
/// <param name="RowVersion">Версія для оптимістичного блокування.</param>
/// <param name="Cells">Значення; ключ — код колонки. Присутній ключ зі значенням
/// <c>null</c> означає <b>явну порожнечу</b>, відсутній ключ — «не заповнювали» (R-B4).</param>
/// <param name="IsOrphaned">
/// Рядок посилається на запис реєстру, що втратив чинність (ФВ-8.13).
/// ⚠ Читається зі збереженого поля <c>doc.TableRow.IsOrphaned</c>, а не
/// обчислюється при читанні: перерахунок на кожен зріз не вкладається в
/// бюджет 400 мс. Читання не блокує, <c>Submit</c> блокує.
/// </param>
public sealed record RowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string RowVersion,
    IReadOnlyDictionary<string, object?> Cells,
    bool IsOrphaned = false);
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

Межа проходить між **формою** і **фізичним розміщенням**.

**EF Core міграції** створюють **форму**: таблиці, стовпці з типами й
довжинами, ключі, FK, індекси, `CHECK`-обмеження, іменовані `DEFAULT`-обмеження,
`SEQUENCE`. Це артефакт розгортання, який накочує конвеєр окремим обліковим
записом (`D-14`).

**Окремі SQL-скрипти** (`src/Ecr.Infrastructure/Persistence/Sql/`) відповідають
за **фізичне розміщення** і серверні об'єкти, бо їх виконує SQL Agent під
окремим principal (`D-66`) — застосунок не має DDL-прав:

| Скрипт | Що |
|---|---|
| `01-filegroups.sql` | `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT`, `INDEXES` |
| `02-partitions.sql` | `pf_ByPeriodKey`, `ps_ByPeriodKey`, `pf_AuditByMonth`, `ps_AuditByMonth` |
| `03-archive-proc.sql` | процедура архівації: `INSERT…TABLOCK` → звірка сум → `TRUNCATE … WITH (PARTITIONS)` |
| `04-partition-maintenance.sql` | `SPLIT` наступних партицій на 6 місяців уперед |
| `05-rpt-views.sql` | генеровані вʼюхи `rpt.v_*` |
| `06-rcsi.sql` | `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` |
| `07-partition-tables.sql` | **прив'язка партиційованих таблиць до схем** + `DATA_COMPRESSION = PAGE` на `PK_CellValue` |
| `08-system-tables.sql` | таблиці `sys_ecr.*`: `Language`, `SystemSetting`, `UiString`, `UiStringRevision` |
| `09-seed.sql` | seed чистої БД — **єдиний скрипт, який виконує застосунок**, а не SQL Agent |
| `10-triggers.sql` | тригери незмінності `TR_ColumnDef_Immutable`, `TR_RowDef_Immutable`, `TR_FormulaDef_Immutable` |
| `11-audit-tables.sql` | таблиці `aud.*`: `CellChange`, `StructureChange`, `SecurityEvent`, `PublicationEvent`, `SimulationSession` |

⚠ `07` існує тому, що `ON ps_ByPeriodKey(PeriodKey)` — частина `CREATE TABLE`, а
`migrationBuilder` цього не вміє: анотації для розміщення на схемі
партиціонування в EF Core немає. Без `07` таблиці лягають на `PRIMARY`, і
модель архівації **мовчки** не працює — `TRUNCATE … WITH (PARTITIONS)`
виконається і не звільнить нічого (`Q-035`).

⚠ `08` існує з тієї самої причини, з іншого боку: у таблиць `sys_ecr.*` **немає
доменних сутностей** (доступ через порт `IUiStringCatalog`), а чого немає в
моделі EF — того міграція не створить. Без `08` seed падає на першому ж
`MERGE sys_ecr.Language` (`Q-041`).

⚠ `09-seed.sql` — виняток із правила `D-66`: його виконує **застосунок**
(`SeedRunner`), бо seed це DML, а не DDL, і без нього застосунок не стартує
(§14). Файл вбудований у збірку як `EmbeddedResource` і витягнутий скриптом із
`02a-db-schema.md` §17, щоб не з'явилося другої, розбіжної копії.

⚠ `10` існує тому, що **`HasTrigger()` тригера не створює**. Ця анотація лише
каже EF Core не користуватися `OUTPUT`-клаузою на таблиці — інакше
`SaveChanges` падає в рантаймі (ТЗ §13.5 п.1). Сам тригер не створює ні
міграція, ні будь-що інше. Без `10` незмінність опублікованої структури
(`ФВ-7.1`) не тримає **ніщо**: структурна зміна колонки в опублікованій версії
проходить без помилки (`Q-045`).

**Порядок:** `01` → `02` → міграції EF → `07` → `03`, `04`, `05` → `06`.
Скрипти `03` і `05` посилаються на `calc.*` і `arc.*`, тому до етапів 3–5 їх
запускати ще нема на чому.

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
