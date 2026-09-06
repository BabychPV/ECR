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

/// <summary>
/// Стан проєкту: <c>Draft → Active → Archived</c>.
/// </summary>
/// <remarks>
/// ⛔ Членів ТРИ (`D-123`). Були ще <c>Grace</c> і <c>Closed</c> — вони мають
/// сенс для ПЕРІОДУ, і там вони є; на рівні проєкту до них не вів жоден
/// перехід і на них ніхто не спирався, крім двох випадкових перевірок. Стан із
/// переліку, до якого не веде жоден перехід, — це `A7-25` вдруге.
/// </remarks>
public enum ProjectStatus : byte
{
    /// <summary>Створено; періоди ще не відкриваються.</summary>
    Draft = 0,

    /// <summary>У роботі: стан періодів веде <c>PeriodStateJob</c>.</summary>
    Active = 1,

    /// <summary>
    /// Заархівовано: дані доступні лише для читання.
    /// </summary>
    /// <remarks>
    /// ⚠ Значення <c>4</c>, а не <c>2</c>, збережено НАВМИСНО. Прибрані
    /// <c>Grace = 2</c> і <c>Closed = 3</c> лишили дірку в нумерації, і
    /// закривати її означало б переписати `Status` у кожному рядку
    /// `doc.Project` заради охайності переліку.
    /// </remarks>
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
