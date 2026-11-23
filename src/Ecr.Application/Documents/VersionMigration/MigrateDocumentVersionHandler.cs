using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Звіт переносу документа на нову версію шаблону (ФВ-7.5).</summary>
/// <param name="DocumentId">Документ, з якого відкрили перенос.</param>
/// <param name="ProjectId">Його проєкт — версія живе на проєкті, тож переносяться всі його документи.</param>
/// <param name="DocumentCount">Скільки документів проєкту переноситься.</param>
/// <param name="FromVersionId">Поточна версія.</param>
/// <param name="FromVersion">Її номер.</param>
/// <param name="ToVersionId">Цільова версія.</param>
/// <param name="ToVersion">Її номер.</param>
/// <param name="Mode">Режим.</param>
/// <param name="DryRun">Сухий прогін: нічого не змінено.</param>
/// <param name="Applied">Перенос виконано.</param>
/// <param name="CanApply">Режим дозволяє перенос.</param>
/// <param name="Refusals">Причини відмови: <c>structural</c>, <c>dataLoss</c>, <c>guardedWithData</c>, <c>sheetsLocked</c>, <c>projectArchived</c>.</param>
/// <param name="TransferredValues">Скільки введених значень переїде.</param>
/// <param name="LostValues">Скільки введених значень зникло б.</param>
/// <param name="GuardedValues">Скільки введених значень змінили б тлумачення.</param>
/// <param name="LockedSheets">Скільки пар «аркуш × період» подано або затверджено (усього в проєкті).</param>
/// <param name="Items">Відмінності версій, спершу ті, що зачіпають дані.</param>
/// <param name="ItemsTruncated">Перелік обрізано стелею <see cref="MigrateDocumentVersionHandler.MaxItems"/>.</param>
/// <param name="BlockedGrantCount">
/// Скільки грантів на ресурсах (аркуш/таблиця/колонка) вихідної версії не мають відповідника за кодом у новій і
/// через це блокують перенос; <c>null</c>, коли таких немає. ⛔ Лише кількість — які саме ресурси й ролі, не
/// розкривається, доки перелік не фільтрується через AccessProfile.
/// </param>
/// <param name="MappingsRemapped">
/// D-1 RC20: скільки мапінгів збору (<c>ext.EntityFieldMap</c>) на колонки вихідної версії переїде на колонки-
/// відповідники цільової (за кодами аркуш/таблиця/колонка) — у сухому прогоні «буде», у застосованому — «перенесено».
/// Призупинені мапінги переносяться теж (щоб відновлення не писало в колонку старої версії).
/// </param>
/// <param name="MappingsUnmapped">
/// D-1 RC20: скільки АКТИВНИХ мапінгів збору адресують колонку, якої в цільовій версії немає. Такий мапінг після
/// переносу «успішно» збирав би точки й нічого не писав, тому перенос відмовляє (<c>mappingsNotMapped</c>), доки
/// мапінг не призупинено. Призупинені без відповідника не рахуються: вони нічого не пишуть.
/// </param>
/// <param name="OtherProjectsOnSourceVersion">
/// D-1b P3: скільки ІНШИХ проєктів лишається на вихідній версії шаблону. Мапінги збору, подій і вікон глобальні
/// (одна ціль): після цього переносу вони вказують на нову версію, і ті проєкти не збираються, доки їх не перенесено.
/// ⛔ Лише кількість, без переліку проєктів.
/// </param>
/// <param name="OtherDocumentsOnSourceVersion">D-1b P3: скільки документів у цих проєктах.</param>
/// <param name="MappingsUnmappedKeys">
/// До 10 (<c>MaxMappingKeysListed</c>) ключів: <c>SHEET.TABLE.COLUMN</c> колонки вихідної версії для мапінгу збору,
/// <c>events SHEET.TABLE</c> для мапінгу подій і <c>window SHEET.TABLE.COLUMN</c> для прив'язки вікна (D-1b).
/// ⛔ Лише шлях у шаблоні (його й так показує звіт відмінностей): джерело, сутність і поле джерела не розкриваються.
/// </param>
/// <param name="LockedSheetsCarried">
/// LP-32: скільки поданих/затверджених пар «аркуш × період» перенесено БЕЗ змін — план їх не зачіпає: статус, значення
/// й журнал погодження лишаються. Формульні колонки таких аркушів не перераховуються до повернення в роботу.
/// </param>
/// <param name="LockedSheetsAffected">
/// LP-32: скільки поданих/затверджених пар «аркуш × період» зачіпав би перенос (звуження/втрата, видалене, нові
/// рядки фіксованої таблиці, зміна коду, зміна типу). Більше нуля — відмова <c>sheetsLocked</c> (<c>409 ECR-DOC-0409</c>).
/// </param>
/// <param name="LockedSheetItems">
/// LP-32: перелік поданих/затверджених пар (спершу зачеплені), не більше <c>MaxLockedItems</c>; <c>null</c> — таких
/// аркушів немає. ⛔ Без імен користувачів і значень — лише код аркуша, період, статус.
/// </param>
/// <param name="LockedSheetItemsTruncated">LP-32: перелік <paramref name="LockedSheetItems"/> обрізано стелею.</param>
/// <param name="GuardedOverwritable">
/// ACC-OW: скільки з <paramref name="GuardedValues"/> лежить у комірках, на які цільова версія кладе НОВУ комірочну
/// формулу (<c>CellFormula</c>, WW-02): це похідні підсумки, які після переносу замінить перерахунок. Лише їх (і
/// лише їх) дозволяє перезаписати прапор <c>acceptGuardedOverwrite</c>; решта <c>guardedWithData</c> (звуження,
/// зміна типу, одиниці, довідника, обов'язковості, режиму рядків) прапором НЕ знімається. Рахується завжди (також
/// без прапора), щоб діалог показав кількість ДО згоди.
/// </param>
/// <param name="GuardedOverwriteAccepted">Запит ніс <c>acceptGuardedOverwrite = true</c>.</param>
/// <param name="GuardedOverwrittenItems">
/// Перелік комірок шаблону, значення яких перезапише формула (за зростанням шляху), не більше
/// <c>MaxGuardedOverwriteItems</c>; <c>null</c> — таких немає. ⛔ Без значень: лише адреса комірки й кількість
/// введених значень у ній (по всіх документах і періодах проєкту).
/// </param>
/// <param name="GuardedOverwrittenItemsTruncated">ACC-OW: перелік обрізано стелею.</param>
/// <param name="LookupRemapReport">
/// Звіт мап Lookup по цілях запиту (<c>lookupRemaps</c>); <c>null</c> — мап у запиті не було. ⛔ Лише id записів і
/// кількості. Причина відмови <c>lookupValuesNotMapped</c> з'являється в <c>Refusals</c>, коли є значення без
/// придатного відповідника.
/// </param>
/// <param name="LookupRemapped">Скільки значень шапки переїде (сухий прогін) або переїхало; <c>null</c> — мап не було.</param>
/// <param name="LockedDocumentCount">
/// Скільки документів із поданими/затвердженими аркушами мають значення шапки, яке перепише мапа Lookup; <c>null</c> —
/// мап не було. Більше нуля без <c>allowLockedDocuments</c> — відмова <c>lookupLockedDocuments</c> (<c>ECR-SCHM-0422</c>).
/// ⛔ Лише кількість.
/// </param>
/// <param name="LostFormulaOverrides">
/// ✎ D-461: скільки ручних значень замість формул НЕ переїде як ручні (формула цільової версії ручного значення не
/// дозволяє або колонки немає): комірки знову рахує формула, журнал — FormulaRestored. Перенос не блокує; пункти
/// звіту — Kind OverrideLost. null — таких немає.
/// </param>
/// <param name="Range">RPT-STABLE Part B: обсяг переносу, як у запиті (<c>WholeProject</c> / <c>FromMonth</c>).</param>
/// <param name="FromPeriodKey">Запитаний період X (<c>FromMonth</c>); <c>null</c> для <c>WholeProject</c>.</param>
/// <param name="EffectiveFromPeriodKey">
/// Період, з якого перенос реально діє: для <c>FromMonth</c> збігається з X, окрім випадку «рік-сімейство Q-17» —
/// тоді січень року X (див. <paramref name="Warnings"/>); для <c>WholeProject</c> — перший період проєкту; <c>null</c>,
/// якщо в проєкті ще немає періодів.
/// </param>
/// <param name="Warnings">
/// Попередження (не відмови): <c>effectiveFromShiftedToJanuary</c> (ключ <c>documents.migrateWarnEffectiveFromJanuary</c>,
/// параметри <c>requested</c>, <c>effective</c>); <c>null</c> — немає.
/// </param>
/// <param name="LockedOverridesLost">
/// ✎ V-7 (рішення PO 11.11): скільки ручних значень замість формул (D-461), яких формула цільової версії не дозволяє,
/// лежить на ПОДАНИХ або ЗАТВЕРДЖЕНИХ аркушах. Більше нуля — відмова <c>lockedOverridesLost</c> (<c>409 ECR-DOC-0409</c>,
/// <c>migrateLockedOverrides</c>), доки аркуші не повернуть у роботу. <c>null</c> — таких немає.
/// </param>
/// <param name="LockedOverrideItems">
/// ✎ V-7: ці подані/затверджені пари «аркуш × період» (<see cref="LockedSheetItem.Affected"/> = <c>true</c>), не більше
/// <c>MaxLockedItems</c>; <c>null</c> — таких немає. ⛔ Лише коди аркушів, періоди й статуси.
/// </param>
/// <param name="MonthTransferredValues">
/// ✎ LAND-YEAR L3: скільки з <paramref name="TransferredValues"/> переїде (переїхало) з немісячних колонок «копії на
/// місяць» у місячні колонки річної версії (<c>C</c> періоду з місяцем m → <c>C_Mmm</c>). <c>null</c> — перенос не
/// змінює розкладку «копія на місяць» → «рік, місяці в колонках».
/// </param>
/// <param name="RecomputedValues">
/// ✎ LAND-YEAR L3: скільки значень обчислюваних колонок, яких річна версія не має (селектори «поточного місяця»), не
/// переїде, а буде перераховано (не втрата). <c>null</c> — як для <paramref name="MonthTransferredValues"/>.
/// </param>
/// <param name="MonthColumnsUnmapped">
/// ✎ LAND-YEAR L3: шляхи <c>SHEET.TABLE.COLUMN</c> немісячних колонок зі значеннями без повного набору місячних цілей
/// у річній версії (причина <c>dataLoss</c>), не більше <c>MaxItems</c>; <c>null</c> — таких немає.
/// </param>
/// <param name="NonMonthCopiedToAnchor">
/// ✎ LAND-YEAR L3 (B-static): скільки введених значень немісячних колонок якір року (найменший період року в календарі
/// проєкту — його показує річний перегляд) дістане (дістав) з НАЙПІЗНІШОГО місяця, бо в якорі їх немає. Значення якоря
/// не перезаписується ніколи. <c>null</c> — перенос не є переходом на рік.
/// </param>
/// <param name="NonMonthConflicts">
/// ✎ LAND-YEAR L3 (B-static): комірки немісячних колонок, де якір порожній, а місяці року мають РІЗНІ значення —
/// причина відмови <c>nonMonthConflict</c> (система не вибирає мовчки). Не більше <c>MaxItems</c>; <c>null</c> — таких немає.
/// </param>
/// <param name="NonMonthConflictsTruncated">Перелік <paramref name="NonMonthConflicts"/> обрізано стелею; <c>null</c> — переліку немає.</param>
/// <param name="NonMonthAnchorRowsCreated">
/// ✎ LAND-YEAR L3 (B-static): скільки рядків створить (створив) перенос у якорі року, бо якорю бракувало рядка (а то й
/// екземпляра таблиці) для немісячного значення: рядок береться з найпізнішого місяця (<c>RowKey</c>, опис рядка,
/// порядок). <c>null</c> — перенос не є переходом на рік.
/// </param>
/// <param name="AnchorInstancesCreated">
/// ✎ LAND-YEAR L3 (a′): скільки екземплярів таблиць цільової версії перенос створить (створив) у якорі року — УСІ відсутні
/// (оригінал і копії аркушів складу документа, з фіксованими рядками), щоб перерахунок року одразу після переносу їх
/// заповнив, а річний перегляд їх показав без попереднього відкриття аркуша. <c>null</c> — перенос не є переходом на рік.
/// </param>
/// <param name="NonMonthSkippedArchived">
/// ⛔ B69 «архів незмінний»: скільки ДОКУМЕНТІВ (× рік) крок якоря пропустив, бо їхній якір заархівовано (немає жодного
/// гарячого екземпляра в якорі, є архівні): у них нічого не створюється й не копіюється — пропуск, не відмова. Рахуються
/// лише документи, у яких крок мав джерело (екземпляри пізніших місяців року). <c>null</c> — перенос не є переходом на рік.
/// </param>
public sealed record DocumentVersionMigrationDto(
    long DocumentId,
    int ProjectId,
    int DocumentCount,
    int FromVersionId,
    string FromVersion,
    int ToVersionId,
    string ToVersion,
    VersionMigrationMode Mode,
    bool DryRun,
    bool Applied,
    bool CanApply,
    IReadOnlyList<string> Refusals,
    long TransferredValues,
    long LostValues,
    long GuardedValues,
    int LockedSheets,
    IReadOnlyList<VersionMigrationItem> Items,
    bool ItemsTruncated,
    int? BlockedGrantCount = null,
    int MappingsRemapped = 0,
    int MappingsUnmapped = 0,
    IReadOnlyList<string>? MappingsUnmappedKeys = null,
    int OtherProjectsOnSourceVersion = 0,
    int OtherDocumentsOnSourceVersion = 0,
    int LockedSheetsCarried = 0,
    int LockedSheetsAffected = 0,
    IReadOnlyList<LockedSheetItem>? LockedSheetItems = null,
    bool LockedSheetItemsTruncated = false,
    long? GuardedOverwritable = null,
    bool? GuardedOverwriteAccepted = null,
    IReadOnlyList<GuardedOverwriteItem>? GuardedOverwrittenItems = null,
    bool? GuardedOverwrittenItemsTruncated = null,
    IReadOnlyList<LookupRemapReportItem>? LookupRemapReport = null,
    long? LookupRemapped = null,
    int? LockedDocumentCount = null,
    long? LostFormulaOverrides = null,
    VersionMigrationRange? Range = null,
    int? FromPeriodKey = null,
    int? EffectiveFromPeriodKey = null,
    IReadOnlyList<VersionMigrationWarning>? Warnings = null,
    int? LockedOverridesLost = null,
    IReadOnlyList<LockedSheetItem>? LockedOverrideItems = null,
    long? MonthTransferredValues = null,
    long? RecomputedValues = null,
    IReadOnlyList<string>? MonthColumnsUnmapped = null,
    long? NonMonthCopiedToAnchor = null,
    IReadOnlyList<NonMonthConflictItem>? NonMonthConflicts = null,
    bool? NonMonthConflictsTruncated = null,
    long? NonMonthAnchorRowsCreated = null,
    long? AnchorInstancesCreated = null,
    long? NonMonthSkippedArchived = null);

/// <summary>
/// ✎ LAND-YEAR L3 (B-static): комірка немісячної колонки, у якої якір року порожній, а місяці року не згодні
/// (причина відмови <c>nonMonthConflict</c>). Коди — цільової (річної) версії.
/// </summary>
/// <param name="DocumentId">Документ проєкту.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Values">Значення по місяцях за зростанням періоду.</param>
public sealed record NonMonthConflictItem(
    long DocumentId, string SheetCode, string TableCode, string RowKey, string ColumnCode,
    IReadOnlyList<NonMonthConflictValue> Values);

