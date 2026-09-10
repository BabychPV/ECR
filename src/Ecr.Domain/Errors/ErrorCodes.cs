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

    /// <summary>Обліковий запис із таким іменем уже існує (<c>ECR-USR-0409</c>).</summary>
    /// <remarks>
    /// ⛔ Заведений замість запозиченого <c>ECR-ROW-0409</c> (<c>P-25</c>,
    /// рядок 3) — та сама підміна, що й у <see cref="UserInvalid"/>: дублікат
    /// ОБЛІКОВОГО ЗАПИСУ приходив клієнтові як дублікат <c>RowKey</c> у
    /// таблиці документа. Форма створення користувача не має ні сітки, ні
    /// обробника її помилок, тому відмова доїжджала в нікуди.
    /// </remarks>
    public const string UserDuplicate = "ECR-USR-0409";

    /// <summary>Параметр самого запиту не проходить перевірку (<c>ECR-REQ-0422</c>).</summary>
    /// <remarks>
    /// ⛔ Заведений замість запозиченого <c>ECR-CELL-0422</c> (<c>P-25</c>,
    /// рядок 1). Хибний <c>limit</c> у переліку ПРОЄКТІВ приходив клієнтові як
    /// помилка валідації комірки — у обробник помилок сітки документа, якої на
    /// тому екрані немає взагалі.
    ///
    /// ⚠ Суб'єкт відмови — сам запит (розмір сторінки, межі вікна аудиту), а
    /// не дані, які він мав повернути. Тому не <c>ECR-CFG-0422</c>: той
    /// описує помилку ВВЕДЕНОГО значення (код, <c>RowKey</c>), а тут людина
    /// нічого не вводила — параметр склав клієнт.
    /// </remarks>
    public const string RequestInvalid = "ECR-REQ-0422";

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
    /// ⛔ Відмова ОПЕРАЦІЇ, а не попередження — так сказано у ФВ-7.4 дослівно.
    /// Доки цей код не кидав ніхто, <c>ChangeClassifier</c> розрізняв
    /// <c>Breaking</c> і <c>Guarded</c> лише для діагностики версій, а
    /// <c>PatchPresentationHandler</c> відповідав на будь-яку структурну зміну
    /// одним <see cref="TemplateFrozen"/>. Тобто перейменування <c>Code</c>
    /// колонки на версії з тисячею документів і перестановка колонок місцями
    /// поверталися клієнтові однаково — а це різниця між «зробіть це клоном» і
    /// «цього не можна зробити взагалі».
    ///
    /// ⚠ Відрізняється від <see cref="TemplateFrozen"/> суб'єктом: там
    /// заборона тримається СТАНОМ версії (ФВ-7.1, опублікована — незмінна),
    /// тут — наявністю ДОКУМЕНТІВ на ній. Перша знімається клонуванням, друга
    /// не знімається нічим.
    /// </remarks>
    public const string BreakingChange = "ECR-SCHM-0409";

    /// <summary>
    /// <c>Guarded</c>-зміна без стратегії міграції.
    /// </summary>
    /// <remarks>
    /// ⚠ Дані лишаються на місці, змінюється їхнє ТЛУМАЧЕННЯ: тип, точність,
    /// одиниця, довідник, обов'язковість (<c>ChangeClassifier.GuardedFields</c>).
    /// Тому це <c>422</c>, а не <c>409</c>: операція не суперечить стану,
    /// вона неповна — бракує стратегії міграції наявних значень (ФВ-7.5).
    /// </remarks>
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

    /// <summary>
    /// Політика періодів: пільговий строк довший за жорстке закриття, або
    /// річний пільговий строк від'ємний (T6/#37, <c>ECR-PRD-4225</c>).
    /// </summary>
    public const string PeriodPolicyOrderInvalid = "ECR-PRD-4225";

    /// <summary>Політика періодів із таким кодом уже існує (<c>UQ_PeriodPolicy</c>, T6/#37).</summary>
    public const string PeriodPolicyDuplicate = "ECR-PRD-4091";

    /// <summary>Активація проєкту, який уже не чернетка або не має періодів (<c>A7-25</c>).</summary>
    public const string ProjectActivationInvalid = "ECR-PRJ-0422";

    /// <summary>Проєкту з таким ідентифікатором не існує (<c>ECR-PRJ-0404</c>).</summary>
    /// <remarks>
    /// ⛔ Заведений замість ДВОХ запозичених (<c>P-25</c>, рядки 2 і 4).
    /// Перший — <see cref="RowNotFound"/>: <c>ROW</c> означає рядок таблиці
    /// документа, і «проєкту немає» приходило в обробник помилок сітки.
    /// Другий гірший — <see cref="PeriodOutOfProject"/>, у якому цифри коду
    /// кажуть <c>422</c>, а <c>NotFoundException</c> віддає <c>404</c>: код
    /// суперечив сам собі, і клієнт, який розбирає HTTP із коду, читав із
    /// відповіді два різні статуси.
    ///
    /// ⚠ Не <see cref="PeriodNotFound"/>: суб'єкт відмови — проєкт, а не
    /// період у ньому. Обидва трапляються в одному обробнику
    /// (<c>ReopenPeriodHandler</c>), і однаковий код на них позбавив би
    /// користувача єдиної підказки, ЩО саме він назвав неправильно.
    /// </remarks>
    public const string ProjectNotFound = "ECR-PRJ-0404";

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

    /// <summary>
    /// Часовий пояс проєкту не є відомим ідентифікатором IANA
    /// (<c>ECR-CFG-4221</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Окремий код, а не <see cref="InvalidCode"/>. Форма створення проєкту
    /// має ОБИДВА поля — код і пояс, — і на обидва сервер відповідав однаковим
    /// <c>ECR-CFG-0422</c>. Клієнт маршрутизує за кодом, тому підсвітити
    /// правильне поле він не міг: «Код «KASH-2026» недопустимий» і «поясу
    /// «Central Asia Standard Time» не існує» приходили як та сама відмова.
    ///
    /// ⚠ Один код на всі три причини (порожньо, невідомий ідентифікатор,
    /// Windows-ідентифікатор замість IANA) — навмисно: клієнт підсвічує ПОЛЕ,
    /// а яка саме з трьох причин — сказано текстом. Три коди на одне поле
    /// змусили б клієнт знати їх усі, щоб зробити те саме.
    /// </remarks>
    public const string ProjectTimeZoneNotIana = "ECR-CFG-4221";

    // Реєстри і одиниці
    public const string RegistryEntryNotFound = "ECR-REG-0404";
    public const string RegistryEntryInUse = "ECR-REG-0409";
    public const string RegistrySwitchInOpenPeriod = "ECR-REG-0422";

    /// <summary>
    /// Довідник із таким кодом уже є (<c>ECR-REG-4091</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>4091</c>, а не <c>0409</c>: той код уже зайнятий
    /// <see cref="RegistryEntryInUse"/>, і два стани під одним кодом означали
    /// б, що «цей довідник уже є» неможливо відрізнити від «цей запис
    /// видалити не можна» — форма, як у <see cref="ReportDefDuplicate"/>
    /// (<c>ECR-RPT-4091</c>).
    /// </remarks>
    public const string RegistryDefDuplicate = "ECR-REG-4091";

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

    /// <summary>
    /// <c>^</c> у діалекті методологій (<c>ECR-CALC-0431</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ У NCalc це побітове XOR, а не піднесення до степеня: <c>2^3 = 1</c>
    /// (виміряно). Формула, яка виглядає як степінь, мовчки рахувала б
    /// інше число, тому оператор відхиляється парсером.
    /// </remarks>
    public const string CaretNotPower = "ECR-CALC-0431";

    /// <summary>
    /// Токен <c>@Arg</c> у виразі, якого немає в оголошеному списку аргументів
    /// формули (<c>ECR-CALC-0432</c>, директива ПК-1 №05 §7, пастка 2).
    /// </summary>
    /// <remarks>
    /// ⛔ Джерело істини про аргументи — <c>FormulaDef.Arguments</c>, а не текст
    /// виразу: збірка підставляє рівно те, що перелічено в <c>;</c>-списку, і
    /// токен поза списком у вираз не потрапляє. У чинній системі це не аварія,
    /// а ЧИСЛО: формула рахується з невизначеним параметром і повертає
    /// правдоподібний результат. Замір корпусу — 38 таких токенів у двох
    /// формулах <c>Flert</c>.
    ///
    /// ⚠ Окремий код, а не <see cref="MethodologyNoGreenTest"/>: у методолога
    /// тут рівно одна правильна дія — дописати токен у список аргументів
    /// формули, — і зводити це до загального «версія не пройшла перевірок»
    /// означало б сховати саме ту відповідь, яка потрібна.
    /// </remarks>
    public const string FormulaArgumentNotDeclared = "ECR-CALC-0432";

    /// <summary>
    /// Функція ярусу <c>Extension</c> у версії з <c>NumericMode = Legacy</c>
    /// (<c>ECR-CALC-0433</c>, <c>02b</c> §8).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>Legacy</c> існує рівно для того, щоб відтворити числа чинного
    /// рушія. <c>Ln</c> і <c>ifs</c> у NCalc 1.3.8 не оголошені (виміряно),
    /// <c>CONVERT</c> і <c>SUBSTANCE</c> — наші власні; формула з ними не
    /// рахувалася чинною системою ніколи, і відтворювати їй нічого. Правильна
    /// дія одна — <c>NumericMode.Strict</c> з нової дати дії.
    /// </remarks>
    public const string ExtensionFunctionInLegacy = "ECR-CALC-0433";

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

    /// <summary>
    /// Джерело відмовило в автентифікації (<c>ECR-INT-0502</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Окремий код від <see cref="SourceUnavailable"/>, і різниця не
    /// косметична: «джерело лежить» минає само, «нас не пускають» — ні. Доки
    /// обидва стани приходили як <c>ECR-INT-0503</c>, збір із неправильними
    /// обліковими даними йшов у наздоганяння і **завершувався успішно** —
    /// неправильно налаштована система не відрізнялася від справної (<c>H-20</c>).
    ///
    /// ⚠ Тип винятку (<c>SourceAuthenticationException</c>) розділяє їх у
    /// коді, цей код — у відповіді й журналі. Одного типу мало: клієнт і
    /// зведення бачать не тип, а код.
    ///
    /// ⛔ Номер — <b>502</b>, а не <c>0401</c>, як пропонував крок. Цифри в
    /// коді означають НАШ статус відповіді (<c>ECR-&lt;ДОМЕН&gt;-&lt;HTTP&gt;</c>),
    /// а не статус, який віддало джерело. <c>0401</c> сказав би клієнтові
    /// «увійдіть», хоча увійшов він давно — не пускають не його, а нас;
    /// заразом цифри розійшлися б зі статусом відповіді, тобто відтворили б
    /// рівно ту суперечність, яку прибрав <c>D2-49</c>.
    ///
    /// ⚠ І не <c>0503</c>: 503 обіцяє «спробуйте пізніше», а відмова в
    /// автентифікації від повторення не минає — у цьому весь її сенс.
    /// </remarks>
    public const string SourceAuthenticationRefused = "ECR-INT-0502";

    // Звіти
    /// <summary>Звіту з таким кодом немає або жодну версію не опубліковано (<c>ECR-RPT-0404</c>).</summary>
    public const string ReportNotFound = "ECR-RPT-0404";

    /// <summary>Версія звіту або зріз уже не чернетка: потрібен новий (ФВ-9.17).</summary>
    public const string ReportImmutable = "ECR-RPT-0409";

    /// <summary>
    /// Опис звіту з таким кодом уже є (<c>UQ_ReportDef</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>4091</c>, а не <c>0409</c>: статус той самий (<c>409</c>), але
    /// код мусить бути УНІКАЛЬНИМ — клієнт розрізняє причини саме ним, і два
    /// стани під одним кодом означали б, що «цей звіт уже є» неможливо
    /// відрізнити від «цю версію вже опубліковано». Форма з додатковою
    /// цифрою — та сама, що в <see cref="CellOutOfRange"/>
    /// (<c>ECR-CELL-4222</c>) і <see cref="UnitContextualCoefficient"/>
    /// (<c>ECR-UOM-4221</c>).
    /// </remarks>
    public const string ReportDefDuplicate = "ECR-RPT-4091";

    /// <summary>Опис звіту не складається: порожня назва, колонки чи невідоме правило.</summary>
    public const string ReportInvalid = "ECR-RPT-0422";

    // Система
    public const string Internal = "ECR-SYS-0500";
    public const string Archiving = "ECR-SYS-0503";
}
