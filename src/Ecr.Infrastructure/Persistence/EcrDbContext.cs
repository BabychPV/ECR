using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Entities.Workflow;
using Microsoft.EntityFrameworkCore;

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

    // wf
    public DbSet<ApprovalRoute> ApprovalRoutes => Set<ApprovalRoute>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ApprovalState> ApprovalStates => Set<ApprovalState>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => throw new NotImplementedException(
            "TODO: modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcrDbContext).Assembly). " +
            "Далі — глобальні конвенції: усі decimal без явної точності → (28,10); " +
            "усі DateTime → datetime2(3); заборонити каскадне видалення за замовчуванням " +
            "(soft delete скрізь, ФВ-7.6).");

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
        => throw new NotImplementedException(
            "TODO: builder.Properties<decimal>().HavePrecision(28, 10); " +
            "builder.Properties<DateTime>().HaveColumnType(\"datetime2(3)\"). " +
            "float/double не використовуються ніде — якщо з'явилися, це помилка моделі (D-30).");
}