/// <summary>Значення комірки в одному місяці (✎ LAND-YEAR L3, <see cref="NonMonthConflictItem"/>).</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="Value">Значення текстом (число — інваріантною культурою, дата — ISO 8601, запис довідника — <c>#id</c>).</param>
public sealed record NonMonthConflictValue(int PeriodKey, string? Value);

/// <summary>Комірка, значення якої перезапише нова комірочна формула (ACC-OW).</summary>
/// <param name="SheetCode">Код аркуша вихідної версії.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Values">Скільки непорожніх значень у цій комірці по документах і періодах проєкту.</param>
public sealed record GuardedOverwriteItem(
    string SheetCode, string TableCode, string RowKey, string ColumnCode, long Values);

/// <summary>Подана або затверджена пара «аркуш × період» у звіті переносу (LP-32).</summary>
/// <param name="DocumentId">Документ проєкту.</param>
/// <param name="SheetCode">Код аркуша вихідної версії.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Status"><c>Submitted</c> або <c>Approved</c>.</param>
/// <param name="Affected"><c>true</c> — перенос зачепив би цей аркуш (відмова); <c>false</c> — переноситься без змін.</param>
public sealed record LockedSheetItem(long DocumentId, string SheetCode, int PeriodKey, string Status, bool Affected);

/// <summary>Куди можна перенести документ: поточна версія й опубліковані версії того самого шаблону.</summary>
/// <param name="ProjectId">Проєкт документа — переноситься весь.</param>
/// <param name="CurrentVersionId">Поточна версія проєкту.</param>
/// <param name="CurrentVersion">Її номер.</param>
/// <param name="Targets">Опубліковані версії того самого шаблону, крім поточної, від новішої.</param>
/// <param name="FromMonthEnabled">
/// RPT-STABLE Part B: чи доступний перенос «з місяця» (прапор <c>Documents:VersionMigration:FromMonthEnabled</c>,
/// типово <c>false</c>); <c>false</c> - діалог показує лише «увесь проєкт».
/// </param>
public sealed record DocumentVersionMigrationTargetsDto(
    int ProjectId,
    int CurrentVersionId,
    string CurrentVersion,
    IReadOnlyList<TemplateVersionSummary> Targets,
    bool FromMonthEnabled = false);

