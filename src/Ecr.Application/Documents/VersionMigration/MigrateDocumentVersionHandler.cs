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
    int? LockedDocumentCount = null);

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
public sealed record DocumentVersionMigrationTargetsDto(
    int ProjectId,
    int CurrentVersionId,
    string CurrentVersion,
    IReadOnlyList<TemplateVersionSummary> Targets);

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
    Microsoft.Extensions.Logging.ILogger<MigrateDocumentVersionHandler> log)
{
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
                TargetOf(request.ProjectId),
                new MigrateDocumentVersionTask(
                    documentId, targetVersionId, mode.ToString(), actor,
                    renames is { Count: > 0 } ? renames : null,
                    acceptGuardedOverwrite,
                    lookupRemaps is { Count: > 0 } ? lookupRemaps : null,
                    allowLockedDocuments),
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

    /// <summary>Що з'ясовують швидкі перевірки до плану: профіль, проєкт, ціль.</summary>
    private sealed record Resolved(
        AccessProfile Profile, int ProjectId, int SourceVersionId, TemplateVersion Target, bool Archived);

    private async Task<Resolved> ResolveAsync(long documentId, int targetVersionId, bool tolerateSame, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        var target = await RequireTargetAsync(project.TemplateVersionId, targetVersionId, tolerateSame, ct).ConfigureAwait(false);
        var archived = project.Status == ProjectStatus.Archived || project.IsArchiving;

        return new Resolved(profile, projectId, project.TemplateVersionId, target, archived);
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
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ План перераховується ПІД блоком рядка проєкту: між сухим
            // прогоном і підтвердженням у колонку, яку нова версія прибирає,
            // могли ввести значення. Перевірка на знімку з минулого запиту
            // пропустила б саме ту втрату, від якої вона захищає.
            var current = await store.LockProjectVersionAsync(projectId, innerCt).ConfigureAwait(false)
                          ?? throw DocumentVisibility.NotFound(documentId);
            if (current == target.Id)
            {
                // Фонова задача: інший перенос на ту саму ціль встиг раніше - це не помилка (див. RunQueuedAsync).
                if (onProgress is not null)
                {
                    return;
                }

                throw SameVersion(target.Id);
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

            var planned = await PlanAsync(
                documentId, projectId, current, target, mode, archived, dryRun: false, renames,
                acceptGuardedOverwrite, lookupRemaps, allowLockedDocuments, innerCt).ConfigureAwait(false);
            var (dto, plan, foreignKeys, extended) = (planned.Dto, planned.Plan, planned.ForeignKeys, planned.Extended);
            ThrowIfRefused(dto, foreignKeys, plan.RenameConflicts, acceptGuardedOverwrite, plan.LookupGuardedValues);

            grantedUsers = await store.ListUsersWithGrantsAsync(plan, MaxInvalidatedUsers, innerCt).ConfigureAwait(false);

            VersionMigrationStepReporter? onStep = null;
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

            await store.ApplyAsync(projectId, target.Id, plan, onStep, innerCt).ConfigureAwait(false);
            formulaTables = plan.FormulaChanges.Count;

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
                    }),
                    profile.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Профіль у кеші не бачить зміни грантів, яка не рухає відбиток груп
        // (прямі ролі): без явного скидання закрита колонка лишалася б доступною
        // до TTL (60 хв). Скидаються ВСІ записи користувача (будь-який відбиток
        // груп); збій або переповнення переліку — скидання всього кешу, не 500.
        GrantProfileInvalidation.Run(profileCache, log, grantedUsers);

        // ⛔ LP-41: нова/змінена формула без перерахунку лишається порожньою до явної дії людини (інкрементний
        // шлях не бере формул поза зміненими комірками). Один coalesced перерахунок проєкту — лише коли план
        // щось додав чи змінив і лише ПІСЛЯ коміту; сухий прогін і перенос без змін формул нічого не ставлять.
        if (result is not null && formulaTables > 0)
        {
            await MigrationRecalculation
                .EnqueueAsync(jobs, log, clock.UtcNow, projectId, formulaTables, ct)
                .ConfigureAwait(false);
        }

        return result!;
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
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        var current = await versions.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
        var all = await templates
            .ListVersionsForTemplatesAsync([current.TemplateId], MaxTargets, ct)
            .ConfigureAwait(false);

        var targets = all.GetValueOrDefault(current.TemplateId, [])
            .Where(v => v.Id != current.Id && v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.Id)
            .ToList();

        return new DocumentVersionMigrationTargetsDto(projectId, current.Id, current.Version, targets);
    }

    /// <summary>План переносу разом зі звітом, мапінгами й значеннями шапки, які читав аудит мап Lookup.</summary>
    private sealed record PlannedMigration(
        DocumentVersionMigrationDto Dto,
        VersionMigrationPlan Plan,
        IReadOnlyList<string> ForeignKeys,
        ExtendedMappingRemap Extended,
        IReadOnlyList<HeaderLookupValueRow> LookupRows);

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
        var scope = await store.ReadScopeAsync(projectId, ct).ConfigureAwait(false);

        // Без мап нових читань немає: шлях і кількість запитів як до мап Lookup.
        var lookup = await PrepareLookupAsync(projectId, from, to, lookupRemaps, ct).ConfigureAwait(false);

        var plan = VersionMigrationPlanner.Plan(from, to, scope.Cells, scope.HeaderValues, renames, lookup.Input);

        // ACC-OW: прапор знімає guardedWithData лише для нової комірочної формули на комірці зі значенням.
        var refusals = plan.RefusalsFor(mode, acceptGuardedOverwrite).ToList();

        // ⛔ LP-32 (locked-safe): подані/затверджені аркуші самі по собі перенос не блокують. Відмова — лише коли план
        // ЗАЧІПАЄ такий аркуш (див. `VersionMigrationPlan.TouchedSheets`): подане стосується СТАРОЇ структури, і
        // видати його за затвердження нової не можна. Решта лишається як є: `WorkflowSql` перемапує стани й журнал на
        // нові `SheetDefId`, значення переїжджають за кодами, перерахунок Submitted/Approved не пише.
        // ⚠ Аркуш, якого немає серед живих вихідної версії (не потрапив у `plan.Sheets`), `WorkflowSql` ВИДАЛИВ би
        // разом зі станом, тому рахується зачепленим.
        var locked = ClassifyLocked(scope, plan, from);
        if (locked.Affected > 0)
        {
            refusals.Add("sheetsLocked");
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
        var mappedColumns = plan.Columns.Select(c => c.SourceColumnDefId).ToHashSet();
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

        // ⛔ D-13: активна прив'язка методології до колонки вихідної версії, якої нема в цільовій, після
        // переносу не рахувала б нічого — колонка лишалася б порожньою без помилки. Блокуємо, доки
        // конфігуратор не перенесе прив'язку (клон версії тепер копіює їх сам) або не вимкне її.
        var columnMap = plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId);
        var unmappedBindings = await store.CountUnmappedBindingsAsync(sourceVersionId, columnMap, ct)
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
            LockedDocumentCount: lockedDocumentCount);

        return new PlannedMigration(
            dto, plan, foreignKeys, extendedPlan.Remap with { CollectionMappingIds = collectionPlan.RemapIds }, lookup.Rows);
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

        var fields = new Dictionary<int, LookupRemapField>();
        foreach (var (remap, src, dst) in applied)
        {
            // ⛔ Старий запис мусить належати ВИХІДНОМУ довіднику поля (будь-якого стану): мапа з чужого довідника
            // не повинна вдавати покриття. Перевіряються всі пари, у тому числі ті, що нічого не покривають.
            foreach (var pair in remap.Mappings)
            {
                if (!standings.TryGetValue(pair.From, out var old) || old.RegistryDefId != src.LookupRegistryDefId)
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
                    standings.GetValueOrDefault(newId), dst.LookupRegistryDefId!.Value, windowStart, windowEndExclusive);
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
        RegistryEntryStanding? standing, int registryDefId, DateOnly windowStart, DateOnly windowEndExclusive)
        => standing is null ? "missing"
            : standing.RegistryDefId != registryDefId ? "foreign"
            : standing.IsDeleted ? "deleted"
            : !standing.IsActive ? "inactive"
            : !new Ecr.Domain.ValueObjects.ValidityWindow(standing.ValidFrom, standing.ValidTo)
                .OverlapsSegment(windowStart, windowEndExclusive) ? "notValidInWindow"
            : null;

    /// <summary>Розподіл поданих/затверджених пар «аркуш × період» на перенесені без змін і зачеплені планом.</summary>
    private sealed record LockedClassification(
        int Carried, int Affected, IReadOnlyList<LockedSheetItem> Items, bool Truncated);

    private static LockedClassification ClassifyLocked(
        VersionMigrationScope scope, VersionMigrationPlan plan, TemplateVersionSnapshot from)
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
                Affected: !plan.Sheets.ContainsKey(l.SheetDefId) || touched.Contains(l.SheetDefId)))
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
        bool acceptGuardedOverwrite = false, long lookupGuardedValues = 0)
    {
        // ACC-OW: значення під новою формулою за згодою не «змінюють тлумачення» — у відмові лишається решта.
        var guardedValues = acceptGuardedOverwrite ? dto.GuardedValues - (dto.GuardedOverwritable ?? 0L) : dto.GuardedValues;

        // Частина guarded, що лишилась через мапу Lookup без повного покриття: її називає окрема причина.
        var otherGuardedValues = guardedValues - lookupGuardedValues;

        if (dto.Refusals.Contains("projectArchived") || dto.Refusals.Contains("sheetsLocked"))
        {
            // Подане чи затверджене стосувалося СТАРОЇ структури, і зачепити його
            // переносом означало б видати чуже затвердження за нове. Спершу
            // повернути аркуші в роботу. (LP-32: незачеплені переносяться без змін.)
            throw new ConcurrencyConflictException(
                ErrorCodes.DocumentSubmitted,
                dto.Refusals.Contains("projectArchived")
                    ? "Проєкт архівовано: документи архівного проєкту не переносяться."
                    : $"Перенос зачепив би подані або затверджені аркуші: {dto.LockedSheetsAffected}. Спершу поверніть їх у роботу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = dto.Refusals.Contains("projectArchived")
                        ? "err.ECR-DOC-0409.migrateProjectArchived"
                        : "err.ECR-DOC-0409.migrateSheetsLocked",

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
            });
    }
}
