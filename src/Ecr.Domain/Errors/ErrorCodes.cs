namespace Ecr.Domain.Errors;

/// <summary>
/// Каталог кодів помилок. Код **стабільний**: клієнт і тести покладаються на
/// нього, тому текст змінювати можна, код — ні.
/// </summary>
/// <remarks>
/// ⛔ Каталог живе в <c>Ecr.Domain</c>, а не в <c>Ecr.Api</c>, і саме тому
/// доступний усім шарам. Доки він лежав під API, <c>Ecr.Application</c> і
/// <c>Ecr.Domain</c> не могли на нього послатися (залежності йдуть лише
/// всередину) — і писали коди рядковими літералами повз каталог. Чотири коди
/// (<c>ECR-INT-0404</c>, <c>ECR-PRJ-0422</c>, <c>ECR-RPT-0404</c>,
/// <c>ECR-RPT-0409</c>) так і з'явилися: у каталозі їх не було, друкарську
/// помилку в них не спіймав би ніхто.
///
/// ⚠ Перша літера родини (<c>ECR-<b>ROW</b>-…</c>) — це маршрут на клієнті, а
/// не оздоба. Помилка облікового запису з родиною <c>ROW</c> потрапляла б у
/// обробку помилок рядка сітки. Родину підбирають за **суб'єктом відмови**, а
/// не за схожістю тексту.
///
/// ⚠ Кількість кодів — **наслідок**, а не ціль. «Каталог закритий на N кодах»
/// у цьому файлі не пишеться: числом опікується сторож
/// <c>ContractIntegrityTests</c>, який звіряє те, що кидається в <c>src/</c>,
/// з таблицею <c>02-contracts.md</c> §7 в обидва боки.
/// </remarks>
public static class ErrorCodes
{
    // Автентифікація і доступ
    public const string Unauthorized = "ECR-AUTH-0401";
    public const string Forbidden = "ECR-AUTH-0403";
    public const string AccountLocked = "ECR-AUTH-0423";
    public const string AccessDenied = "ECR-ACCS-0403";

    /// <summary>Користувача або ролі не існує (<c>ECR-SEC-0404</c>).</summary>
    /// <remarks>
    /// ⚠ Родина <c>SEC</c>, а не <c>AUTH</c>: <c>AUTH</c> відповідає на «хто
    /// ви», а тут суб'єкт відмови — запис каталогу безпеки, якого немає.
    /// </remarks>
    public const string SecurityPrincipalNotFound = "ECR-SEC-0404";

    /// <summary>Дані облікового запису не проходять перевірку.</summary>
    /// <remarks>
    /// ⛔ Заведений замість запозиченого <c>ECR-ROW-0422</c>. <c>ROW</c> — це
    /// родина помилок **рядка таблиці документа**: клієнт маршрутизує за
    /// кодом, і відмова «немає пошти — вмикати алерти немає куди» приходила в
    /// обробник помилок сітки, де для неї немає ні місця, ні тексту.
    /// </remarks>
    public const string UserInvalid = "ECR-USR-0422";

    // Шаблони і схема
    public const string TemplateNotFound = "ECR-TMPL-0404";
    public const string TemplateFrozen = "ECR-TMPL-0409";
    public const string TemplateInvalid = "ECR-TMPL-0422";
    public const string FormulaCycle = "ECR-TMPL-4221";
    public const string ReferenceUnresolved = "ECR-TMPL-4222";
    public const string UnitMismatch = "ECR-TMPL-4223";

    /// <summary>Правила однієї області дії з різними рівнями (ФВ-5.10).</summary>
    public const string RuleConflict = "ECR-TMPL-4224";

    /// <summary>Обов'язкова колонка без правила і без формули (ФВ-5.11).</summary>
    public const string RequiredNotCovered = "ECR-TMPL-4225";

    /// <summary>
    /// <c>Breaking</c>-зміна у версії з документами (ФВ-7.4).
    /// </summary>
    /// <remarks>
    /// ⚠ Заброньований: <c>ChangeClassifier</c> уже класифікує зміну як
    /// <c>Breaking</c>, але жоден шлях поки не **відхиляє** операцію цим
    /// кодом — класифікація лише показується в діагностиці версій. Сторож
    /// тримає код у списку броні поіменно, щоб «нікому не потрібен» не
    /// сплуталося з «вимогу забули».
    /// </remarks>
    public const string BreakingChange = "ECR-SCHM-0409";

    /// <summary><c>Guarded</c>-зміна без стратегії міграції. Заброньований (див. <see cref="BreakingChange"/>).</summary>
    public const string GuardedChangeWithoutStrategy = "ECR-SCHM-0422";

    // Документи, рядки, комірки
    public const string DocumentNotFound = "ECR-DOC-0404";
    public const string DocumentSubmitted = "ECR-DOC-0409";
    public const string DocumentCompositionInvalid = "ECR-DOC-0422";
    public const string RowNotFound = "ECR-ROW-0404";
    public const string RowDuplicate = "ECR-ROW-0409";
    public const string CellConflict = "ECR-CELL-0409";
    public const string CellInvalid = "ECR-CELL-0422";
    public const string CellComputed = "ECR-CELL-4221";

    /// <summary>
    /// Значення поза межами реєстру або дії дозволу.
    /// </summary>
    /// <remarks>
    /// ⚠ Заброньований: обидва сценарії наразі закриті іншими кодами —
    /// довідникові межі перевіряє <c>ColumnDef.ValidateValue</c>
    /// (<see cref="CellInvalid"/>), а вихід за вікно дозволу приходить як
    /// <see cref="AccessDenied"/> з <c>EditDenyReason.OutsidePermitWindow</c>.
    /// </remarks>
    public const string CellOutOfRange = "ECR-CELL-4222";