/// <summary>
/// Перенос документа на нову версію шаблону — явна операція з попереднім
/// переглядом наслідків (ФВ-7.5).
/// </summary>
/// <remarks>
/// ⛔ Версія шаблону живе на ПРОЄКТІ (<c>Project.TemplateVersionId</c>): її
/// беруть усі шляхи читання — таблиці, права, збір, звіти. Документ, чия
/// версія розійшлася з версією проєкту, відкривається без аркушів (V-11).
/// Тому перенос, відкритий із документа, переносить УСІ документи його
/// проєкту однією транзакцією, і звіт це прямо каже (<c>DocumentCount</c>).
/// ✎ B74 P3 (D-576, рішення людини 13.11): проєкт має кілька шаблонів — версія живе на ПАРІ (проєкт, шаблон документа).
/// Нова версія шаблону переносить разом усі документи ЦЬОГО шаблону в проєкті; інші шаблони проєкту незалежні
/// (їхні документи, версія пари й шкала не змінюються). <c>Project.TemplateVersionId</c> оновлюється лише для основного.
///
/// ⚠ Відповідність — за кодами, як у <c>DiffTemplateVersionsHandler</c>:
/// ідентифікатори в кожної версії свої.
///
/// ⚠ Право — <c>Template.Edit</c> І грант <c>Manage</c> на проєкт (L1-08, HU-11 Q3=A), як на редагування шаблону: перенос міняє
/// те, за якою структурою живуть дані, а не самі дані одного користувача.
/// Невидимий документ — той самий <c>404</c>, що й неіснуючий.
/// </remarks>
public sealed class MigrateDocumentVersionHandler(
    IDocumentVersionMigrationStore store,
    IPeriodStore periods,
    ITemplateVersionStore templates,
    IRepository<TemplateVersion, int> versions,
    IMetadataCache metadata,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAccessProfileInvalidator profileCache,
    ISheetEditGate structureGate,
    IBackgroundJobScheduler jobs,
    IRegistryStore registries,
    Microsoft.Extensions.Logging.ILogger<MigrateDocumentVersionHandler> log,

    // ⛔ PRMG: ціль Lookup-поля може бути ГРУПОЮ типів дозволів; порівняння цілі - лише через `LookupTargetMatcher`.
    // `null` (прямий конструктор у тестах) - груп немає.
    Permits.Groups.ILookupTargetResolver? groups = null,

    // ✎ D-461: ручні значення замість формул — переїжджають, лише коли цільова формула дозволяє ручне значення.
    // `null` (прямий конструктор у тестах) — ручних значень немає.
    IFormulaOverrideStore? formulaOverrides = null,

    // ✎ RPT-STABLE Part B: шкала версій проєкту. `null` (прямий конструктор у тестах) - шкали немає: перенос усього проєкту.
    IProjectVersionTimeline? timeline = null,

    // ✎ RPT-STABLE Part B: прапор «з місяця». `null` (прямий конструктор у тестах) - вимкнено, як і типова конфігурація.
    VersionMigrationOptions? migrationOptions = null,

    // ✎ V-5: нові аліаси колонок (cfg.ColumnRename) діють у стабільних вʼюхах SSRS лише після генерації — її ставить
    // фонова задача ReportViewsTask; стан картки `reportviews` («оновлюються», збій постановки). `null` - картки немає.
    IReportViewStatus? reportViewStatus = null)
{
    private readonly bool _fromMonthEnabled = migrationOptions?.FromMonthEnabled ?? false;

    /// <summary>Відмова, коли запит просить «з місяця», а прапор вимкнено (до B2).</summary>
    private void EnsureRangeAllowed()
    {
        if (Range == VersionMigrationRange.FromMonth && !_fromMonthEnabled)
        {
            throw RangeError(
                "err.ECR-SCHM-0422.migrateFromMonthDisabled",
                $"Перенос «з місяця» вимкнено ({VersionMigrationOptions.FromMonthEnabledKey} = false): доступний лише перенос усього проєкту.");
        }
    }

    /// <summary>
    /// Скільки активна (<c>Queued</c>/<c>Running</c>) задача переносу може не оновлюватися, щоб ще вважатися
    /// живою: залишок від краху процесу не мусить назавжди блокувати новий перенос проєкту.
    /// </summary>
    /// <remarks>
    /// 15 хв: задача оновлює рядок стану на кожній стадії/пачці (≥ раз на секунду), а найдовша одинична
    /// команда обмежена 3600 с (<c>CommandTimeout</c>); оренда черги подовжується кожні 30 с.
    /// </remarks>
    public static readonly TimeSpan ActiveJobStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>Скільки останніх активних задач переглядає пошук уже поставленого переносу проєкту.</summary>
    private const int ActiveJobScanLimit = 200;

    /// <summary>Стеля переліку користувачів для скидання кешу профілів; більше — скидається весь кеш.</summary>
    public const int MaxInvalidatedUsers = 100_000;

    /// <summary>Право операції.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Подія журналу безпеки.</summary>
    public const string EventType = "DocumentVersionMigrated";

    /// <summary>Подія журналу безпеки: перенос розпочато (маркер "міграція в процесі" зафіксовано).</summary>
    public const string StartedEventType = "DocumentVersionMigrationStarted";

    /// <summary>
    /// Від скількох значень, що переносяться, обов'язкове <see cref="BackupConfirmation"/> (рішення людини 21.10, варіант «б»:
    /// лише для великих проєктів). Змінюється одним числом; <c>0</c> — вимагати завжди.
    /// </summary>
    public static long BackupConfirmationThreshold { get; set; } = DefaultBackupConfirmationThreshold;

    /// <summary>Типовий поріг обов'язковості підтвердження бекапу, значень.</summary>
    public const long DefaultBackupConfirmationThreshold = 500_000;

    /// <summary>
    /// Підтвердження резервної копії (шлях і/або час) із запиту. Виставляє той, хто викликає (контролер / фонова задача) ДО
    /// виклику; не параметр перевантажень, щоб не множити їх (тести й інші виклики незмінні).
    /// </summary>
    public string? BackupConfirmation { get; set; }

    /// <summary>
    /// RPT-STABLE Part B: обсяг переносу (увесь проєкт / з місяця). Виставляє той, хто викликає (контролер / фонова задача)
    /// ДО виклику — тим самим прийомом, що й <see cref="BackupConfirmation"/>, щоб не множити перевантаження.
    /// </summary>
    public VersionMigrationRange Range { get; set; } = VersionMigrationRange.WholeProject;

    /// <summary>Запитаний період X для <see cref="VersionMigrationRange.FromMonth"/>.</summary>
    public int? FromPeriodKey { get; set; }

    /// <summary>Подія журналу безпеки: значення Lookup шапки документа перенесено на запис нового довідника.</summary>
    public const string LookupRemapEventType = "DocumentHeaderLookupRemapped";

    /// <summary>Стеля переліку версій шаблону, серед яких обирається ціль.</summary>
    public const int MaxTargets = 200;

    /// <summary>Стеля переліку відмінностей у звіті.</summary>
    public const int MaxItems = 500;

    /// <summary>LP-32: стеля переліку поданих/затверджених пар у звіті й у тілі <c>409</c>.</summary>
    public const int MaxLockedItems = 200;

    /// <summary>ACC-OW: стеля переліку комірок, значення яких перезапише формула (<c>guardedOverwrittenItems</c>).</summary>
    public const int MaxGuardedOverwriteItems = 200;

    /// <summary>Скільки ключів методологій на чужі колонки називає відмова (<c>methodologyKeys</c>).</summary>
    public const int MaxMethodologyKeysListed = 20;

    /// <summary>Скільки колонок із мапінгами без відповідника називає звіт (<c>mappingsUnmappedKeys</c>).</summary>
    public const int MaxMappingKeysListed = 10;

    /// <summary>
    /// ⛔ L1-08 (HU-11 Q3=A): перенос міняє структуру ВСІХ документів проєкту, тож потрібен грант
    /// <c>Manage</c> на проєкт (раніше досить було Read + глобального <c>Template.Edit</c>). Діє і на
    /// сухий прогін, і на перелік цілей. Документ уже видимий — відмова нічого не розкриває.
    /// </summary>
    private static void RequireProjectManage(AccessProfile profile, int projectId)
    {
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                $"Немає гранта Manage на проєкт {projectId}, документи якого переніс би на іншу версію шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Будує звіт і, якщо це не сухий прогін, переносить.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Документа (або цільової версії) немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-TMPL-0422</c> — ціль не є опублікованою іншою версією того самого шаблону;
    /// <c>ECR-SCHM-0422</c> — режим не має стратегії для таких змін (втрата даних,
    /// змінене тлумачення значень, структура в режимі <c>Presentation</c>).
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>ECR-DOC-0409</c> — перенос зачепив би подані чи затверджені аркуші (LP-32; решта поданих переноситься без
    /// змін), або проєкт архівовано.
    /// </exception>
    public Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun, CancellationToken ct)
        => HandleAsync(documentId, targetVersionId, mode, dryRun, renames: null, ct);

    /// <summary>Будує звіт і, якщо це не сухий прогін, переносить — з мапою перейменувань таблиць/колонок.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="renames">
    /// Мапа «було → стало» кодів таблиць (і колонок) для структури, перейменованої в цільовій версії; <c>null</c> —
    /// без мапи. Діє лише там, де відповідника за кодом немає; суперечність мапи парі версій — <c>renameConflict</c>.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Також <c>ECR-SCHM-0422</c> (<c>migrateRenameMapInvalid</c>) — зламана форма мапи.
    /// </exception>
    public Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun,
        IReadOnlyList<VersionMigrationTableRename>? renames, CancellationToken ct)
        => HandleAsync(documentId, targetVersionId, mode, dryRun, renames, acceptGuardedOverwrite: false, ct);

    /// <summary>
    /// Будує звіт і, якщо це не сухий прогін, переносить — з мапою перейменувань і згодою на перезапис похідних
    /// значень (ACC-OW).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">
    /// <c>true</c> — не відмовляти <c>guardedWithData</c> там, де «змінене тлумачення» — це ЛИШЕ нова комірочна
    /// формула на комірці з уже введеним значенням (<c>CellFormula</c>): значення переїздить, а перерахунок після
    /// переносу замінює його результатом формули. Інші класи guarded і подані/затверджені аркуші прапор не знімає.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite, CancellationToken ct)
        => HandleAsync(documentId, targetVersionId, mode, dryRun, renames, acceptGuardedOverwrite, lookupRemaps: null, ct);

    /// <summary>
    /// Будує звіт і, якщо це не сухий прогін, переносить — з мапою перейменувань, згодою на перезапис похідних
    /// значень і мапою записів Lookup шапки («було → стало» для поля, довідник якого змінився).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода на перезапис похідних значень.</param>
    /// <param name="lookupRemaps">
    /// Мапи записів Lookup по полях шапки; <c>null</c> — без мап (поведінка й SQL як раніше). Мапа діє лише там,
    /// де довідник поля між версіями змінився; повна мапа знімає з цього поля <c>guardedWithData</c>.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Також <c>ECR-SCHM-0422</c>: <c>migrateLookupRemapInvalid</c> (форма або зміст мапи),
    /// <c>migrateLookupNotMapped</c> (є значення без придатного відповідника; нічого не записано).
    /// </exception>
    public Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, CancellationToken ct)
        => HandleAsync(
            documentId, targetVersionId, mode, dryRun, renames, acceptGuardedOverwrite, lookupRemaps,
            allowLockedDocuments: false, ct);

    /// <summary>
    /// Те саме, що попередній виклик, і з явною згодою мігрувати шапку закритих документів (<c>allowLockedDocuments</c>).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода на перезапис похідних значень.</param>
    /// <param name="lookupRemaps">Мапи записів Lookup шапки; <c>null</c> — без мап.</param>
    /// <param name="allowLockedDocuments">
    /// <c>true</c> — дозволити переписати значення Lookup у шапці документів, що мають подані/затверджені аркуші.
    /// Без прапора такі документи — відмова <c>ECR-SCHM-0422</c> (<c>migrateLookupLockedDocuments</c>); сухий прогін
    /// показує їх кількість (<c>lockedDocumentCount</c>) завжди.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments, CancellationToken ct)
    {
        var request = await ResolveAsync(documentId, targetVersionId, tolerateSame: false, ct).ConfigureAwait(false);
        VersionMigrationRenames.Validate(renames);
        VersionMigrationLookupRemaps.Validate(lookupRemaps);
        EnsureRangeAllowed();

        if (dryRun)
        {
            var planned = await PlanAsync(
                documentId, request.ProjectId, request.SourceVersionId, request.Target, mode, request.Archived,
                dryRun: true, renames, acceptGuardedOverwrite, lookupRemaps, allowLockedDocuments, ct).ConfigureAwait(false);
            return planned.Dto;
        }

        // Синхронний шлях без приймача прогресу завжди дає звіт (збіг версій там — виняток, не "вже перенесено").
        return (await ApplyAsync(
                request, documentId, mode, renames, acceptGuardedOverwrite, lookupRemaps, allowLockedDocuments,
                onProgress: null, ct)
            .ConfigureAwait(false))!;
    }

    /// <summary>
    /// Ставить перенос у ЧЕРГУ (фон) і повертає ідентифікатор задачі (D-2 RC15B).
    /// </summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Ідентифікатор задачі. Повторна постановка того самого автора, поки перенос проєкту ще йде, повертає
    /// ТОЙ САМИЙ ідентифікатор і другого переносу не ставить.
    /// </returns>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-JOB-0409</c> — перенос цього проєкту вже йде за вказівкою ІНШОЇ людини (її задачу автор не бачить).
    /// </exception>
    /// <remarks>
    /// ⛔ Ті самі швидкі перевірки, що й у синхронного шляху: право <c>Template.Edit</c>, видимість документа
    /// (невидимий — той самий <c>404</c>), грант <c>Manage</c> на проєкт, ціль, архівний проєкт. Це швидка
    /// відмова заради людини; ПЛАН (відмови за даними) задача перераховує сама під блоком проєкту — він
    /// на мільйонах значень іде десятки секунд, і тримати заради нього HTTP-запит означало б ту саму
    /// проблему, яку фон розв'язує. Для нього є сухий прогін, який діалог показує людині перед переносом.
    /// <para>
    /// ⚠ Ціль задачі — ПРОЄКТ (<c>migrate-project{id}</c>), а не документ: переноситься весь проєкт. Постановка
    /// <c>Coalesced</c> — НЕ витісняє нічого (перенос під блоком проєкту обривати посеред пачок немає сенсу, а
    /// чужі задачі іншого типу ціль не зачіпає). Пошук уже поставленого переносу додатково перевіряє
    /// <c>Running</c>: черга в базі ставила б другий перенос ПОЗАДУ виконуваного.
    /// </para>
    /// </remarks>
    public Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, CancellationToken ct)
        => EnqueueAsync(documentId, targetVersionId, mode, renames: null, ct);

    /// <summary>Ставить перенос у чергу з мапою перейменувань (форма мапи перевіряється одразу, зміст — у задачі).</summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, CancellationToken ct)
        => EnqueueAsync(documentId, targetVersionId, mode, renames, acceptGuardedOverwrite: false, ct);

    /// <summary>Ставить перенос у чергу з мапою перейменувань і згодою на перезапис похідних значень (ACC-OW).</summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода: див. <see cref="HandleAsync(long, int, VersionMigrationMode, bool, IReadOnlyList{VersionMigrationTableRename}?, bool, CancellationToken)"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite, CancellationToken ct)
        => EnqueueAsync(documentId, targetVersionId, mode, renames, acceptGuardedOverwrite, lookupRemaps: null, ct);

    /// <summary>Ставить перенос у чергу з мапою перейменувань, згодою ACC-OW і мапою записів Lookup шапки.</summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода на перезапис похідних значень.</param>
    /// <param name="lookupRemaps">Мапи Lookup; <c>null</c> — без мап (форма перевіряється одразу, зміст — у задачі).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, CancellationToken ct)
        => EnqueueAsync(
            documentId, targetVersionId, mode, renames, acceptGuardedOverwrite, lookupRemaps,
            allowLockedDocuments: false, ct);

    /// <summary>Ставить перенос у чергу з усіма мапами запиту й згодою на шапку закритих документів.</summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода на перезапис похідних значень.</param>
    /// <param name="lookupRemaps">Мапи Lookup; <c>null</c> — без мап.</param>
    /// <param name="allowLockedDocuments">Згода мігрувати шапку документів із поданими/затвердженими аркушами.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public async Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments, CancellationToken ct)
    {
        var request = await ResolveAsync(documentId, targetVersionId, tolerateSame: false, ct).ConfigureAwait(false);
        VersionMigrationRenames.Validate(renames);
        VersionMigrationLookupRemaps.Validate(lookupRemaps);
        EnsureRangeAllowed();

        if (request.Archived)
        {
            throw ArchivedProject();
        }

        // ⛔ Пошук активної задачі та постановка мусять іти ПІД одним замком проєкту. Без нього два запити
        // (дві вкладки, дві людини) обидва не бачать активної задачі й обидва ставлять: коалесценція
        // планувальника Quartz між двома одночасними викликами НЕ атомарна (виміряно тестом гонки: два
        // різні `jobId`), тож друга людина діставала б зайву задачу, яка потім падала "вже перенесено".
        // Замок процесний: Quartz-режим однопроцесний за побудовою, а черга в базі між процесами
        // коалесціює атомарно на рівні SQL - там цей замок лише зменшує зайві постановки.
        var gate = ProjectGates.GetOrAdd(request.ProjectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnqueueLockedAsync(
                    request, documentId, targetVersionId, mode, renames, acceptGuardedOverwrite, lookupRemaps,
                    allowLockedDocuments, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Замки постановки переносу за проєктом (записів небагато: по одному на проєкт, де переносили).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> ProjectGates = new();

    private async Task<string> EnqueueLockedAsync(
        Resolved request, long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments, CancellationToken ct)
    {
        var documentIds = (await store.ListDocumentIdsAsync(request.ProjectId, ct).ConfigureAwait(false)).ToHashSet();
        var active = await FindActiveJobAsync(documentIds, ct).ConfigureAwait(false);
        if (active is not null)
        {
            if (active.CreatedByUserId == request.Profile.UserId)
            {
                return active.JobId;
            }

            throw MigrationInProgress(request.ProjectId);
        }

        // ⛔ Автор — у завданні: задача виконується поза HTTP-запитом і без нього бачила б анонімного
        // користувача (F-01, той самий прийом, що й застосування великого імпорту).
        var actor = new JobActor(
            currentUser.UserId ?? request.Profile.UserId,
            currentUser.UserName,
            currentUser.Language,
            [.. currentUser.GroupSids],
            currentUser.CorrelationId);

        var jobId = await jobs
            .EnqueueCoalescedAsync<IMigrateDocumentVersionJob>(
                request.IsPrimary ? TargetOf(request.ProjectId) : TargetOf(request.ProjectId, request.TemplateId),
                new MigrateDocumentVersionTask(
                    documentId, targetVersionId, mode.ToString(), actor,
                    renames is { Count: > 0 } ? renames : null,
                    acceptGuardedOverwrite,
                    lookupRemaps is { Count: > 0 } ? lookupRemaps : null,
                    allowLockedDocuments,
                    BackupConfirmation,
                    Range.ToString(),
                    FromPeriodKey),
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);

        // ⛔ Друга лінія захисту (інший процес, "сліпий" пошук активних): коалесценція за ключем проєкту
        // (`migrate-project{id}`) віддає `jobId` ПЕРШОЇ постановки. Повернений `jobId` віддається лише якщо
        // задачу поставила ця сама людина; чужий не витікає - 409.
        var owner = await jobs.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);
        if (owner is { } ownerId && ownerId != request.Profile.UserId)
        {
            throw MigrationInProgress(request.ProjectId);
        }

        return jobId;
    }

    private static BusinessRuleException MigrationInProgress(int projectId)
        => new(
            ErrorCodes.JobStateConflict,
            $"Перенос проєкту {projectId} уже виконується за вказівкою іншого користувача.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-JOB-0409.migrationInProgress",
                ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Ціль задачі переносу: проєкт (переноситься весь).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <returns>Ключ цілі для злиття постановок.</returns>
    public static string TargetOf(int projectId)
        => string.Create(CultureInfo.InvariantCulture, $"migrate-project{projectId}");

    /// <summary>
    /// ✎ B74 P3 (D-576): ціль задачі переносу ПРИЄДНАНОГО шаблону — пара (проєкт, шаблон): постановка переносу шаблону B
    /// не зливається з переносом основного шаблону того самого проєкту (злиття віддало б <c>jobId</c> чужого переносу).
    /// Одночасно їх однаково не виконати: маркер переносу — на проєкті (другий отримає 409).
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateId">Приєднаний шаблон.</param>
    /// <returns>Ключ цілі для злиття постановок.</returns>
    public static string TargetOf(int projectId, int templateId)
        => string.Create(CultureInfo.InvariantCulture, $"migrate-project{projectId}-template{templateId}");

    /// <summary>
    /// Виконує перенос у задачі черги — від імені автора (<c>JobActorScope</c>), з прогресом.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    /// <remarks>
    /// ⛔ Усе перевіряється ще раз у мить виконання (право, видимість, грант, ціль, план): між постановкою
    /// і стартом право могли відкликати, версію — перенести. Відмова даних — той самий виняток, що й
    /// синхронного шляху (<c>ECR-SCHM-0422</c>, <c>ECR-DOC-0409</c>): задача стає <c>Failed</c> з його кодом,
    /// а документи лишаються на старій версії (атомарність — одна транзакція).
    /// </remarks>
    public Task<VersionMigrationRunResult> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
        => RunQueuedAsync(documentId, targetVersionId, mode, renames: null, onProgress, ct);

    /// <summary>Виконує перенос у задачі черги з мапою перейменувань (її поставив запит; тут перевіряється ще раз).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    public Task<VersionMigrationRunResult> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
        => RunQueuedAsync(documentId, targetVersionId, mode, renames, acceptGuardedOverwrite: false, onProgress, ct);

    /// <summary>Виконує перенос у задачі черги з мапою перейменувань і згодою на перезапис похідних значень (ACC-OW).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода (її поставив запит; ПЛАН у задачі перераховується з нею).</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    public Task<VersionMigrationRunResult> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
        => RunQueuedAsync(
            documentId, targetVersionId, mode, renames, acceptGuardedOverwrite, lookupRemaps: null, onProgress, ct);

    /// <summary>Виконує перенос у задачі черги з усіма необов'язковими мапами запиту (ACC-OW і Lookup шапки).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода ACC-OW.</param>
    /// <param name="lookupRemaps">Мапи записів Lookup шапки (їх поставив запит; ПЛАН у задачі перераховується з ними).</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    public Task<VersionMigrationRunResult> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
        => RunQueuedAsync(
            documentId, targetVersionId, mode, renames, acceptGuardedOverwrite, lookupRemaps,
            allowLockedDocuments: false, onProgress, ct);

    /// <summary>Виконує перенос у задачі черги з усіма мапами запиту й згодою на шапку закритих документів.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="renames">Мапа перейменувань; <c>null</c> — без мапи.</param>
    /// <param name="acceptGuardedOverwrite">Згода ACC-OW.</param>
    /// <param name="lookupRemaps">Мапи записів Lookup шапки.</param>
    /// <param name="allowLockedDocuments">Згода мігрувати шапку документів із поданими/затвердженими аркушами.</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    public async Task<VersionMigrationRunResult> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        VersionMigrationRenames.Validate(renames);
        VersionMigrationLookupRemaps.Validate(lookupRemaps);

        await onProgress(StartedPercent, "jobs.migrateStarted", null, ct).ConfigureAwait(false);

        // ⛔ Перевірка прав ТУТ обов'язкова і не дублює ту, що в постановці: між постановкою і стартом
        // задачі право/видимість/грант Manage автора могли відкликати, а задача не має писати від імені
        // людини, яка вже не має права (мутаційний доказ — тест "Право_відкликане_до_старту_задачі…").
        // ⚠ Збіг версій у задачі - не помилка: постановка двох однакових переносів (друга пішла позаду
        // виконуваної в черзі в базі) дає зелену задачу "вже перенесено", а не зайву червону.
        var request = await ResolveAsync(documentId, targetVersionId, tolerateSame: true, ct).ConfigureAwait(false);
        if (request.SourceVersionId == request.Target.Id)
        {
            return await AlreadyMigratedAsync(request, ct).ConfigureAwait(false);
        }

        var dto = await ApplyAsync(
                request, documentId, mode, renames, acceptGuardedOverwrite, lookupRemaps, allowLockedDocuments, onProgress, ct)
            .ConfigureAwait(false);

        return dto is null
            ? await AlreadyMigratedAsync(request, ct).ConfigureAwait(false)
            : new VersionMigrationRunResult(
                dto.ToVersion, dto.DocumentCount, dto.TransferredValues, AlreadyMigrated: false,
                MappingsRemapped: dto.MappingsRemapped, MappingsUnmapped: dto.MappingsUnmapped);
    }

    private async Task<VersionMigrationRunResult> AlreadyMigratedAsync(Resolved request, CancellationToken ct)
    {
        var documents = await store.ListDocumentIdsAsync(request.ProjectId, ct).ConfigureAwait(false);

        return new VersionMigrationRunResult(request.Target.Version, documents.Count, 0, AlreadyMigrated: true);
    }

    private const int StartedPercent = 3;
    private const int PlanningPercent = 8;
    private const int StoreFloorPercent = 15;
    private const int StoreCeilingPercent = 97;

    /// <summary>Що з'ясовують швидкі перевірки до плану: профіль, проєкт, ціль; ✎ B74 P3 — шаблон пари.</summary>
    private sealed record Resolved(
        AccessProfile Profile, int ProjectId, int SourceVersionId, TemplateVersion Target, bool Archived,
        int TemplateId = 0, bool IsPrimary = true);

    /// <summary>
    /// ✎ B74 P3 (D-576): пара (проєкт, шаблон) документа — її версія вихідна, і сховище відтепер бачить лише документи
    /// цього шаблону. Нова версія шаблону переносить разом усі документи ЦЬОГО шаблону в проєкті; інші шаблони — незалежні.
    /// </summary>
    private async Task<VersionMigrationTemplatePair> UsePairAsync(long documentId, CancellationToken ct)
    {
        var pair = await store.ReadTemplatePairAsync(documentId, ct).ConfigureAwait(false)
                   ?? throw DocumentVisibility.NotFound(documentId);
        store.UseTemplate(pair.TemplateId, pair.IsPrimary);
        return pair;
    }

    private async Task<Resolved> ResolveAsync(long documentId, int targetVersionId, bool tolerateSame, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        // ⛔ Ціль — версія ТОГО САМОГО шаблону, що й документ (версія пари); версія іншого шаблону проєкту — та сама
        // відмова migrateOtherTemplate, що й чужого шаблону.
        var pair = await UsePairAsync(documentId, ct).ConfigureAwait(false);
        var target = await RequireTargetAsync(pair.CurrentVersionId, targetVersionId, tolerateSame, ct).ConfigureAwait(false);
        var archived = project.Status == ProjectStatus.Archived || project.IsArchiving;

        // ⛔ D-474 (4): річний проєкт не переїжджає на версію з розкладкою «копія на місяць» — у нього один період на рік.
        if (project.PeriodKind == PeriodKind.Yearly && target.Layout == TemplateLayout.PerMonthCopies)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                "Річний проєкт потребує версії шаблону з розкладкою «рік, місяці в колонках».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout",
                    ["versionId"] = targetVersionId.ToString(CultureInfo.InvariantCulture),
                });
        }

        return new Resolved(profile, projectId, pair.CurrentVersionId, target, archived, pair.TemplateId, pair.IsPrimary);
    }

    /// <summary>Незавершена задача переносу будь-якого документа цього проєкту; <c>null</c> — немає.</summary>
    private async Task<JobSummary?> FindActiveJobAsync(HashSet<long> projectDocumentIds, CancellationToken ct)
    {
        var freshAfter = clock.UtcNow - ActiveJobStaleAfter;

        // Код у переліку — повне ім'я типу (черга в базі) або коротке (Quartz): порівняння за хвостом.
        foreach (var state in new[] { "Running", "Queued" })
        {
            var recent = await jobs
                .ListRecentAsync(new JobListFilter(State: state), ActiveJobScanLimit, ct)
                .ConfigureAwait(false);

            var found = recent.FirstOrDefault(j =>
                j.DocumentId is { } doc
                && projectDocumentIds.Contains(doc)
                && j.UpdatedAt >= freshAfter
                && (j.JobCode.EndsWith(nameof(IMigrateDocumentVersionJob), StringComparison.Ordinal)));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static ConcurrencyConflictException ArchivedProject()
        => new(
            ErrorCodes.DocumentSubmitted,
            "Проєкт архівовано: документи архівного проєкту не переносяться.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0409.migrateProjectArchived",
                ["lockedSheets"] = "0",
                ["refusals"] = ArchivedRefusals,
            });

    private static readonly string[] ArchivedRefusals = ["projectArchived"];

    /// <summary>Ключ каталогу повідомлення стадії; ключі — літералами (сторож каталогу шукає саме їх).</summary>
    private static string StageKey(string stage) => stage switch
    {
        VersionMigrationStages.Instances => "jobs.migrateInstances",
        VersionMigrationStages.NewRows => "jobs.migrateNewRows",
        VersionMigrationStages.Header => "jobs.migrateHeader",
        VersionMigrationStages.Index => "jobs.migrateIndex",
        VersionMigrationStages.Workflow => "jobs.migrateWorkflow",
        VersionMigrationStages.Validation => "jobs.migrateValidation",
        VersionMigrationStages.Finish => "jobs.migrateFinish",
        _ => "jobs.migrateCells",
    };

    /// <returns>Звіт; <c>null</c> — лише для фонової задачі: проєкт під блоком уже на цільовій версії.</returns>
    private async Task<DocumentVersionMigrationDto?> ApplyAsync(
        Resolved request, long documentId, VersionMigrationMode mode,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments,
        VersionMigrationProgressReporter? onProgress, CancellationToken ct)
    {
        var profile = request.Profile;
        var projectId = request.ProjectId;
        var target = request.Target;
        var archived = request.Archived;

        DocumentVersionMigrationDto? result = null;
        var grantedUsers = new GrantedUsers([], Overflow: false);
        var formulaTables = 0;

        // ⛔ Б1 (P1-1/P1-2): ДВІ транзакції з зафіксованим маркером «міграція в процесі» між ними.
        //  T1 — блок проєкту, структура документів, план, відмови; наприкінці ставить маркер і КОМІТИТЬСЯ: відтоді писарі
        //      (`SheetEditGate`) і читачі (фільтр API) відмовляють 409 ECR-DOC-4091, тож напівстан діапазонів ніхто не бачить.
        //  Важка частина — діапазонами з комітом, без відкритої транзакції (ні блокувань, ні закріпленого журналу).
        //  T2 — повторний блок проєкту, шапка/склад/екземпляри/гранти, аудит і мапінги збору, ПЕРЕМИКАННЯ ВЕРСІЇ І ЗНЯТТЯ
        //      МАРКЕРА однією командою. Обрив між ними лишає маркер; повторний запуск бачить його, планує заново (уже
        //      перенесене рахується перенесеним) і доводить до кінця.
        PlannedMigration planned = null!;
        var current = 0;
        var sameVersion = false;
        VersionMigrationStepReporter? onStep = null;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ План перераховується ПІД блоком рядка проєкту: між сухим
            // прогоном і підтвердженням у колонку, яку нова версія прибирає,
            // могли ввести значення. Перевірка на знімку з минулого запиту
            // пропустила б саме ту втрату, від якої вона захищає.
            current = await store.LockProjectVersionAsync(projectId, innerCt).ConfigureAwait(false)
                          ?? throw DocumentVisibility.NotFound(documentId);
            if (current == target.Id)
            {
                // Фонова задача: інший перенос на ту саму ціль встиг раніше - це не помилка (див. RunQueuedAsync).
                if (onProgress is not null)
                {
                    sameVersion = true;
                    return;
                }

                throw SameVersion(target.Id);
            }

            // Маркер іншого переносу (на іншу ціль) не перекриваємо; на ту саму ціль — це відновлення після обриву.
            // ✎ B74 P3 (D-576): маркер — на ПРОЄКТІ (не на парі): перенос одного шаблону зупиняє писарів усіх шаблонів проєкту.
            if (await store.ReadMigrationMarkerAsync(projectId, innerCt).ConfigureAwait(false) is { } marked && marked != target.Id)
            {
                throw MigrationInProgress(projectId);
            }

            // ⛔ L6-02: структура КОЖНОГО документа проєкту — винятково, за
            // зростанням `DocumentId`, ДО плану. Писарі (правка, імпорт, рядок,
            // подання, шапка, перерахунок) беруть її спільно першою дією, тож
            // далі жоден із них не комітиться між планом і `ApplyAsync`: план
            // бачить усе зафіксоване, а запис, що чекав, після коміту побачить
            // нову версію і відмовить (`DocumentStructure.EnsureUnchanged`).
            // Доти блокувався лише рядок `doc.Project`, якого писарі не беруть:
            // значення, введене в колонку, яку нова версія прибирає, між
            // плануванням і `DELETE` губилося при відповіді 200.
            foreach (var id in await store.ListDocumentIdsAsync(projectId, innerCt).ConfigureAwait(false))
            {
                await structureGate.EnterStructureAsync(id, exclusive: true, innerCt).ConfigureAwait(false);
            }

            if (onProgress is not null)
            {
                await onProgress(PlanningPercent, "jobs.migratePlanning", null, innerCt).ConfigureAwait(false);
            }

            planned = await PlanAsync(
                documentId, projectId, current, target, mode, archived, dryRun: false, renames,
                acceptGuardedOverwrite, lookupRemaps, allowLockedDocuments, innerCt).ConfigureAwait(false);
            var (dto, plan, foreignKeys) = (planned.Dto, planned.Plan, planned.ForeignKeys);
            ThrowIfRefused(
                dto, foreignKeys, plan.RenameConflicts, acceptGuardedOverwrite, plan.LookupGuardedValues,
                planned.AssignmentConflicts);

            // ⛔ Без підтвердженого бекапу великий перенос не стартує: частина комірок фіксується діапазонами, відкат -
            // лише відновленням з копії. Перевіряється ДО маркера - відмова нічого не залишає.
            if (plan.TransferredValues >= BackupConfirmationThreshold && string.IsNullOrWhiteSpace(BackupConfirmation))
            {
                throw new BusinessRuleException(
                    ErrorCodes.GuardedChangeWithoutStrategy,
                    $"Переноситься {plan.TransferredValues} значень (від {BackupConfirmationThreshold}): потрібне підтвердження резервної копії бази " +
                    "(поле backupConfirmation — шлях і/або час бекапу).",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SCHM-0422.migrateBackupRequired",
                        ["transferredValues"] = plan.TransferredValues.ToString(CultureInfo.InvariantCulture),
                        ["threshold"] = BackupConfirmationThreshold.ToString(CultureInfo.InvariantCulture),
                    });
            }

            grantedUsers = await store.ListUsersWithGrantsAsync(plan, MaxInvalidatedUsers, innerCt).ConfigureAwait(false);

            if (onProgress is not null)
            {
                var denominator = Math.Max(1L, plan.TransferredValues);
                var reached = StoreFloorPercent;
                onStep = async (stage, stepsDone, steps, changedRows, stepCt) =>
                {
                    // Крок вважається виконаним на частку змінених рядків відносно кількості значень, що
                    // переїздять (оцінка: різні кроки міняють різну кількість рядків), але не більше 90 %:
                    // доки пачки йдуть, крок не «закінчений». Відсоток не повертається назад.
                    var fraction = changedRows <= 0 ? 0d : Math.Min(0.9d, (double)changedRows / denominator);
                    var span = StoreCeilingPercent - StoreFloorPercent;
                    var percent = StoreFloorPercent + (int)(span * ((stepsDone + fraction) / Math.Max(1, steps)));
                    reached = Math.Max(reached, Math.Min(percent, StoreCeilingPercent));

                    await onProgress(
                        reached,
                        StageKey(stage),
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["rows"] = changedRows.ToString(CultureInfo.InvariantCulture),
                        },
                        stepCt).ConfigureAwait(false);
                };
            }

            // Маркер «міграція в процесі» — ОСТАННЄ в T1: комітиться разом з нею й відразу блокує писарів і читачів.
            await store.BeginMigrationAsync(projectId, target.Id, plan, profile.UserId, innerCt).ConfigureAwait(false);

            // ⛔ Початок переносу - у журнал безпеки ТІЄЮ САМОЮ транзакцією, що фіксує маркер (завершення пише T2). Обірваний
            // перенос лишає слід «розпочато» без «перенесено».
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow, StartedEventType, TargetUserId: null, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        projectId,
                        fromVersionId = current,
                        toVersionId = target.Id,
                        mode = dto.Mode.ToString(),
                        transferredValues = dto.TransferredValues,
                        backupConfirmation = BackupConfirmation is { Length: > 500 } longValue ? longValue[..500] : BackupConfirmation,
                    }),
                    profile.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (sameVersion)
        {
            return null;
        }

        // ── Важка частина: діапазони з комітом, без транзакції ──
        await store.MoveDataAsync(projectId, target.Id, planned.Plan, onStep, ct).ConfigureAwait(false);

        // ── T2: решта переносу й перемикання версії; маркер знімається тією самою командою ──
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var lockedVersion = await store.LockProjectVersionAsync(projectId, innerCt).ConfigureAwait(false)
                                ?? throw DocumentVisibility.NotFound(documentId);
            if (lockedVersion != current)
            {
                // Хтось довів перенос до кінця чи змінив версію між транзакціями: нічого не пишемо.
                throw SameVersion(target.Id);
            }

            foreach (var id in await store.ListDocumentIdsAsync(projectId, innerCt).ConfigureAwait(false))
            {
                await structureGate.EnterStructureAsync(id, exclusive: true, innerCt).ConfigureAwait(false);
            }

            var (dto, plan, extended) = (planned.Dto, planned.Plan, planned.Extended);

            // ⛔ Усе нижче (аудит, мапінги збору) може відмовити й відкочується разом з T2 — до перемикання версії.
            async Task BeforeCellsAsync(CancellationToken _)
            {
                formulaTables = plan.FormulaChanges.Count;

                // ✎ LAND-YEAR L3: перехід на рік — перерахунок ЗАВЖДИ: місячні колонки результатів і формул отримують
                // значення від власників-місяців (закриті місяці перерахунок не пише, їхнє лишається перенесеним на місці).
                if (planned.YearTransition)
                {
                    formulaTables = Math.Max(formulaTables, 1);
                }

                // ✎ D-461: позначки вже переїхали за колонками (діапазони); ті, яких цільова формула не дозволяє, —
                // знімаються, комірка знову обчислена (перерахунок проєкту нижче), журнал — FormulaRestored.
                var released = await ReleaseLostOverridesAsync(projectId, target.Id, profile.UserId, plan.FromPeriodKey, innerCt).ConfigureAwait(false);
                if (released > 0)
                {
                    formulaTables = Math.Max(formulaTables, 1);
                }

                // ⛔ Слід переносу значень Lookup шапки — на КОЖЕН документ, у ТІЙ САМІЙ транзакції (відкат переносу
                // відкочує й аудит). Старі id взято ДО запису, під блоком проєкту. ⚠ Перерахунок тут НЕ ставиться: кількості
                // комірок і результати не змінюються, а формули з HDR.<Lookup> ремап не охоплює (див. контракт).
                if (plan.LookupRemaps.Count > 0)
                {
                    var events = BuildLookupRemapEvents(planned.LookupRows, plan, projectId, current, target.Id, profile.UserId);
                    if (events.Count > 0)
                    {
                        await audit.WriteSecurityEventsAsync(events, innerCt).ConfigureAwait(false);
                    }
                }

                // ⛔ D-1: мапінги збору — в ТІЙ САМІЙ транзакції, що й документи й версія проєкту: збій відкочує все,
                // і збір не опиняється між версіями. Рахується те, що справді перенесено (мапінг могли додати між
                // планом і оновленням — він теж переїде, якщо його колонка є в мапі).
                var remapped = await store
                    .RemapCollectionMappingsAsync(
                        plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId),
                        extended.CollectionMappingIds ?? [], innerCt)
                    .ConfigureAwait(false);

                // ⛔ D-1b: мапінги подій джерела (документи проєкту) і прив'язки вікна — там само, тією ж транзакцією;
                // рішення плану (відповідники, зайняті цілі) перераховано під блоком проєкту вище.
                await store
                    .RemapEventAndWindowMapsAsync(
                        projectId, extended, plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId),
                        innerCt)
                    .ConfigureAwait(false);
                result = dto with
                {
                    Applied = true,
                    MappingsRemapped = remapped + extended.Events.Count + extended.Windows.Count,
                };

                // ⛔ F2-03 (аудит R11): журнал — В ТІЙ САМІЙ транзакції (`AuditWriter` бере поточну
                // транзакцію контексту): перенос структури всіх документів проєкту не комітиться без
                // сліду. Доти запис ішов після коміту й міг загубитися.
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                        JsonSerializer.Serialize(new
                        {
                            documentId,
                            projectId,
                            fromVersionId = result.FromVersionId,
                            toVersionId = result.ToVersionId,
                            mode = result.Mode.ToString(),
                            documents = result.DocumentCount,
                            transferredValues = result.TransferredValues,
                            mappingsRemapped = result.MappingsRemapped,
                            renames = plan.Items.Count(i => i.Kind == "Renamed"),

                            // LP-32: подані/затверджені пари «аркуш × період», перенесені без змін; «зачеплених» при
                            // застосуванні немає (вони були б відмовою), поле лишається для єдиної форми запису.
                            lockedSheetsCarried = result.LockedSheetsCarried,
                            lockedSheetsAffected = result.LockedSheetsAffected,
                            // LP-41: скільки таблиць додали/змінили формули; > 0 — після коміту ставиться перерахунок.
                            formulaTables = plan.FormulaChanges.Count,

                            // ACC-OW: скільки введених значень перезапише формула за згодою (0 без прапора) і в скількох
                            // комірках шаблону. ⛔ Лише кількості: ні адрес, ні значень.
                            guardedOverwritten = acceptGuardedOverwrite ? plan.GuardedOverwritableValues : 0L,
                            guardedOverwrittenCells = acceptGuardedOverwrite ? plan.GuardedOverwrites.Count : 0,

                            // Мапи Lookup шапки: скільки значень переписано й по скількох полях. ⛔ Лише кількості.
                            lookupRemapped = plan.LookupRemappedValues,
                            lookupRemapTargets = plan.LookupRemaps.Select(r => r.HeaderFieldDefId).Distinct().Count(),
                            backupConfirmation = BackupConfirmation is { Length: > 500 } longValue ? longValue[..500] : BackupConfirmation,

                            // ✎ LAND-YEAR L3 (план §d.3): мапа «стара немісячна колонка × місяць → місячна колонка» — щоб
                            // історія місячної колонки знайшла рядки aud.CellChange, записані ДО переносу на старий Id.
                            // ⛔ Лише ідентифікатори колонок шаблону; null — перенос без зміни розкладки.
                            monthColumns = plan.MonthColumns.Count == 0
                                ? null
                                : plan.MonthColumns
                                    .OrderBy(c => c.SourceColumnDefId).ThenBy(c => c.MonthNumber)
                                    .Select(c => new { o = c.SourceColumnDefId, m = c.MonthNumber, n = c.TargetColumnDefId })
                                    .ToList(),
                            monthTransferredValues = planned.YearTransition ? plan.MonthTransferredValues : (long?)null,
                            recomputedValues = planned.YearTransition ? plan.RecomputedValues : (long?)null,

                            // ✎ LAND-YEAR L3 (B-static): скільки немісячних значень якір року дістав з найпізнішого місяця.
                            nonMonthCopiedToAnchor = planned.YearTransition ? plan.NonMonthCopiedToAnchor : (long?)null,
                            nonMonthAnchorRowsCreated = planned.YearTransition ? plan.NonMonthAnchorRowsCreated : (long?)null,
                            nonMonthAnchorInstancesCreated = planned.YearTransition ? plan.NonMonthAnchorInstancesCreated : (long?)null,
                        }),
                        profile.UserId, currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);
            }

            await store.CompleteAsync(projectId, target.Id, plan, onStep, BeforeCellsAsync, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Профіль у кеші не бачить зміни грантів, яка не рухає відбиток груп
        // (прямі ролі): без явного скидання закрита колонка лишалася б доступною
        // до TTL (60 хв). Скидаються ВСІ записи користувача (будь-який відбиток
        // груп); збій або переповнення переліку — скидання всього кешу, не 500.
        GrantProfileInvalidation.Run(profileCache, log, grantedUsers);

        // RPT-STABLE Part B: шкала версій змінилась - скидаємо кеш (інші вузли побачать за TTL; писарі читають свіжо).
        timeline?.Invalidate(projectId);

        // ⛔ Перенос уже закомічено: хвіст після коміту НЕ бере токена запиту (обрив з'єднання чи зупинка процесу не мають
        // пропустити перерахунок чи вʼюхи вже перенесеного проєкту; F13 RC #872 P2-2). Порядок — спершу перерахунок
        // (дані), потім вʼюхи SSRS (другорядний шар).

        // ⛔ LP-41: нова/змінена формула без перерахунку лишається порожньою до явної дії людини (інкрементний
        // шлях не бере формул поза зміненими комірками). Один coalesced перерахунок проєкту — лише коли план
        // щось додав чи змінив і лише ПІСЛЯ коміту; сухий прогін і перенос без змін формул нічого не ставлять.
        if (result is not null && formulaTables > 0)
        {
            await MigrationRecalculation
                .EnqueueAsync(jobs, log, clock.UtcNow, projectId, formulaTables, CancellationToken.None)
                .ConfigureAwait(false);
        }

        // ⛔ V-5: нові аліаси колонок (cfg.ColumnRename) діють у стабільних вʼюхах лише після генерації; без неї
        // звіти SSRS на старому коді колонки для перенесеного проєкту порожні до перезапуску чи наступної публікації.
        // Генерація — фоновою задачею ReportViewsTask (P-4b), не в запиті: на Land вона триває хвилини (F13 P2-1).
        if (result is not null)
        {
            await MigrationReportViews
                .EnqueueAsync(
                    jobs, reportViewStatus, log, target.TemplateId, target.Id, planned.Plan.ColumnRenames.Count,
                    profile.UserId, currentUser.CorrelationId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return result!;
    }

    /// <summary>
    /// ✎ D-461: після переносу колонок знімає позначки, яких формула ЦІЛЬОВОЇ версії не дозволяє, і пише журнал
    /// (<c>FormulaRestored</c>, причина «Version migration», старе значення). Повертає кількість знятих.
    /// </summary>
    private async Task<int> ReleaseLostOverridesAsync(int projectId, int targetVersionId, int userId, int fromPeriodKey, CancellationToken ct)
    {
        if (formulaOverrides is null)
        {
            return 0;
        }

        var to = await metadata.GetAsync(targetVersionId, ct).ConfigureAwait(false);
        // ✎ RPT-STABLE B2: перенос «з місяця» не чіпає періодів до X - їхні ручні значення лишаються на старій версії.
        // ⛔ B74 P3 (D-576): лише позначки документів пари — колонки інших шаблонів проєкту не належать цільовій версії, і
        // без фільтра їхні ручні значення «втратились» би (знято позначку) переносом чужого шаблону.
        var documents = (await store.ListDocumentIdsAsync(projectId, ct).ConfigureAwait(false)).ToHashSet();
        var marks = OfDocuments(
            InScope(await formulaOverrides.ReadProjectAsync(projectId, ct).ConfigureAwait(false), fromPeriodKey), documents);
        Func<int, int?> targetColumnOf = column => to.ColumnsById.ContainsKey(column) ? column : null;
        var lost = FormulaOverrideMigration.Lost(marks, targetColumnOf, to);

        // ✎ Аудит 31.10 (F2): цільова комірка без жодної формули — позначка знімається, але значення лишається
        // ЗВИЧАЙНИМ введеним (`IsCalculated = 0`, без `ReleaseAsync`): доти воно ставало «обчисленим» у колонці вводу, а
        // журнал казав `FormulaRestored` з `NewValue = null`, хоча формули немає й значення не змінилося. Журнал —
        // `FormulaOverride` (людське походження, як і було: сторожі інтеграції й далі бачать значення людини) зі
        // значенням без змін і окремою причиною `UnformulatedReason`.
        var unformulated = FormulaOverrideMigration.Unformulated(marks, targetColumnOf, to);
        if (lost.Count == 0 && unformulated.Count == 0)
        {
            return 0;
        }

        if (lost.Count > 0)
        {
            await formulaOverrides.ReleaseAsync([.. lost.Select(m => m.Address)], ct).ConfigureAwait(false);
        }

        if (unformulated.Count > 0)
        {
            await formulaOverrides.ApplyAsync([], [.. unformulated.Select(m => m.Address)], ct).ConfigureAwait(false);
        }

        var now = clock.UtcNow;
        await audit.WriteCellChangesAsync(
            [
                .. lost.Select(m => new CellChangeRecord(
                    now, m.Address, m.DocumentId, m.RowKey, OldValue: m.Value, NewValue: null, userId,
                    CellChangeOrigins.FormulaRestored, IsLateEdit: false, CorrelationId: null,
                    Reason: FormulaOverrideMigration.Reason)),
                .. unformulated.Select(m => new CellChangeRecord(
                    now, m.Address, m.DocumentId, m.RowKey, OldValue: m.Value, NewValue: m.Value, userId,
                    CellChangeOrigins.FormulaOverride, IsLateEdit: false, CorrelationId: null,
                    Reason: FormulaOverrideMigration.UnformulatedReason)),
            ],
            ct).ConfigureAwait(false);

        return lost.Count;
    }

    /// <summary>
    /// Події аудиту «значення Lookup шапки перенесено на запис нового довідника» — по одній на документ.
    /// </summary>
    /// <remarks>
    /// ⛔ Лише id (старий/новий запис) і код поля; тексту дозволу в журналі немає. <c>locked</c> — інформативно:
    /// у документа є подані/затверджені аркуші; на допуск переносу не впливає (шапка не дані аркуша).
    /// </remarks>
    private List<SecurityEventRecord> BuildLookupRemapEvents(
        IReadOnlyList<HeaderLookupValueRow> rows, VersionMigrationPlan plan, int projectId,
        int fromVersionId, int toVersionId, int userId)
    {
        var map = plan.LookupRemaps.ToDictionary(r => (r.HeaderFieldDefId, r.OldEntryId));
        var codes = plan.LookupRemaps.GroupBy(r => r.HeaderFieldDefId).ToDictionary(g => g.Key, g => g.First().FieldCode);
        var now = clock.UtcNow;
        var events = new List<SecurityEventRecord>();

        foreach (var document in rows.Where(r => r.EntryId is { } e && map.ContainsKey((r.HeaderFieldDefId, e)))
                     .GroupBy(r => r.DocumentId).OrderBy(g => g.Key))
        {
            var fields = document
                .OrderBy(r => codes[r.HeaderFieldDefId], StringComparer.Ordinal)
                .Select(r => new
                {
                    code = codes[r.HeaderFieldDefId],
                    oldEntryId = r.EntryId!.Value,
                    newEntryId = map[(r.HeaderFieldDefId, r.EntryId!.Value)].NewEntryId,
                    locked = r.Locked,
                })
                .ToList();

            events.Add(new SecurityEventRecord(
                now, LookupRemapEventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    documentId = document.Key,
                    projectId,
                    fromVersionId,
                    toVersionId,
                    fields,
                }),
                userId, currentUser.CorrelationId));
        }

        return events;
    }

    /// <summary>Перелік версій, на які можна перенести документ.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentVersionMigrationTargetsDto> ListTargetsAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);
        _ = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        // ✎ B74 P3 (D-576): чинна версія — версія пари документа; цілі — версії ЙОГО шаблону.
        var pair = await UsePairAsync(documentId, ct).ConfigureAwait(false);
        var current = await versions.GetAsync(pair.CurrentVersionId, ct).ConfigureAwait(false);
        var all = await templates
            .ListVersionsForTemplatesAsync([current.TemplateId], MaxTargets, ct)
            .ConfigureAwait(false);

        var targets = all.GetValueOrDefault(current.TemplateId, [])
            .Where(v => v.Id != current.Id && v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.Id)
            .ToList();

        return new DocumentVersionMigrationTargetsDto(projectId, current.Id, current.Version, targets, _fromMonthEnabled);
    }

    /// <summary>Розв'язаний обсяг переносу: межа для сховища (0 — увесь проєкт), ефективний початок, попередження.</summary>
    private sealed record RangeInfo(int FromPeriodKey, int? Effective, IReadOnlyList<VersionMigrationWarning>? Warnings);

    private static BusinessRuleException RangeError(string key, string message, params (string Name, int Value)[] details)
    {
        var map = new Dictionary<string, object?> { ["messageKey"] = key };
        foreach (var (name, value) in details)
        {
            map[name] = value.ToString(CultureInfo.InvariantCulture);
        }

        return new BusinessRuleException(ErrorCodes.GuardedChangeWithoutStrategy, message, map);
    }

    /// <summary>
    /// RPT-STABLE Part B: обсяг переносу. <c>WholeProject</c> можливий лише при одному сегменті шкали; <c>FromMonth</c> -
    /// з періоду X проєкту, не раніше початку останнього сегмента; для проєкту з таблицею «місяці в колонках» (Q-17) X
    /// не січня зсувається на січень цього року (рішення людини 30.10) - попередження.
    /// </summary>
    private async Task<RangeInfo> ResolveRangeAsync(
        int projectId, TemplateVersion target, TemplateVersionSnapshot from, TemplateVersionSnapshot to, CancellationToken ct)
    {
        if (target.Status != TemplateVersionStatus.Published)
        {
            throw RangeError("err.ECR-SCHM-0422.migrateTargetNotPublished", $"Цільова версія {target.Id} не опублікована.");
        }

        var periodKeys = await store.ListPeriodKeysAsync(projectId, ct).ConfigureAwait(false);
        timeline?.Invalidate(projectId);   // план читає шкалу свіжо: вона може змінитись іншим вузлом
        var entries = timeline is null
            ? []
            : await OfTemplateAsync(await timeline.GetAsync(projectId, ct).ConfigureAwait(false), target.TemplateId, ct)
                .ConfigureAwait(false);
        int? first = periodKeys.Count == 0 ? null : periodKeys[0];

        if (Range == VersionMigrationRange.WholeProject)
        {
            if (entries.Count > 1)
            {
                throw RangeError(
                    "err.ECR-SCHM-0422.migrateWholeProjectMultiVersion",
                    $"Проєкт {projectId} має кілька версій шаблону за періодами: перенос усього проєкту недоступний.");
            }

            return new RangeInfo(0, first, null);
        }

        if (FromPeriodKey is not { } requested)
        {
            throw RangeError("err.ECR-SCHM-0422.migrateFromMonthRequired", "Для переносу «з місяця» потрібен fromPeriodKey.");
        }

        if (periodKeys.Count == 0 || !periodKeys.Contains(requested))
        {
            throw RangeError("err.ECR-SCHM-0422.migrateFromMonthInvalid", $"Період {requested} не належить проєкту {projectId}.", ("periodKey", requested));
        }

        var hasMonths = from.Sheets.Concat(to.Sheets).SelectMany(s => s.Tables)
            .Any(t => !t.IsDeleted && t.LayoutKind == TableLayoutKind.MonthsInColumns);
        var (effective, shifted) = Ecr.Domain.ValueObjects.ProjectVersionTimeline.EffectiveFrom(requested, hasMonths);

        // Січень року X міг бути до початку проєкту: беремо перший наявний період не раніше за нього.
        effective = periodKeys.First(p => p >= effective);

        var segmentFrom = entries.Count == 0 ? 0 : entries[^1].FromPeriodKey;
        if (effective < segmentFrom)
        {
            throw RangeError(
                "err.ECR-SCHM-0422.migrateFromMonthBeforeSegment",
                $"Період {requested} раніше початку чинного сегмента версій ({segmentFrom}).",
                ("periodKey", requested), ("segmentFrom", segmentFrom));
        }

        List<VersionMigrationWarning>? warnings = null;
        if (shifted)
        {
            warnings =
            [
                new VersionMigrationWarning("effectiveFromShiftedToJanuary", "documents.migrateWarnEffectiveFromJanuary", effective),
            ];
        }

        // Початок = перший період проєкту дорівнює «увесь проєкт» для сховища (нема періодів до X).
        return new RangeInfo(effective <= periodKeys[0] ? 0 : effective, effective, warnings);
    }

    /// <summary>
    /// ✎ B74 P3 (D-576): шкала проєкту несе рядки ВСІХ його шаблонів (<c>doc.ProjectTemplateVersion</c> ключується парою);
    /// перенос дивиться лише на сегменти шаблону пари — інакше рядок іншого шаблону робив би «кілька версій».
    /// </summary>
    private async Task<IReadOnlyList<Ecr.Domain.ValueObjects.TimelineEntry>> OfTemplateAsync(
        IReadOnlyList<Ecr.Domain.ValueObjects.TimelineEntry> entries, int templateId, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        var own = new HashSet<int>();
        foreach (var versionId in entries.Select(e => e.TemplateVersionId).Distinct())
        {
            if ((await versions.FindAsync(versionId, ct).ConfigureAwait(false))?.TemplateId == templateId)
            {
                own.Add(versionId);
            }
        }

        return own.Count == entries.Select(e => e.TemplateVersionId).Distinct().Count()
            ? entries
            : [.. entries.Where(e => own.Contains(e.TemplateVersionId))];
    }

    /// <summary>
    /// RPT-STABLE Part B + R-2: пари «колонка → колонка» для <c>cfg.ColumnRename</c>: змінився код КОЛОНКИ або код ТАБЛИЦІ
    /// (таблицю перейменовано мапою) — тоді й колонки з тим самим кодом, щоб стабільна вʼюха й міжперіодні формули знайшли
    /// відповідник.
    /// </summary>
    /// <remarks>
    /// ✎ B130: пари впорядковані за (From, To) — ключем <c>PK_ColumnRename</c>: вставка 6b (<c>FinishSql</c>) іде в порядку
    /// ключа. Повтори прибирає сам SQL (<c>DISTINCT</c> у <c>ColumnRenameInsertSql</c>), не цей список.
    /// </remarks>
    internal static IReadOnlyList<(int FromColumnDefId, int ToColumnDefId)> RenamePairs(
        VersionMigrationPlan plan, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        var fromTables = from.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id, t => t.Code);
        var toTables = to.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id, t => t.Code);
        return [.. plan.Columns
            .Where(c => from.ColumnsById.TryGetValue(c.SourceColumnDefId, out var f)
                        && to.ColumnsById.TryGetValue(c.TargetColumnDefId, out var t)
                        && (!string.Equals(f.Code, t.Code, StringComparison.Ordinal)
                            || (fromTables.TryGetValue(f.TableDefId, out var ft) && toTables.TryGetValue(t.TableDefId, out var tt)
                                && !string.Equals(ft, tt, StringComparison.Ordinal))))
            .Select(c => (c.SourceColumnDefId, c.TargetColumnDefId))
            .OrderBy(p => p.SourceColumnDefId)
            .ThenBy(p => p.TargetColumnDefId)];
    }

    /// <summary>
    /// ✎ D-461 + LAND-YEAR L3: позначки ручних значень, що не переживуть перенос. Позначка немісячної колонки з місячними
    /// цілями оцінюється на цілі МІСЯЦЯ СВОГО ПЕРІОДУ (сховище переносить її туди ж, <c>PeriodKey % 100</c>).
    /// </summary>
    private static List<ProjectFormulaOverride> LostOverrides(
        IReadOnlyList<ProjectFormulaOverride> marks, Dictionary<int, int> columnMap,
        IReadOnlyList<VersionMigrationMonthColumn> monthColumns, TemplateVersionSnapshot to)
    {
        int? Resolve(int column) => columnMap.TryGetValue(column, out var mapped) ? mapped : to.ColumnsById.ContainsKey(column) ? column : null;

        if (monthColumns.Count == 0)
        {
            return FormulaOverrideMigration.Lost(marks, Resolve, to);
        }

        var months = monthColumns.ToDictionary(c => (c.SourceColumnDefId, c.MonthNumber), c => c.TargetColumnDefId);
        var sources = monthColumns.Select(c => c.SourceColumnDefId).ToHashSet();
        var lost = FormulaOverrideMigration.Lost([.. marks.Where(m => !sources.Contains(m.Address.ColumnDefId))], Resolve, to);
        foreach (var group in marks.Where(m => sources.Contains(m.Address.ColumnDefId)).GroupBy(m => m.Address.PeriodKey.Value % 100))
        {
            var month = group.Key;
            lost.AddRange(FormulaOverrideMigration.Lost(
                [.. group], column => months.TryGetValue((column, month), out var target) ? target : null, to));
        }

        return lost;
    }

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): конфлікти кроку якоря — у коди цільової версії (аркуш, таблиця, колонка). Невідомий
    /// ідентифікатор (не мав би траплятися: колонки якоря взято з цього ж знімка) — сам ідентифікатор текстом.
    /// </summary>
    private static List<NonMonthConflictItem> AnchorConflictItems(VersionMigrationAnchorFill fill, TemplateVersionSnapshot to)
    {
        var tables = to.Sheets.SelectMany(s => s.Tables.Select(t => (Sheet: s.Code, Table: t)))
            .ToDictionary(x => x.Table.Id, x => (x.Sheet, x.Table.Code));
        return [.. fill.Conflicts.Select(c =>
        {
            var (sheet, table) = tables.TryGetValue(c.TableDefId, out var found)
                ? found
                : (string.Empty, c.TableDefId.ToString(CultureInfo.InvariantCulture));
            var column = to.ColumnsById.TryGetValue(c.ColumnDefId, out var def)
                ? def.Code
                : c.ColumnDefId.ToString(CultureInfo.InvariantCulture);
            return new NonMonthConflictItem(c.DocumentId, sheet, table, c.RowKey, column, c.Values);
        })];
    }

    /// <summary>RPT-STABLE B2: позначки ручних значень лише в періодах, які перенос зачіпає (0 - усі).</summary>
    private static IReadOnlyList<ProjectFormulaOverride> InScope(IReadOnlyList<ProjectFormulaOverride> marks, int fromPeriodKey)
        => fromPeriodKey == 0 ? marks : [.. marks.Where(m => m.Address.PeriodKey.Value >= fromPeriodKey)];

    /// <summary>✎ B74 P3 (D-576): позначки лише документів пари (документи інших шаблонів проєкту перенос не чіпає).</summary>
    private static IReadOnlyList<ProjectFormulaOverride> OfDocuments(
        IReadOnlyList<ProjectFormulaOverride> marks, HashSet<long> documentIds)
        => marks.All(m => documentIds.Contains(m.DocumentId)) ? marks : [.. marks.Where(m => documentIds.Contains(m.DocumentId))];

    /// <summary>План переносу разом зі звітом, мапінгами й значеннями шапки, які читав аудит мап Lookup.</summary>
    private sealed record PlannedMigration(
        DocumentVersionMigrationDto Dto,
        VersionMigrationPlan Plan,
        IReadOnlyList<string> ForeignKeys,
        ExtendedMappingRemap Extended,
        IReadOnlyList<HeaderLookupValueRow> LookupRows,
        int AssignmentConflicts = 0,
        bool YearTransition = false);

    /// <summary>Підготовлені мапи Lookup: вхід планувальника, прочитані значення, цілі поза парою версій.</summary>
    private sealed record LookupPrepared(
        VersionMigrationLookupInput? Input,
        IReadOnlyList<HeaderLookupValueRow> Rows,
        IReadOnlyList<LookupRemapReportItem> NotApplied);

    private async Task<PlannedMigration> PlanAsync(
        long documentId, int projectId, int sourceVersionId, TemplateVersion target,
        VersionMigrationMode mode, bool archived, bool dryRun,
        IReadOnlyList<VersionMigrationTableRename>? renames, bool acceptGuardedOverwrite,
        IReadOnlyList<VersionMigrationLookupRemap>? lookupRemaps, bool allowLockedDocuments, CancellationToken ct)
    {
        var source = await versions.GetAsync(sourceVersionId, ct).ConfigureAwait(false);
        var from = await metadata.GetAsync(sourceVersionId, ct).ConfigureAwait(false);
        var to = await metadata.GetAsync(target.Id, ct).ConfigureAwait(false);
        EnsureRangeAllowed();   // також для фонової задачі: прапор могли вимкнути між постановкою і стартом
        var range = await ResolveRangeAsync(projectId, target, from, to, ct).ConfigureAwait(false);
        var scope = await store.ReadScopeAsync(projectId, range.FromPeriodKey, ct).ConfigureAwait(false);

        // Без мап нових читань немає: шлях і кількість запитів як до мап Lookup.
        var lookup = await PrepareLookupAsync(projectId, from, to, lookupRemaps, ct).ConfigureAwait(false);

        // ✎ LAND-YEAR L3: місячний проєкт переходить з «копії на місяць» на «рік, місяці в колонках» — немісячні колонки
        // без відповідника за кодом шукають місячні цілі (C → C_M01..C_M12 або псевдоніми), селектори — «перерахується».
        var yearTransition = source.Layout == TemplateLayout.PerMonthCopies
                             && target.Layout == TemplateLayout.YearWithMonthColumns
                             && (await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false))?.PeriodKind == PeriodKind.Monthly;

        var plan = VersionMigrationPlanner.Plan(
            from, to, scope.Cells, scope.HeaderValues, renames, lookup.Input, monthColumns: yearTransition);
        plan = plan with { FromPeriodKey = range.FromPeriodKey, ColumnRenames = RenamePairs(plan, from, to) };

        // ✎ LAND-YEAR L3 (B-static): річний перегляд читає немісячний ввід із ЯКОРЯ року, а «копія на місяць» могла тримати
        // його лише в пізніших місяцях (Land doc 96, T8_00 — у 202609..202611, якір 202601 порожній). Колонки якоря — введені
        // немісячні колонки цільової версії; сам крок (копія з найпізнішого місяця, конфлікт — відмова) рахує сховище.
        if (yearTransition)
        {
            plan = plan with
            {
                AnchorColumns = [.. to.Sheets.Where(s => !s.IsDeleted).SelectMany(s => s.Tables).Where(t => !t.IsDeleted)
                    .SelectMany(t => t.Columns)
                    .Where(c => !c.IsDeleted && !c.IsMonthColumn && !c.IsComputed)
                    .Select(c => c.Id)
                    .Order()],
            };
        }

        // ✎ D-461: ручні значення, яких цільова формула не дозволяє, — пункти «OverrideLost» (перенос не блокують).
        var overrideMap = plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId);
        var lostOverrides = formulaOverrides is null
            ? []
            : LostOverrides(
                OfDocuments(
                    InScope(await formulaOverrides.ReadProjectAsync(projectId, ct).ConfigureAwait(false), range.FromPeriodKey),
                    scope.DocumentIds.ToHashSet()),
                overrideMap, plan.MonthColumns, to);
        if (lostOverrides.Count > 0)
        {
            plan = plan with { Items = [.. plan.Items, .. FormulaOverrideMigration.Items(lostOverrides, from)] };
        }

        // ACC-OW: прапор знімає guardedWithData лише для нової комірочної формули на комірці зі значенням.
        var refusals = plan.RefusalsFor(mode, acceptGuardedOverwrite).ToList();

        // ⛔ LP-32 (locked-safe): подані/затверджені аркуші самі по собі перенос не блокують. Відмова — лише коли план
        // ЗАЧІПАЄ такий аркуш (див. `VersionMigrationPlan.TouchedSheets`): подане стосується СТАРОЇ структури, і
        // видати його за затвердження нової не можна. Решта лишається як є: `WorkflowSql` перемапує стани й журнал на
        // нові `SheetDefId`, значення переїжджають за кодами, перерахунок Submitted/Approved не пише.
        // ⚠ Аркуш, якого немає серед живих вихідної версії (не потрапив у `plan.Sheets`), `WorkflowSql` ВИДАЛИВ би
        // разом зі станом, тому рахується зачепленим.
        // ✎ LAND-YEAR L3 (B-static, рішення координатора): немісячна комірка без значення в якорі дістає значення
        // НАЙПІЗНІШОГО місяця року (тією самою транзакцією перемикання версії; бракує екземпляра таблиці чи рядків у
        // якорі — вони створюються); місяці не згодні — відмова з переліком, система мовчки не вибирає; значення якоря не
        // чіпається ніколи. Рахується ДО LP-32: запис у поданий/затверджений аркуш якоря — це зачеплений аркуш.
        var anchorFill = VersionMigrationAnchorFill.None;
        if (yearTransition && plan.AnchorColumns.Count > 0)
        {
            anchorFill = await store.ReadAnchorFillAsync(projectId, plan, MaxItems, ct).ConfigureAwait(false);
            plan = plan with
            {
                NonMonthCopiedToAnchor = anchorFill.Copies,
                NonMonthAnchorRowsCreated = anchorFill.RowsCreated,
                NonMonthAnchorInstancesCreated = anchorFill.InstancesCreated,
            };
            if (anchorFill.ConflictCells > 0)
            {
                refusals.Add("nonMonthConflict");
            }
        }

        var locked = ClassifyLocked(scope, plan, from, AnchorWrittenSheets(anchorFill, plan, to));
        if (locked.Affected > 0)
        {
            refusals.Add("sheetsLocked");
        }

        // ✎ V-7 (рішення PO 11.11, варіант А): ручне значення (D-461), яке перенос зняв би (`lostOverrides`), на
        // поданому/затвердженому аркуші змінює його дані (позначка, IsCalculated, журнал FormulaRestored), а перерахунок
        // такий аркуш не пише. Тому — відмова, доки аркуш не повернуть у роботу, як решта LP-32. Окрема причина: план
        // структури цей аркуш може й не зачіпати.
        var lockedOverrides = LockedOverrideTouches(lostOverrides, from, scope.LockedSheetRefs);
        if (lockedOverrides.Marks > 0)
        {
            refusals.Add("lockedOverridesLost");
        }

        // ⛔ Шапка закритого документа (є подані/затверджені аркуші) мігрується лише з явною згодою
        // (`allowLockedDocuments`, рішення людини 21.10): подане стосується значень шапки в тому вигляді, в якому його
        // подали. Рахуються документи, значення шапки яких справді перепише мапа.
        int? lockedDocumentCount = null;
        if (lookup.Input is not null)
        {
            var remapKeys = plan.LookupRemaps.Select(r => (r.HeaderFieldDefId, r.OldEntryId)).ToHashSet();
            lockedDocumentCount = lookup.Rows
                .Where(r => r.Locked && r.EntryId is { } e && remapKeys.Contains((r.HeaderFieldDefId, e)))
                .Select(r => r.DocumentId)
                .Distinct()
                .Count();
            if (lockedDocumentCount > 0 && !allowLockedDocuments)
            {
                refusals.Add("lookupLockedDocuments");
            }
        }

        if (archived)
        {
            refusals.Add("projectArchived");
        }

        // ⛔ Грант (заборона АБО дозвіл) на ресурсі, якого в новій версії за кодом немає,
        // нікуди не копіюється. Дозвіл теж звужує доступ (Read під Write проєкту — береться
        // найдрібніший рівень). Якщо ресурс просто перейменували, новий код лишився б без
        // обмеження (fail-open) — тому перенос відмовляє, доки грант на старому
        // ресурсі свідомо не зніме адміністратор безпеки.
        // ✎ LAND-YEAR L3: грант немісячної колонки з місячними цілями копіюється на кожну з них (FinishSql, @grantMap).
        var mappedColumns = plan.Columns.Select(c => c.SourceColumnDefId)
            .Concat(plan.MonthColumns.Select(c => c.SourceColumnDefId))
            .ToHashSet();
        var denied = await store.CountDenyGrantsAsync(
            [.. from.Sheets.Where(s => !s.IsDeleted && !plan.Sheets.ContainsKey(s.Id)).Select(s => s.Id)],
            [.. from.Sheets.SelectMany(s => s.Tables).Where(t => !t.IsDeleted && !plan.Tables.ContainsKey(t.Id)).Select(t => t.Id)],
            [.. from.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns)
                .Where(c => !c.IsDeleted && !mappedColumns.Contains(c.Id)).Select(c => c.Id)],
            ct).ConfigureAwait(false);
        if (denied > 0)
        {
            refusals.Add("grantsNotMapped");
        }

        // ⛔ D-6 (+C-2): аркуш, якого цільова версія не має, з копіями (RS1). WorkflowSql видаляє і копію, а її екземпляри
        // таблиць іще живі (FK_TableInstance_SheetCopy) — T2 падав би 547 ПІСЛЯ закомічених діапазонів і лишав маркер.
        // Копія завжди має власні екземпляри, тож рахується будь-яка копія, а не лише з даними. У режимі «з місяця»
        // (FromPeriodKey > 0) склад документа не видаляється — відмова не потрібна.
        var removedSheets = from.Sheets.Where(s => !s.IsDeleted && !plan.Sheets.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        if (range.FromPeriodKey == 0
            && await store.CountSheetCopiesAsync(projectId, removedSheets, ct).ConfigureAwait(false) > 0)
        {
            refusals.Add("sheetCopiesNotMapped");
        }

        // ⛔ D-10-b: мапа перейменовує таблицю X → Y, а передача зрізу під Y у документі вже є. Перейменування передачі
        // X дало б той самий ключ зрізу — 2601 на UQ_EditAssignment ПІСЛЯ маркера, відкат усього переносу із загальною
        // помилкою. Відмова — тут, на етапі плану: спершу зняти одну з двох передач.
        var assignmentConflicts = await store.CountAssignmentRenameConflictsAsync(projectId, plan, ct).ConfigureAwait(false);
        if (assignmentConflicts > 0)
        {
            refusals.Add("assignmentsConflict");
        }

        // ⛔ D-13: активна прив'язка методології до колонки вихідної версії, якої нема в цільовій, після
        // переносу не рахувала б нічого — колонка лишалася б порожньою без помилки. Блокуємо, доки
        // конфігуратор не перенесе прив'язку (клон версії тепер копіює їх сам) або не вимкне її.
        // ✎ LAND-YEAR L3: одна стара прив'язка немісячної колонки ↔ 12 прив'язок її місячних колонок.
        var columnMap = plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId);
        var unmappedBindings = plan.MonthColumns.Count == 0
            ? await store.CountUnmappedBindingsAsync(sourceVersionId, columnMap, ct).ConfigureAwait(false)
            : await store.CountUnmappedBindingsAsync(
                    sourceVersionId, columnMap,
                    plan.MonthColumns.GroupBy(c => c.SourceColumnDefId)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(c => c.TargetColumnDefId)]),
                    ct)
                .ConfigureAwait(false);
        if (unmappedBindings > 0)
        {
            refusals.Add("bindingsNotMapped");
        }

        // ⛔ D-1 (RC20): мапінг збору адресує колонку числовим ColumnDefId КОНКРЕТНОЇ версії; після переносу
        // проєкту матеріалізація шукає екземпляри за цією колонкою, і мапінг, що лишився на колонці старої версії,
        // «успішно» збирав би точки й нічого не писав. Мапінги з відповідником (за кодами — той самий план, що й
        // для комірок) переносяться в тій самій транзакції; активний мапінг без відповідника — відмова, як і для
        // прив'язок методологій (призупинений мапінг нічого не пише й перенос не блокує).
        var mappings = await store.ListCollectionMappingsAsync(sourceVersionId, ct).ConfigureAwait(false);

        // ⛔ D-1b P2-1/P2-2: колонка з відповідником ще не означає придатного місця — режим рядків таблиці мусить
        // збігатися, а рядок-адресат (TargetRowKey) фіксованої таблиці — існувати в цільовій версії.
        var collectionPlan = MappingRemapPlanner.PlanCollection(from, to, plan, mappings);
        var mappingsRemapped = collectionPlan.RemapIds.Count;
        var unmappedKeys = collectionPlan.Unmapped.Where(u => u.Active).Select(u => u.Mapping.ColumnPath).ToList();
        var unmappedCount = unmappedKeys.Count;

        // ⛔ D-1b (RC20): той самий клас дефекту для мапінгів подій джерела (адресують ТАБЛИЦЮ й колонки версії
        // і належать документу) і прив'язок вікна (таблиця + складені ключі колонок). Відповідність — за тим
        // самим планом (plan.Tables/plan.Columns); мапінги лише документів ЦЬОГО проєкту й лише таблиць двох
        // версій цього шаблону. Активний мапінг без відповідника (або з зайнятою ціллю) — та сама відмова.
        var eventMaps = await store.ListEventMapsAsync(scope.DocumentIds, ct).ConfigureAwait(false);
        var windowMaps = await store
            .ListRowWindowMapsAsync(
                [.. from.Sheets.Concat(to.Sheets).SelectMany(s => s.Tables).Select(t => t.Id)], ct)
            .ConfigureAwait(false);
        var extendedPlan = MappingRemapPlanner.Plan(from, to, plan, eventMaps, windowMaps);
        mappingsRemapped += extendedPlan.Remapped;
        var extendedUnmapped = extendedPlan.Unmapped.Where(u => u.IsActive).ToList();
        unmappedCount += extendedUnmapped.Count;
        unmappedKeys.AddRange(extendedUnmapped.Select(u => u.Key));

        if (unmappedCount > 0)
        {
            refusals.Add("mappingsNotMapped");
        }

        // ⛔ D1: правила й обов'язкові входи методології адресують колонки числовим ColumnDefId КОНКРЕТНОЇ версії
        // шаблону. Якщо методологія, прив'язана до цільової версії, має опубліковану версію з ключами на колонки
        // іншої версії цього шаблону, то після переносу предикат не збігається ніколи (прогін мовчки порожній), а
        // вимога Block блокує збереження назавжди. Блокується лише перенос, не публікація шаблону.
        // ✎ C1: ключі, чия колонка має відповідник за шляхом у цілі, перекладаються при читанні
        // (MethodologyKeyLocalizer) і сюди не потрапляють; відмова лишилась для колонок, втрачених у цілі.
        var foreignKeys = await store.ListForeignMethodologyKeysAsync(target.Id, MaxMethodologyKeysListed, ct)
            .ConfigureAwait(false);
        if (foreignKeys.Count > 0)
        {
            refusals.Add("methodologyKeysNotMapped");
        }

        // D-1b P3: мапінги глобальні, тож інші проєкти на вихідній версії після переносу не збиратимуться — звіт
        // каже скільки (лише кількість; перенос не блокується: інакше перший перенос у багатопроєктному шаблоні
        // неможливий).
        var others = await store.CountOtherProjectsOnVersionAsync(projectId, sourceVersionId, ct).ConfigureAwait(false);

        // Спершу те, що зачіпає введені дані, потім решта структури, потім вигляд.
        var ordered = plan.Items
            .OrderByDescending(i => i.Values > 0)
            .ThenByDescending(i => i.ChangeClass)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        var dto = new DocumentVersionMigrationDto(
            documentId, projectId, scope.DocumentIds.Count,
            source.Id, source.Version, target.Id, target.Version,
            mode, dryRun, Applied: false, CanApply: refusals.Count == 0, refusals,
            plan.TransferredValues, plan.LostValues, plan.GuardedValues, scope.LockedSheets,
            [.. ordered.Take(MaxItems)], ordered.Count > MaxItems,
            denied > 0 ? denied : null,
            mappingsRemapped,
            unmappedCount,
            unmappedCount == 0
                ? null
                : [.. unmappedKeys
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(MaxMappingKeysListed)],
            others.Projects,
            others.Documents,
            locked.Carried,
            locked.Affected,
            locked.Items.Count == 0 ? null : locked.Items,
            locked.Truncated,
            plan.GuardedOverwritableValues,
            acceptGuardedOverwrite,
            plan.GuardedOverwrites.Count == 0 ? null : [.. plan.GuardedOverwrites.Take(MaxGuardedOverwriteItems)],
            plan.GuardedOverwrites.Count > MaxGuardedOverwriteItems,
            LookupRemapReport: lookup.Input is null && lookup.NotApplied.Count == 0
                ? null
                : [.. plan.LookupReport.Concat(lookup.NotApplied).OrderBy(i => i.Target.Code, StringComparer.Ordinal)],
            LookupRemapped: lookup.Input is null && lookup.NotApplied.Count == 0 ? null : plan.LookupRemappedValues,
            LockedDocumentCount: lockedDocumentCount,
            LostFormulaOverrides: lostOverrides.Count == 0 ? null : lostOverrides.Count,
            Range: Range,
            FromPeriodKey: Range == VersionMigrationRange.FromMonth ? FromPeriodKey : null,
            EffectiveFromPeriodKey: range.Effective,
            Warnings: range.Warnings,
            LockedOverridesLost: lockedOverrides.Marks == 0 ? null : lockedOverrides.Marks,
            LockedOverrideItems: lockedOverrides.Items.Count == 0 ? null : lockedOverrides.Items,
            MonthTransferredValues: yearTransition ? plan.MonthTransferredValues : null,
            RecomputedValues: yearTransition ? plan.RecomputedValues : null,
            MonthColumnsUnmapped: plan.MonthColumnsUnmapped.Count == 0 ? null : [.. plan.MonthColumnsUnmapped.Take(MaxItems)],
            NonMonthCopiedToAnchor: yearTransition ? anchorFill.Copies : null,
            NonMonthConflicts: anchorFill.ConflictCells == 0 ? null : AnchorConflictItems(anchorFill, to),
            NonMonthConflictsTruncated: anchorFill.ConflictCells == 0 ? null : anchorFill.ConflictCells > anchorFill.Conflicts.Count,
            NonMonthAnchorRowsCreated: yearTransition ? anchorFill.RowsCreated : null,
            AnchorInstancesCreated: yearTransition ? anchorFill.InstancesCreated : null,
            NonMonthSkippedArchived: yearTransition ? anchorFill.SkippedArchived : null);

        return new PlannedMigration(
            dto, plan, foreignKeys, extendedPlan.Remap with { CollectionMappingIds = collectionPlan.RemapIds }, lookup.Rows,
            assignmentConflicts, yearTransition);
    }

    /// <summary>
    /// Готує вхід планувальника з мап запиту: читає значення шапки (один запит), перевіряє, що старі записи з
    /// вихідного довідника, і придатність нових (як <c>ECR-HDR-4223</c>). Без мап — нічого не читає.
    /// </summary>
    /// <remarks>
    /// Ціль, якої нема в парі версій або чий довідник не змінився, мовчки пропускається (<c>applied = false</c>):
    /// одну й ту саму мапу можна подавати на кожен проєкт. Пари, чий старий запис ніде не вжито, ігноруються.
    /// </remarks>
    private async Task<LookupPrepared> PrepareLookupAsync(
        int projectId, TemplateVersionSnapshot from, TemplateVersionSnapshot to,
        IReadOnlyList<VersionMigrationLookupRemap>? remaps, CancellationToken ct)
    {
        if (remaps is null || remaps.Count == 0)
        {
            return new LookupPrepared(null, [], []);
        }

        var sourceFields = from.HeaderFields.Where(f => !f.IsDeleted).ToDictionary(f => f.Code, StringComparer.Ordinal);
        var targetFields = to.HeaderFields.Where(f => !f.IsDeleted).ToDictionary(f => f.Code, StringComparer.Ordinal);

        var notApplied = new List<LookupRemapReportItem>();
        var applied = new List<(VersionMigrationLookupRemap Remap, HeaderFieldDef Source, HeaderFieldDef Target)>();
        foreach (var remap in remaps)
        {
            var code = remap.Target.Code;
            if (sourceFields.TryGetValue(code, out var src) && targetFields.TryGetValue(code, out var dst)
                && src.LookupRegistryDefId is not null && dst.LookupRegistryDefId is not null
                && src.LookupRegistryDefId != dst.LookupRegistryDefId)
            {
                applied.Add((remap, src, dst));
                continue;
            }

            notApplied.Add(new LookupRemapReportItem(
                remap.Target, Applied: false, MappedValues: 0, UnmappedValues: 0, MappedEntries: 0, UnmappedEntries: 0,
                UnmappedEntryIds: [], UnmappedTruncated: false, TargetNotUsable: []));
        }

        if (applied.Count == 0)
        {
            return new LookupPrepared(new VersionMigrationLookupInput(new Dictionary<int, LookupRemapField>()), [], notApplied);
        }

        var rows = await store
            .ListHeaderLookupValuesAsync(
                projectId, [.. applied.Select(a => a.Source.Id)], VersionMigrationLookupRemaps.MaxHeaderValues, ct)
            .ConfigureAwait(false);
        if (rows.Count > VersionMigrationLookupRemaps.MaxHeaderValues)
        {
            throw VersionMigrationLookupRemaps.Invalid(
                $"Значень шапки в мапованих полях більше за {VersionMigrationLookupRemaps.MaxHeaderValues}.");
        }

        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Проєкт переносу зник під блоком.");
        var windowStart = project.PeriodStart;
        var windowEndExclusive = project.PeriodEnd.AddDays(1);

        var standings = (await registries
                .FindEntryStandingsAsync(
                    [.. applied.SelectMany(a => a.Remap.Mappings).SelectMany(p => new[] { p.From, p.To }).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        // PRMG: знімок груп оновлюється лише при незбігу реєстру (мапи між звичайними довідниками - без резолвера).
        await Permits.Groups.LookupTargetMatcher.EnsureFreshOnMismatchAsync(
            groups,
            applied.SelectMany(a => a.Remap.Mappings.SelectMany(m => new[]
            {
                (a.Source.LookupRegistryDefId, standings.TryGetValue(m.From, out var o) ? o.RegistryDefId : 0),
                (a.Target.LookupRegistryDefId, standings.TryGetValue(m.To, out var n) ? n.RegistryDefId : 0),
            })),
            ct).ConfigureAwait(false);

        var fields = new Dictionary<int, LookupRemapField>();
        foreach (var (remap, src, dst) in applied)
        {
            // ⛔ Старий запис мусить належати ВИХІДНОМУ довіднику поля (будь-якого стану): мапа з чужого довідника
            // не повинна вдавати покриття. Перевіряються всі пари, у тому числі ті, що нічого не покривають.
            foreach (var pair in remap.Mappings)
            {
                if (!standings.TryGetValue(pair.From, out var old)
                    || !Permits.Groups.LookupTargetMatcher.Belongs(groups, null, src.LookupRegistryDefId, old.RegistryDefId))
                {
                    throw VersionMigrationLookupRemaps.Invalid(
                        $"Мапа Lookup поля «{src.Code}»: запис {pair.From} не належить довіднику вихідної версії.");
                }
            }

            var fieldRows = rows.Where(r => r.HeaderFieldDefId == src.Id).ToList();
            var used = fieldRows
                .Where(r => r.EntryId is not null)
                .GroupBy(r => r.EntryId!.Value)
                .ToDictionary(g => g.Key, g => (long)g.Count());
            var mappings = remap.Mappings.ToDictionary(p => p.From, p => p.To);

            var unusable = new Dictionary<long, string>();
            foreach (var entryId in used.Keys.Where(mappings.ContainsKey))
            {
                var newId = mappings[entryId];
                var reason = NotUsableReason(
                    standings.GetValueOrDefault(newId), dst.LookupRegistryDefId!.Value, windowStart, windowEndExclusive, groups);
                if (reason is not null)
                {
                    unusable[newId] = reason;
                }
            }

            fields[src.Id] = new LookupRemapField(
                src.Code, mappings, used, fieldRows.Count(r => r.EntryId is null), unusable);
        }

        return new LookupPrepared(new VersionMigrationLookupInput(fields), rows, notApplied);
    }

    /// <summary>
    /// Чому запис нового довідника не можна покласти в шапку; <c>null</c> — придатний. Те саме правило, що
    /// <c>EnsureLookupEntriesUsableAsync</c> (<c>ECR-HDR-4223</c>): активний, не видалений, чинний бодай один день
    /// вікна проєкту.
    /// </summary>
    private static string? NotUsableReason(
        RegistryEntryStanding? standing, int registryDefId, DateOnly windowStart, DateOnly windowEndExclusive,
        Permits.Groups.ILookupTargetResolver? groups)
        => standing is null ? "missing"
            : !Permits.Groups.LookupTargetMatcher.Belongs(groups, null, registryDefId, standing.RegistryDefId) ? "foreign"
            : standing.IsDeleted ? "deleted"
            : !standing.IsActive ? "inactive"
            : !new Ecr.Domain.ValueObjects.ValidityWindow(standing.ValidFrom, standing.ValidTo)
                .OverlapsSegment(windowStart, windowEndExclusive) ? "notValidInWindow"
            : null;

    /// <summary>Розподіл поданих/затверджених пар «аркуш × період» на перенесені без змін і зачеплені планом.</summary>
    private sealed record LockedClassification(
        int Carried, int Affected, IReadOnlyList<LockedSheetItem> Items, bool Truncated);

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): пари «документ × аркуш ВИХІДНОЇ версії × період якоря», куди пише крок якоря (таблиця
    /// цільової версії → її аркуш → аркуш-джерело за мапою аркушів плану). Подана/затверджена така пара — зачеплена (LP-32).
    /// </summary>
    private static HashSet<(long DocumentId, int SheetDefId, int PeriodKey)> AnchorWrittenSheets(
        VersionMigrationAnchorFill fill, VersionMigrationPlan plan, TemplateVersionSnapshot to)
    {
        var result = new HashSet<(long, int, int)>();
        if (fill.Targets is not { Count: > 0 } targets)
        {
            return result;
        }

        var targetSheetOfTable = to.Sheets.SelectMany(s => s.Tables.Select(t => (t.Id, SheetId: s.Id)))
            .ToDictionary(x => x.Id, x => x.SheetId);
        var sourceSheetsOf = plan.Sheets.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList());
        foreach (var target in targets)
        {
            if (targetSheetOfTable.TryGetValue(target.TableDefId, out var sheet)
                && sourceSheetsOf.TryGetValue(sheet, out var sources))
            {
                foreach (var source in sources)
                {
                    result.Add((target.DocumentId, source, target.PeriodKey));
                }
            }
        }

        return result;
    }

    private static LockedClassification ClassifyLocked(
        VersionMigrationScope scope, VersionMigrationPlan plan, TemplateVersionSnapshot from,
        HashSet<(long DocumentId, int SheetDefId, int PeriodKey)>? anchorWritten = null)
    {
        if (scope.LockedSheets == 0)
        {
            return new LockedClassification(0, 0, [], Truncated: false);
        }

        // Сховище без переліку (обгортка в тесті): кількість відома, а що саме — ні, тож усе вважається зачепленим
        // і перенос відмовляє, як до LP-32.
        if (scope.LockedSheetRefs is null)
        {
            return new LockedClassification(0, scope.LockedSheets, [], Truncated: false);
        }

        var codes = from.Sheets.ToDictionary(s => s.Id, s => s.Code.ToString());
        var touched = plan.TouchedSheets;
        var items = scope.LockedSheetRefs
            .Select(l => new LockedSheetItem(
                l.DocumentId,
                codes.GetValueOrDefault(l.SheetDefId) ?? string.Create(CultureInfo.InvariantCulture, $"#{l.SheetDefId}"),
                l.PeriodKey,
                l.Status,
                Affected: !plan.Sheets.ContainsKey(l.SheetDefId) || touched.Contains(l.SheetDefId)
                          || anchorWritten?.Contains((l.DocumentId, l.SheetDefId, l.PeriodKey)) == true))
            .ToList();

        var affected = items.Count(i => i.Affected);

        // Спершу зачеплені (вони — причина відмови), далі решта; стеля — щоб звіт на тисячах пар не роздувався.
        var listed = items
            .OrderByDescending(i => i.Affected)
            .ThenBy(i => i.SheetCode, StringComparer.Ordinal)
            .ThenBy(i => i.PeriodKey)
            .ThenBy(i => i.DocumentId)
            .Take(MaxLockedItems)
            .ToList();

        return new LockedClassification(items.Count - affected, affected, listed, items.Count > MaxLockedItems);
    }

    /// <summary>✎ V-7: ручні значення (D-461), що зникли б, на поданих/затверджених парах «документ × аркуш × період».</summary>
    /// <param name="Marks">Скільки позначок лежить на таких парах.</param>
    /// <param name="Items">Самі пари (≤ <c>MaxLockedItems</c>), <c>Affected = true</c>.</param>
    internal sealed record LockedOverrideClassification(int Marks, IReadOnlyList<LockedSheetItem> Items);

    /// <summary>
    /// ✎ V-7: які з позначок <paramref name="lost"/> (колонки ВИХІДНОЇ версії) лежать на поданих/затверджених парах
    /// <paramref name="lockedRefs"/>. Копії аркуша зараховуються разом з оригіналом: <see cref="VersionMigrationLockedSheet"/>
    /// не несе <c>SheetCopyId</c> (консервативно, як <see cref="ClassifyLocked"/>).
    /// </summary>
    internal static LockedOverrideClassification LockedOverrideTouches(
        IReadOnlyList<ProjectFormulaOverride> lost, TemplateVersionSnapshot from,
        IReadOnlyList<VersionMigrationLockedSheet>? lockedRefs)
    {
        if (lost.Count == 0 || lockedRefs is not { Count: > 0 })
        {
            return new LockedOverrideClassification(0, []);
        }

        var lockedStatus = lockedRefs
            .GroupBy(l => (l.DocumentId, l.SheetDefId, l.PeriodKey))
            .ToDictionary(g => g.Key, g => g.First().Status);
        var sheetOfTable = from.Sheets
            .SelectMany(s => s.Tables.Select(t => (TableId: t.Id, SheetId: s.Id)))
            .ToDictionary(x => x.TableId, x => x.SheetId);
        var codes = from.Sheets.ToDictionary(s => s.Id, s => s.Code.ToString());

        var marks = 0;
        var pairs = new Dictionary<(long DocumentId, int SheetDefId, int PeriodKey), string>();
        foreach (var mark in lost)
        {
            if (!from.ColumnsById.TryGetValue(mark.Address.ColumnDefId, out var column)
                || !sheetOfTable.TryGetValue(column.TableDefId, out var sheetId))
            {
                continue;
            }

            var key = (mark.DocumentId, sheetId, mark.Address.PeriodKey.Value);
            if (lockedStatus.TryGetValue(key, out var status))
            {
                marks++;
                pairs.TryAdd(key, status);
            }
        }

        var items = pairs
            .Select(p => new LockedSheetItem(
                p.Key.DocumentId,
                codes.GetValueOrDefault(p.Key.SheetDefId)
                    ?? string.Create(CultureInfo.InvariantCulture, $"#{p.Key.SheetDefId}"),
                p.Key.PeriodKey,
                p.Value,
                Affected: true))
            .OrderBy(i => i.SheetCode, StringComparer.Ordinal)
            .ThenBy(i => i.PeriodKey)
            .ThenBy(i => i.DocumentId)
            .Take(MaxLockedItems)
            .ToList();

        return new LockedOverrideClassification(marks, items);
    }

    private async Task<TemplateVersion> RequireTargetAsync(
        int sourceVersionId, int targetVersionId, bool tolerateSame, CancellationToken ct)
    {
        var target = await versions.FindAsync(targetVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                $"Версії шаблону {targetVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = targetVersionId.ToString(CultureInfo.InvariantCulture),
                });

        if (target.Id == sourceVersionId)
        {
            return tolerateSame ? target : throw SameVersion(target.Id);
        }

        var sourceTemplate = await templates.FindTemplateOfVersionAsync(sourceVersionId, ct).ConfigureAwait(false);
        if (sourceTemplate is null || sourceTemplate.Id != target.TemplateId)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Версія {target.Version} належить іншому шаблону: документ переноситься лише між версіями свого шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.migrateOtherTemplate",
                    ["versionId"] = targetVersionId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // Чернетка ще змінюється (перенесені дані опинилися б на структурі,
        // яку завтра переправлять), виведена з обігу — вже не для нових даних.
        if (target.Status != TemplateVersionStatus.Published)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Переносити можна лише на опубліковану версію; версія {target.Version} у стані {target.Status}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.migrateTargetNotPublished",
                    ["version"] = target.Version,
                    ["status"] = target.Status.ToString(),
                });
        }

        return target;
    }

    private static BusinessRuleException SameVersion(int versionId)
        => new(
            ErrorCodes.TemplateInvalid,
            $"Документ уже на версії {versionId}: переносити нема куди.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0422.migrateSameVersion",
                ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
            });

    private static void ThrowIfRefused(
        DocumentVersionMigrationDto dto, IReadOnlyList<string> foreignKeys, IReadOnlyList<string>? renameConflicts = null,
        bool acceptGuardedOverwrite = false, long lookupGuardedValues = 0, int assignmentConflicts = 0)
    {
        // ACC-OW: значення під новою формулою за згодою не «змінюють тлумачення» — у відмові лишається решта.
        var guardedValues = acceptGuardedOverwrite ? dto.GuardedValues - (dto.GuardedOverwritable ?? 0L) : dto.GuardedValues;

        // Частина guarded, що лишилась через мапу Lookup без повного покриття: її називає окрема причина.
        var otherGuardedValues = guardedValues - lookupGuardedValues;

        if (dto.Refusals.Contains("projectArchived") || dto.Refusals.Contains("sheetsLocked")
            || dto.Refusals.Contains("lockedOverridesLost"))
        {
            // Подане чи затверджене стосувалося СТАРОЇ структури, і зачепити його
            // переносом означало б видати чуже затвердження за нове. Спершу
            // повернути аркуші в роботу. (LP-32: незачеплені переносяться без змін.)
            // ✎ V-7: те саме для ручних значень (D-461), які перенос зняв би на поданому/затвердженому аркуші.
            // Пріоритет ключа: архів > структура (sheetsLocked) > ручні значення; `refusals` несе всі причини.
            var messageKey = dto.Refusals.Contains("projectArchived")
                ? "err.ECR-DOC-0409.migrateProjectArchived"
                : dto.Refusals.Contains("sheetsLocked")
                    ? "err.ECR-DOC-0409.migrateSheetsLocked"
                    : "err.ECR-DOC-0409.migrateLockedOverrides";
            throw new ConcurrencyConflictException(
                ErrorCodes.DocumentSubmitted,
                messageKey switch
                {
                    "err.ECR-DOC-0409.migrateProjectArchived" => "Проєкт архівовано: документи архівного проєкту не переносяться.",
                    "err.ECR-DOC-0409.migrateSheetsLocked" =>
                        $"Перенос зачепив би подані або затверджені аркуші: {dto.LockedSheetsAffected}. Спершу поверніть їх у роботу.",
                    _ => "Перенос зняв би ручні значення замість формул на поданих або затверджених аркушах: значень "
                         + $"{dto.LockedOverridesLost}, аркушів {dto.LockedOverrideItems?.Count ?? 0}. Спершу поверніть їх у роботу.",
                },
                new Dictionary<string, object?>
                {
                    ["messageKey"] = messageKey,

                    // ⚠ `lockedSheets` — параметр тексту повідомлення: з LP-32 це кількість ЗАЧЕПЛЕНИХ аркушів
                    // (раніше — усіх поданих); перенесені без змін у відмову не входять.
                    ["lockedSheets"] = (dto.Refusals.Contains("projectArchived") ? dto.LockedSheets : dto.LockedSheetsAffected)
                        .ToString(CultureInfo.InvariantCulture),
                    ["lockedSheetsAffected"] = dto.LockedSheetsAffected.ToString(CultureInfo.InvariantCulture),
                    ["lockedSheetsCarried"] = dto.LockedSheetsCarried.ToString(CultureInfo.InvariantCulture),

                    // ⚠ Лише коди аркушів, періоди й статуси: без імен користувачів і значень.
                    ["lockedSheetItems"] = dto.LockedSheetItems is { Count: > 0 } lockedItems
                        ? lockedItems.Where(i => i.Affected).Take(MaxLockedItems)
                            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.SheetCode}|{i.PeriodKey}|{i.Status}"))
                            .ToList()
                        : null,

                    // ✎ V-7: лише кількість і коди аркушів / періоди / статуси, без значень.
                    ["lockedOverrides"] = dto.LockedOverridesLost?.ToString(CultureInfo.InvariantCulture),
                    ["lockedOverrideSheets"] = (dto.LockedOverrideItems?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                    ["lockedOverrideItems"] = dto.LockedOverrideItems is { Count: > 0 } overrideItems
                        ? overrideItems
                            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.SheetCode}|{i.PeriodKey}|{i.Status}"))
                            .ToList()
                        : null,
                    ["refusals"] = dto.Refusals,
                });
        }

        if (dto.Refusals.Count == 0)
        {
            return;
        }

        // ⛔ ФВ-7.5 + ФВ-7.3: режим — це і є стратегія міграції. Зміна, для
        // якої в обраного режиму стратегії немає, — неповна операція (422), а
        // не конфлікт стану: `ECR-SCHM-0422` саме про це.
        throw new BusinessRuleException(
            ErrorCodes.GuardedChangeWithoutStrategy,
            $"Режим {dto.Mode} не переносить ці зміни: {string.Join(", ", dto.Refusals)}. " +
            $"Втратилося б значень: {dto.LostValues}, змінило б тлумачення: {guardedValues}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = dto.Refusals.Contains("structural")
                    ? "err.ECR-SCHM-0422.migrateStructural"
                    : dto.Refusals.Contains("renameConflict")
                    ? "err.ECR-SCHM-0422.migrateRenameConflict"
                    : dto.Refusals.Contains("lookupValuesNotMapped") && dto.LostValues == 0 && otherGuardedValues == 0
                        ? "err.ECR-SCHM-0422.migrateLookupNotMapped"
                    : dto.Refusals.Contains("lookupLockedDocuments") && dto.LostValues == 0 && otherGuardedValues == 0
                        ? "err.ECR-SCHM-0422.migrateLookupLockedDocuments"
                    : dto.Refusals.Contains("grantsNotMapped") && dto.LostValues == 0 && guardedValues == 0
                        ? "err.ECR-SCHM-0422.migrateGrantsNotMapped"
                        : dto.Refusals.Contains("bindingsNotMapped") && dto.LostValues == 0 && guardedValues == 0
                            ? "err.ECR-SCHM-0422.migrateBindingsNotMapped"
                            : dto.Refusals.Contains("methodologyKeysNotMapped") && dto.LostValues == 0 && guardedValues == 0
                                ? "err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped"
                                : dto.Refusals.Contains("mappingsNotMapped") && dto.LostValues == 0 && guardedValues == 0
                                    ? "err.ECR-SCHM-0422.migrateMappingsNotMapped"
                                    : dto.Refusals.Contains("sheetCopiesNotMapped")
                                        ? "err.ECR-SCHM-0422.migrateSheetCopiesNotMapped"
                                        : dto.Refusals.Contains("assignmentsConflict")
                                            ? "err.ECR-SCHM-0422.migrateAssignmentsConflict"
                                            : dto.Refusals.Contains("nonMonthConflict") && dto.LostValues == 0 && guardedValues == 0
                                                ? "err.ECR-SCHM-0422.migrateNonMonthConflict"
                                                : "err.ECR-SCHM-0422.migrateDataLoss",
                ["lostValues"] = dto.LostValues.ToString(CultureInfo.InvariantCulture),
                ["lockedDocumentCount"] = dto.LockedDocumentCount > 0
                    ? dto.LockedDocumentCount.Value.ToString(CultureInfo.InvariantCulture)
                    : null,
                ["guardedValues"] = guardedValues.ToString(CultureInfo.InvariantCulture),
                ["mode"] = dto.Mode.ToString(),
                ["refusals"] = dto.Refusals,
                ["blockedGrantCount"] = dto.BlockedGrantCount?.ToString(CultureInfo.InvariantCulture),
                ["methodologyKeys"] = foreignKeys.Count > 0 ? foreignKeys : null,
                ["mappingsUnmapped"] = dto.MappingsUnmapped > 0
                    ? dto.MappingsUnmapped.ToString(CultureInfo.InvariantCulture)
                    : null,
                ["mappingColumns"] = dto.MappingsUnmappedKeys is { Count: > 0 } ? dto.MappingsUnmappedKeys : null,
                // ⚠ Лише коди шаблону (шлях → цільовий код); значень і документів тут немає.
                ["renameConflicts"] = renameConflicts is { Count: > 0 } ? renameConflicts : null,
                // ✎ LAND-YEAR L3: немісячні колонки зі значеннями без місячних цілей у річній версії (лише шляхи шаблону).
                ["monthColumnsUnmapped"] = dto.MonthColumnsUnmapped is { Count: > 0 } ? dto.MonthColumnsUnmapped : null,
                // ✎ LAND-YEAR L3 (B-static): адреси немісячних комірок, де місяці не згодні (лише коди шаблону й ключ рядка;
                // значення — у звіті сухого прогону, доступному тому самому адміністратору шаблону).
                ["nonMonthConflicts"] = dto.NonMonthConflicts is { Count: > 0 } conflicts
                    ? conflicts.Select(c => string.Create(
                            CultureInfo.InvariantCulture, $"{c.DocumentId}|{c.SheetCode}.{c.TableCode}.{c.ColumnCode}|{c.RowKey}"))
                        .ToList()
                    : null,
                // D-10-b: лише КІЛЬКІСТЬ передач, що зіткнулися б із наявними (без виконавців і зрізів).
                ["assignmentConflicts"] = assignmentConflicts > 0
                    ? assignmentConflicts.ToString(CultureInfo.InvariantCulture)
                    : null,
            });
    }
}
