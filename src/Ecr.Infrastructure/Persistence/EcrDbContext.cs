using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Entities.Workflow;
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
public sealed class EcrDbContext(DbContextOptions<EcrDbContext> options) : DbContext(options)
{
    // cfg
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<SheetDef> SheetDefs => Set<SheetDef>();
    public DbSet<TableDef> TableDefs => Set<TableDef>();
    public DbSet<ColumnDef> ColumnDefs => Set<ColumnDef>();
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

    // sec
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<ResourceGrant> ResourceGrants => Set<ResourceGrant>();
    public DbSet<PasswordPolicy> PasswordPolicies => Set<PasswordPolicy>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    // wf
    public DbSet<ApprovalRoute> ApprovalRoutes => Set<ApprovalRoute>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ApprovalState> ApprovalStates => Set<ApprovalState>();
    public DbSet<ValidationResult> ValidationResults => Set<ValidationResult>();
    public DbSet<SubmissionSnapshot> SubmissionSnapshots => Set<SubmissionSnapshot>();

    // calc
    public DbSet<Methodology> Methodologies => Set<Methodology>();
    public DbSet<MethodologyVersion> MethodologyVersions => Set<MethodologyVersion>();
    public DbSet<MethodologyFormula> MethodologyFormulas => Set<MethodologyFormula>();
    public DbSet<MethodologyConstant> MethodologyConstants => Set<MethodologyConstant>();
    public DbSet<MethodologySubstance> MethodologySubstances => Set<MethodologySubstance>();
    public DbSet<MethodologyOutput> MethodologyOutputs => Set<MethodologyOutput>();
    public DbSet<MethodologyRule> MethodologyRules => Set<MethodologyRule>();
    public DbSet<ScriptVersion> ScriptVersions => Set<ScriptVersion>();
    public DbSet<CalculationRun> CalculationRuns => Set<CalculationRun>();
    public DbSet<CalculationResult> CalculationResults => Set<CalculationResult>();
    public DbSet<CalculationInputRow> CalculationInputs => Set<CalculationInputRow>();
    public DbSet<CalculationStep> CalculationSteps => Set<CalculationStep>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcrDbContext).Assembly);

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

        // Id для партиційованих таблиць беруться з SEQUENCE, а не з IDENTITY:
        // значення потрібне ДО вставки, щоб завантажити TableRow і CellValue
        // одним проходом SqlBulkCopy (B02 §2.3). CACHE 1000 — компроміс між
        // круглими втратами при перезапуску і зверненнями до системних таблиць.
        //
        // Оголошуються лише для SQL Server. Це не умовна модель «під тести»:
        // послідовність тут — фізичний об'єкт SQL Server, який читається через
        // sp_sequence_get_range, і в провайдера без послідовностей (SQLite,
        // на якому йдуть не-Integration тести) вона не має ні реалізації, ні
        // сенсу — EF просто падає на CREATE SEQUENCE.
        if (Database.IsSqlServer())
        {
            modelBuilder.HasSequence<long>("TableInstanceSeq", "doc").StartsAt(1).IncrementsBy(1);
            modelBuilder.HasSequence<long>("TableRowSeq", "doc").StartsAt(1).IncrementsBy(1);
        }
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // decimal(28,10) скрізь: точність задається один раз, а не забувається
        // в кожній новій колонці. float і double не використовуються ніде —
        // порядок додавання змінює результат, і звірка з еталоном стає
        // неможливою (D-30).
        configurationBuilder.Properties<decimal>().HavePrecision(28, 10);

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