    // Періоди і проєкти
    public const string PeriodClosed = "ECR-PRD-0409";
    public const string PeriodOutOfProject = "ECR-PRD-0422";

    /// <summary>Періоду з таким ключем у проєкті немає (<c>ECR-PRD-0404</c>).</summary>
    public const string PeriodNotFound = "ECR-PRD-0404";

    public const string ReopenBlockedByPeriod = "ECR-PRD-4223";

    /// <summary><c>Sequence</c> поза діапазоном <c>1…12</c> (ФВ-1.5a, <c>D-108</c>).</summary>
    public const string PeriodSequenceOutOfRange = "ECR-PRD-4224";

    /// <summary>Активація проєкту, який уже не чернетка або не має періодів (<c>A7-25</c>).</summary>
    public const string ProjectActivationInvalid = "ECR-PRJ-0422";

    /// <summary>
    /// Код або ключ рядка не відповідає шаблону.
    /// </summary>
    /// <remarks>
    /// ⚠ Це помилка ВВЕДЕННЯ, а не збою. Доки значеннєві об'єкти кидали
    /// <c>ArgumentException</c>, конвеєр не впізнавав її і відповідав
    /// <c>500</c> «Внутрішня помилка»: користувач, який набрав природну
    /// форму коду з дефісом, бачив аварію сервера замість пояснення.
    /// </remarks>
    public const string InvalidCode = "ECR-CFG-0422";

    // Реєстри і одиниці
    public const string RegistryEntryNotFound = "ECR-REG-0404";
    public const string RegistryEntryInUse = "ECR-REG-0409";
    public const string RegistrySwitchInOpenPeriod = "ECR-REG-0422";
    public const string UnitDimensionMismatch = "ECR-UOM-0422";

    /// <summary>Одиниці з таким кодом немає в довіднику (<c>ECR-UOM-0404</c>).</summary>
    public const string UnitNotFound = "ECR-UOM-0404";

    /// <summary>
    /// Контекстний коефіцієнт у <c>uom.Conversion</c> (ФВ-16.5).
    /// </summary>
    /// <remarks>
    /// ⚠ Заброньований: заборона тримається побудовою таблиці конверсій, а не
    /// перевіркою в коді, тому жоден шлях C# цього коду не кидає.
    /// </remarks>
    public const string UnitContextualCoefficient = "ECR-UOM-4221";

    // Розрахунки
    public const string MethodologyFourEyes = "ECR-CALC-0409";
    public const string MethodologyNoGreenTest = "ECR-CALC-0422";
    public const string RecalculateClosedPeriod = "ECR-CALC-4221";

    /// <summary>Версії методології не існує (<c>ECR-CALC-0404</c>).</summary>
    public const string MethodologyVersionNotFound = "ECR-CALC-0404";

    // Робочий процес
    /// <summary><c>Submit</c> при наявності рядків <c>IsOrphaned</c> (ФВ-8.13).</summary>
    public const string SubmitBlockedByOrphans = "ECR-SUB-4221";

    // Безпека: симуляція і зміна пароля
    /// <summary>
    /// Спроба запису в сеансі симуляції (<c>SimulationReadOnly</c>, ФВ-6.16a).
    /// </summary>
    /// <remarks>
    /// ⚠ Заброньований: заборона доїжджає до клієнта як
    /// <see cref="AccessDenied"/> з <c>EditDenyReason.SimulationReadOnly</c> —
    /// однією відмовою доступу з причиною, а не окремим кодом.
    /// </remarks>
    public const string SimulationReadOnly = "ECR-SIM-0403";

    /// <summary>Симуляція самого себе або без причини.</summary>
    public const string SimulationInvalid = "ECR-SIM-0422";

    /// <summary>
    /// Потрібна зміна пароля: доки <c>MustChangePassword</c>, доступні лише
    /// зміна пароля і вихід (ФВ-6.18).
    /// </summary>
    public const string PasswordChangeRequired = "ECR-PWD-0428";

    /// <summary>
    /// Новий пароль не відповідає політиці.
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий код від <see cref="PasswordChangeRequired"/> (`P-01`). До
    /// цього обидва стани — «ще не міняв» і «спробував невдало» — поверталися
    /// як <c>ECR-PWD-0428</c>, і клієнт не міг їх розрізнити: він показував ту
    /// саму форму, не пояснюючи, що саме не так із введеним паролем.
    /// </remarks>
    public const string PasswordPolicyViolated = "ECR-PWD-0422";

    // Імпорт та інтеграція
    public const string ImportStructureMismatch = "ECR-IMP-0422";
    public const string SourceUnavailable = "ECR-INT-0503";
    public const string SourceUnitChanged = "ECR-INT-0422";

    /// <summary>Сутності зовнішнього джерела немає або вона вимкнена (<c>ECR-INT-0404</c>).</summary>
    public const string SourceEntityNotFound = "ECR-INT-0404";

    // Звіти
    /// <summary>Звіту з таким кодом немає або жодну версію не опубліковано (<c>ECR-RPT-0404</c>).</summary>
    public const string ReportNotFound = "ECR-RPT-0404";

    /// <summary>Версія звіту або зріз уже не чернетка: потрібен новий (ФВ-9.17).</summary>
    public const string ReportImmutable = "ECR-RPT-0409";

    // Система
    public const string Internal = "ECR-SYS-0500";
    public const string Archiving = "ECR-SYS-0503";
}
