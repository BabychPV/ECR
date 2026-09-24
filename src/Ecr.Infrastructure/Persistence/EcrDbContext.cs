using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Entities.Workflow;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Контекст EF Core.</summary>
/// <remarks>
/// ⚠ Кожна таблиця з тригером **зобов'язана** мати
/// <c>.ToTable(t =&gt; t.HasTrigger("..."))</c> у своїй конфігурації. EF Core 7+
/// використовує <c>OUTPUT</c>-клаузу при <c>SaveChanges</c>, і на таблиці з
/// тригером без цього оголошення падає в рантаймі — помилка, яку легко
/// пропустити до першого запису (ТЗ §13.5 п.1).
/// </remarks>
public sealed class EcrDbContext(DbContextOptions<EcrDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    // cfg
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<SheetDef> SheetDefs => Set<SheetDef>();
    public DbSet<TableDef> TableDefs => Set<TableDef>();
    public DbSet<ColumnDef> ColumnDefs => Set<ColumnDef>();
    public DbSet<HeaderFieldDef> HeaderFieldDefs => Set<HeaderFieldDef>();
    public DbSet<RowDef> RowDefs => Set<RowDef>();
    public DbSet<StyleDef> StyleDefs => Set<StyleDef>();
    public DbSet<FormulaDef> FormulaDefs => Set<FormulaDef>();
    public DbSet<FormulaDependency> FormulaDependencies => Set<FormulaDependency>();
    public DbSet<ValidationRule> ValidationRules => Set<ValidationRule>();
    public DbSet<TableRelationDef> TableRelations => Set<TableRelationDef>();
    public DbSet<PeriodAccessRuleDef> PeriodAccessRules => Set<PeriodAccessRuleDef>();
    public DbSet<SheetGroupRule> SheetGroupRules => Set<SheetGroupRule>();
    public DbSet<RegistryDef> RegistryDefs => Set<RegistryDef>();
    public DbSet<RegistryFieldDef> RegistryFieldDefs => Set<RegistryFieldDef>();
    public DbSet<RegistryRuleDef> RegistryRuleDefs => Set<RegistryRuleDef>();
    public DbSet<RegistryDefinitionDraft> RegistryDefinitionDrafts => Set<RegistryDefinitionDraft>();
    public DbSet<CalculationBinding> CalculationBindings => Set<CalculationBinding>();

    // uom
    public DbSet<Dimension> Dimensions => Set<Dimension>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();

    // dic
    public DbSet<RegistryEntry> RegistryEntries => Set<RegistryEntry>();
    public DbSet<RegistryValue> RegistryValues => Set<RegistryValue>();
    public DbSet<RegistryEntryLink> RegistryEntryLinks => Set<RegistryEntryLink>();
    public DbSet<RegistryExternalKey> RegistryExternalKeys => Set<RegistryExternalKey>();

    // doc
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<PeriodPolicy> PeriodPolicies => Set<PeriodPolicy>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentSheet> DocumentSheets => Set<DocumentSheet>();
    public DbSet<TableInstance> TableInstances => Set<TableInstance>();
    public DbSet<TableRow> TableRows => Set<TableRow>();
    public DbSet<CellValue> CellValues => Set<CellValue>();
    public DbSet<DocumentHeaderValue> DocumentHeaderValues => Set<DocumentHeaderValue>();

    // sec
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<ResourceGrant> ResourceGrants => Set<ResourceGrant>();
    public DbSet<PasswordPolicy> PasswordPolicies => Set<PasswordPolicy>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    // wf
    public DbSet<ApprovalRoute> ApprovalRoutes => Set<ApprovalRoute>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ApprovalState> ApprovalStates => Set<ApprovalState>();
    public DbSet<ApprovalEvent> ApprovalEvents => Set<ApprovalEvent>();
    public DbSet<ValidationResult> ValidationResults => Set<ValidationResult>();
    public DbSet<SubmissionSnapshot> SubmissionSnapshots => Set<SubmissionSnapshot>();

    // calc
    public DbSet<Methodology> Methodologies => Set<Methodology>();
    public DbSet<MethodologyVersion> MethodologyVersions => Set<MethodologyVersion>();
    public DbSet<MethodologyFormula> MethodologyFormulas => Set<MethodologyFormula>();
    public DbSet<MethodologyConstant> MethodologyConstants => Set<MethodologyConstant>();

    /// <summary>Тести методології: вхід, очікуваний вихід, допуск (ФВ-13.7).</summary>
    public DbSet<MethodologyTestCaseEntity> MethodologyTestCases => Set<MethodologyTestCaseEntity>();
    public DbSet<MethodologySubstance> MethodologySubstances => Set<MethodologySubstance>();
    public DbSet<MethodologyOutput> MethodologyOutputs => Set<MethodologyOutput>();
    public DbSet<MethodologyRule> MethodologyRules => Set<MethodologyRule>();

    /// <summary>Обов'язкові вхідні колонки — gate перед збереженням клітинки (директива «обов'язкові вхідні колонки методології»).</summary>
    public DbSet<MethodologyRequiredInput> MethodologyRequiredInputs => Set<MethodologyRequiredInput>();

    /// <summary>Чиї формули видно виразам версії через <c>!Name</c> (директива ПК-1 №05, поправка 10).</summary>
    public DbSet<MethodologyImport> MethodologyImports => Set<MethodologyImport>();

    /// <summary>Ребра графа між методологіями: без них порядок перерахунку неповний (`B13` §4.3).</summary>
    public DbSet<MethodologyDependency> MethodologyDependencies => Set<MethodologyDependency>();
    public DbSet<CalculationRun> CalculationRuns => Set<CalculationRun>();
    public DbSet<CalculationResult> CalculationResults => Set<CalculationResult>();
    public DbSet<CalculationInputRow> CalculationInputs => Set<CalculationInputRow>();
    public DbSet<CalculationStep> CalculationSteps => Set<CalculationStep>();

    // rpt
    public DbSet<ReportDef> ReportDefs => Set<ReportDef>();
    public DbSet<ReportVersion> ReportVersions => Set<ReportVersion>();
    public DbSet<ReportSnapshot> ReportSnapshots => Set<ReportSnapshot>();
    public DbSet<ReportRow> ReportRows => Set<ReportRow>();

    // ext
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<SourceEntity> SourceEntities => Set<SourceEntity>();
    public DbSet<EntityFieldMap> EntityFieldMaps => Set<EntityFieldMap>();
    public DbSet<CollectionSchedule> CollectionSchedules => Set<CollectionSchedule>();
    public DbSet<RawDataPoint> RawDataPoints => Set<RawDataPoint>();
    public DbSet<ConsistencyRule> ConsistencyRules => Set<ConsistencyRule>();
    public DbSet<LegacySheetMapping> LegacySheetMappings => Set<LegacySheetMapping>();
    public DbSet<LegacyTableMapping> LegacyTableMappings => Set<LegacyTableMapping>();
    public DbSet<LegacyRowMapping> LegacyRowMappings => Set<LegacyRowMapping>();
    public DbSet<LegacyColumnMapping> LegacyColumnMappings => Set<LegacyColumnMapping>();

    // itg
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();
    public DbSet<CollectionCoverage> CollectionCoverages => Set<CollectionCoverage>();
    public DbSet<ArchiveRun> ArchiveRuns => Set<ArchiveRun>();
    public DbSet<MaintenanceRun> MaintenanceRuns => Set<MaintenanceRun>();

    /// <summary>Черга сповіщень; доставку виконує NotificationJob (P-13).</summary>
    public DbSet<NotificationOutboxItem> NotificationOutbox => Set<NotificationOutboxItem>();
    public DbSet<JobProgress> JobProgresses => Set<JobProgress>();

    /// <summary>Позиції відновлюваних сканувань (див. <see cref="ScanCursor"/>).</summary>
    public DbSet<ScanCursor> ScanCursors => Set<ScanCursor>();

    // Сповіщення, налаштовані в застосунку (BE-32): канали й правила — sys_ecr, журнал доставок — itg.
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    // doc — індекс фільтрів
    public DbSet<DocumentIndexValue> DocumentIndexValues => Set<DocumentIndexValue>();

    /// <summary>
    /// Кільце ключів DataProtection — спільне для всіх інстансів (`MI-01`).
    /// </summary>
    /// <remarks>
    /// ⛔ Реалізація <see cref="IDataProtectionKeyContext"/>, а не «ще одна
    /// таблиця». Без неї ключі лежать у профілі облікового запису процесу:
    /// cookie, видана одним інстансом, для другого — шум (`D-32` не
    /// виконується), а під сервісною обліковкою без завантаженого профілю вони
    /// ефемерні, тобто кожен рестарт служби розлогінює всіх.
    ///
    /// ⚠ Форму таблиці диктує пакет
    /// <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c> — ми її
    /// не визначаємо. На відміну від <c>dbo.Cache</c>, вона все ж кладеться в
    /// контрактну схему <c>sec</c>: сюди йдуть ключі шифрування сесії, і
    /// таблиця, на яку DBA видає <c>DENY</c> (див. <c>02a-db-schema.md</c> §11),
    /// мусить бути названа в контракті схеми.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcrDbContext).Assembly);

        // ⚠ Конфігурація тут, а не в `Configurations/`: сутність чужа (її
        // оголошує пакет DataProtection), і єдине, що ми про неї вирішуємо, —
        // де вона лежить. Форму колонок лишаємо за пакетом: `Xml` несе
        // серіалізований елемент кільця ключів, і будь-яка наша межа довжини
        // була б вигаданою. Без цього рядка EF кладе таблицю конвенцією в
        // `dbo.DataProtectionKeys`, і сторож
        // `Міграція_не_створює_таблиць_поза_контрактними_схемами` червоніє —
        // справедливо: таблицю, на яку DBA видає `DENY`, контракт схеми має
        // знати поіменно.
        modelBuilder.Entity<DataProtectionKey>().ToTable("DataProtectionKey", "sec");

        // ⛔ Сім сутностей, у яких розбіжність зі схемою ще не вирішена
        // (`Q-027` для шести, `Q-042` для `RoleAssignment`), свідомо вилучені
        // з моделі.
        //
        // Без цього EF відображає їх КОНВЕНЦІЄЮ: множинне ім'я, схема `dbo` —
        // і міграція створює сім таблиць, яких у `02a-db-schema.md` немає.
        // Це найгірший з можливих станів: у базі з'являється те, чого контракт
        // не описує, а `SchemaValidator` цього не бачить, бо звіряє список
        // міграцій, а не форму схеми.
        //
        // Прив'язати їх до контрактних таблиць зараз теж не можна: у схемі є
        // колонки `NOT NULL`, яких у сутностях немає взагалі
        // (`wf.ApprovalRoute.TemplateVersionId`, `dic.RegistryEntry.Ordinal`),
        // і будь-яке значення для них було б вигаданим.
        //
        // Кожна повернулася в модель на своєму етапі разом із конфігурацією.
        // `wf.ApprovalRoute`, `wf.ApprovalStep` і `sec.RoleAssignment` — на
        // Етапі 3 разом із бракуючими полями (`TemplateVersionId`,
        // `IsOptional`, `ValidFrom`/`ValidTo`); чотири `dic.*` — на Етапі 4
        // (`Ordinal`, `LinkKind`, `DataSourceId`, `ValueNumeric`).
        // **Список порожній.** Тест
        // `Міграція_не_створює_таблиць_поза_контрактними_схемами` стежить, щоб
        // він не наповнювався мовчки знову.

        // ⚠ Каскадне видалення вимкнене скрізь за замовчуванням: у системі
        // діє soft delete (ФВ-7.6), бо на кожен запис хтось посилається —
        // комірки, аудит, формули. Каскад тут означав би тихе зникнення
        // історії разом із довідником.
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        // `V-13`: моменти часу читаються з Kind=Utc і йдуть у JSON із «Z»;
        // календарні дати (`ValueDate`) — ні. Класифікація й межа — у
        // `UtcDateTimeColumns`.
        UtcDateTimeColumns.Apply(modelBuilder);

        // Id для партиційованих таблиць беруться з SEQUENCE, а не з IDENTITY:
        // значення потрібне ДО вставки, щоб завантажити TableRow і CellValue
        // одним проходом SqlBulkCopy (B02 §2.3). CACHE 1000 — компроміс між
        // круглими втратами при перезапуску і зверненнями до системних таблиць.
        //
        // Оголошуються лише для SQL Server. Послідовність тут — фізичний
        // об'єкт SQL Server, який читається через sp_sequence_get_range, і в
        // провайдера без послідовностей вона не має ні реалізації, ні сенсу:
        // EF просто падає на CREATE SEQUENCE.
        //
        // ⚠ Другого провайдера в дереві НЕМАЄ. Тут стояло «SQLite, на якому
        // йдуть не-Integration тести» — і це була неправда: `UseSqlite` не мав
        // у `tests/` жодного місця виклику, а посилання на пакет висіло в
        // `Ecr.TestKit.csproj` мертвим. Обидва прибрано. Умова лишається
        // навмисно: вона описує ФІЗИЧНУ межу об'єкта, а не режим тестів.
        if (Database.IsSqlServer())
        {
            modelBuilder.HasSequence<long>("TableInstanceSeq", "doc").StartsAt(1).IncrementsBy(1);
            modelBuilder.HasSequence<long>("TableRowSeq", "doc").StartsAt(1).IncrementsBy(1);

            // ⛔ Послідовності результатів розрахунку НЕ БУЛО, хоча
            // `CalculationResultStore.ReserveResultIdRangeAsync` читає її
            // поіменно через `sp_sequence_get_range`. Відсутність не мала
            // симптому рівно тому, що результат методології не записувався
            // ніколи: прив'язку `cfg.CalculationBinding` не створювало ніщо
            // (директива №09, `W6` §3), тож до цього рядка коду не доходив
            // жоден прогін. Перша ж заведена з нуля методологія падала
            // «Invalid object name 'calc.CalculationResultSeq'» — уже після
            // того, як усі числа пораховані.
            modelBuilder.HasSequence<long>("CalculationResultSeq", "calc").StartsAt(1).IncrementsBy(1);
        }
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // decimal(34,16) скрізь: точність задається один раз, а не забувається
        // в кожній новій колонці. float і double не використовуються ніде —
        // порядок додавання змінює результат, і звірка з еталоном стає
        // неможливою (D-30).
        //
        // ⚠ Конвенція переводиться ПІСЛЯ міграцій типів і навмисно останньою:
        // усі тринадцять стовпців вимірюваних величин оголошені явно, тож ця
        // зміна не мусить давати жодної нової міграції — і
        // `has-pending-model-changes` після неї це й доводить. Зроби її першою
        // — і кожна наступна міграція несла б фасети, яких ніхто не замовляв.
        //
        // ⚠ 34, а не 28 (`D-148`, пряме рішення людини 2026-09-21). Precision
        // 28 при масштабі 16 лишає 12 цілих розрядів, а множник
        // `MWh → 3 600 000 000` із каталогу одиниць виводить уже 278 МВт·год за
        // цю межу. Ціна названа й не нульова, на відміну від переходу 10 → 16:
        // ширина `decimal` залежить ЛИШЕ від precision, і 28 — межа категорії
        // (20–28 → 13 Б, 29–38 → 17 Б), тобто +4 Б на рядок, +12.4 % розміру
        // `doc.CellValue`.
        //
        // ⚠ Стеля не тут: `System.Decimal` несе 29 значущих цифр, тож при
        // масштабі 16 із бази читається щонайбільше 13 цілих розрядів, а не 18
        // (виміряно; `CellValueScale16Tests` тримає це твердженням).
        configurationBuilder.Properties<decimal>().HavePrecision(34, 16);

        // Локалізований текст — одна колонка nvarchar(max) із JSON (ФВ-2.2).
        // Конвенція, а не налаштування на кожну властивість: інакше перша ж
        // забута сутність ламає побудову моделі.
        configurationBuilder.Properties<Domain.ValueObjects.LocalizedText>()
            .HaveConversion<Configurations.LocalizedTextConverter, Configurations.LocalizedTextComparer>()
            .HaveColumnType("nvarchar(max)");

        // datetime2(3) — мілісекунди. datetime2(7) коштує зайвих байт на
        // ~108 млн рядків і не дає нічого: точніше за мілісекунду тут ніщо
        // не вимірюється.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(3)");

        // ⚠ Індекси під зовнішні ключі EF більше не вигадує.
        // Конвенція створила IX_CellValue_TableDefId_ColumnDefId на таблиці в
        // ~108 млн рядків — індекс, якого в 02a-db-schema.md немає і який
        // коштував би гігабайти й уповільнював кожну вставку. Схема — єдине
        // джерело істини про індекси (08-workflow §7): кожен оголошений явно.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }
}
