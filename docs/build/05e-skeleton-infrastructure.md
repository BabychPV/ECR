# 05e — Скелет: `Ecr.Infrastructure`

> Частина [`05-skeleton.md`](05-skeleton.md).
> Схема БД — [`02a-db-schema.md`](02a-db-schema.md).
>
> Тут живуть **усі** технологічні деталі: EF Core, `SqlBulkCopy`, партиції,
> кеш, планувальник. Бізнес-правил тут немає — вони в `Ecr.Domain` і
> `Ecr.Application`.

---

## 1. Контекст даних

### `src/Ecr.Infrastructure/Persistence/EcrDbContext.cs`
MODULE: infrastructure-persistence | STAGE: 1
CONTRACT: 02a-db-schema.md
SCOPE: `DbSet`'и і застосування конфігурацій.
NOT IN SCOPE: бізнес-логіка, `SaveChanges`-хуки з правилами.

```csharp
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
```

---

### `src/Ecr.Infrastructure/Persistence/EcrDbContextFactory.cs`
MODULE: infrastructure-persistence | STAGE: 1

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Фабрика для <c>dotnet ef migrations</c>. Потрібна, бо інструмент не може
/// підняти повний host застосунку.
/// </summary>
public sealed class EcrDbContextFactory : IDesignTimeDbContextFactory<EcrDbContext>
{
    /// <inheritdoc />
    public EcrDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ECR_ConnectionStrings__Ecr")
                         ?? "Server=localhost;Database=Ecr;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options;

        return new EcrDbContext(options);
    }
}
```

---

## 2. Конфігурації сутностей

### `src/Ecr.Infrastructure/Persistence/Configurations/CellValueConfiguration.cs`
MODULE: infrastructure-persistence | STAGE: 1
CONTRACT: 02a-db-schema.md#doc
SCOPE: найважливіша конфігурація в системі — 108 млн рядків на рік.

```csharp
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="CellValue"/>.
/// </summary>
/// <remarks>
/// Ключ складений і **починається з партиційного стовпця**: без цього індекси
/// не вирівняні, і партиційні операції неможливі. Сурогатного <c>Id</c> немає
/// навмисно — він коштував би ~0.9 ГБ/рік і не давав би нічого (R-A1).
/// </remarks>
public sealed class CellValueConfiguration : IEntityTypeConfiguration<CellValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CellValue> builder)
        => throw new NotImplementedException(
            "TODO:\n" +
            "builder.ToTable(\"CellValue\", \"doc\");\n" +
            "builder.HasKey(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId });\n" +
            "Property(PeriodKeyValue).HasColumnName(\"PeriodKey\");\n" +
            "Property(ValueString).HasMaxLength(1000);\n" +
            "Property(ValueNumeric).HasPrecision(28, 10);\n" +
            "Property(ValueDate).HasColumnType(\"datetime2(3)\");\n" +
            "FK на TableRow — СКЛАДЕНИЙ (PeriodKey, TableRowId) → doc.TableRow (PeriodKey, Id);\n" +
            "FK на ColumnDef — СКЛАДЕНИЙ (TableDefId, ColumnDefId) → cfg.ColumnDef (TableDefId, Id):\n" +
            "  для цього на ColumnDef має бути HasAlternateKey(x => new { x.TableDefId, x.Id }),\n" +
            "  інакше EF не побудує зв'язок (ТЗ §13.5 п.3);\n" +
            "FK ValueRegistryEntryId → dic.RegistryEntry, ValueUnitId → uom.Unit;\n" +
            "жодного некластерного індексу: усі альтернативні доступи — через doc.DocumentIndexValue.");
}
```

---

### `src/Ecr.Infrastructure/Persistence/Configurations/TemplateVersionConfiguration.cs`
MODULE: infrastructure-persistence | STAGE: 1

```csharp
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація версії шаблону.</summary>
public sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
        => throw new NotImplementedException(
            "TODO: ToTable(\"TemplateVersion\", \"cfg\"); ключ Id; UQ (TemplateId, Version); " +
            "Version HasMaxLength(20). " +
            "⚠ Таблиці cfg.ColumnDef, cfg.RowDef, cfg.FormulaDef мають тригери незмінності, тому " +
            "в ЇХНІХ конфігураціях обов'язково .ToTable(t => t.HasTrigger(\"TR_...\")) — інакше " +
            "SaveChanges падає в рантаймі (ТЗ §13.5 п.1).");
}
```

> **Решта конфігурацій** — по одному файлу на агрегат, за тим самим зразком:
> `TemplateConfiguration`, `SheetDefConfiguration`, `TableDefConfiguration`,
> `ColumnDefConfiguration` (з `HasAlternateKey` і `HasTrigger`),
> `RowDefConfiguration` (з `HasTrigger`), `StyleDefConfiguration`,
> `FormulaDefConfiguration` (з `HasTrigger`), `FormulaDependencyConfiguration`,
> `ValidationRuleConfiguration`, `TableRelationConfiguration`,
> `PeriodAccessRuleConfiguration`, `SheetGroupRuleConfiguration`,
> `RegistryDefConfiguration`, `RegistryFieldDefConfiguration`,
> `CalculationBindingConfiguration`, `DimensionConfiguration`,
> `UnitConfiguration`, `UnitConversionConfiguration`,
> `RegistryEntryConfiguration`, `RegistryValueConfiguration`,
> `RegistryEntryLinkConfiguration`, `RegistryExternalKeyConfiguration`,
> `ProjectConfiguration`, `PeriodPolicyConfiguration`, `PeriodConfiguration`,
> `DocumentConfiguration`, `DocumentSheetConfiguration`,
> `TableInstanceConfiguration`, `TableRowConfiguration`, `UserConfiguration`,
> `RoleConfiguration`, `PermissionConfiguration`,
> `RoleAssignmentConfiguration`, `ResourceGrantConfiguration`,
> `PasswordPolicyConfiguration`, `ApprovalRouteConfiguration`,
> `ApprovalStepConfiguration`, `ApprovalStateConfiguration`.

---

## 3. Сховище комірок

### `src/Ecr.Infrastructure/Persistence/NormalizedCellStore.cs`
MODULE: infrastructure-persistence | STAGE: 1
CONTRACT: 02-contracts.md#ports
SCOPE: базова реалізація `ICellStore`.
NOT IN SCOPE: гібридна модель — окрема реалізація за результатом гейта.

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Нормалізоване сховище комірок — **базова модель** (D-21).
/// </summary>
/// <remarks>
/// Бюджет: <c>ReadSliceAsync</c> — p95 &lt; 600 мс на 500×60,
/// <c>ApplyAsync</c> — p95 &lt; 150 мс на 100 комірок (tz/08 §8.2).
/// Ці числа і є критерієм гейта Етапу 0: якщо не проходить після індексів і
/// стиснення — вибірково по таблицях вмикається гібрид, а не глобально.
/// </remarks>
public sealed class NormalizedCellStore(EcrDbContext db, BulkCellLoader bulk) : ICellStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: ОДИН запит із AsNoTracking, проєкцією в CellRecord і фільтром по " +
            "(PeriodKey, TableRowId IN ...) — partition elimination має бути видно в плані. " +
            "Порожні комірки не існують у БД і не повертаються (ФВ-3.8). " +
            "Заборонено: Include по графу, ToList() без Take, завантаження сутностей замість проєкції.");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: згрупувати адреси за PeriodKey і читати пакетно — по одному запиту на партицію, " +
            "не по запиту на комірку.");

    /// <inheritdoc />
    public Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: одна транзакція:\n" +
            "1) DELETE для changes.Deletes через ExecuteDeleteAsync (не завантажуючи сутності);\n" +
            "2) upsert для changes.Upserts — MERGE або ExecuteUpdate + вставка нових;\n" +
            "3) UPDATE doc.TableRow SET ModifiedAt = @now WHERE ... для TouchedRowIds — " +
            "   ОБОВ'ЯЗКОВО: без цього RowVersion не піднімається і оптимістичне блокування " +
            "   тихо не працює (B04 §2.4);\n" +
            "Транзакція має бути КОРОТКОЮ: під RCSI довга транзакція роздуває version store.");

    /// <inheritdoc />
    public Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати bulk.LoadAsync (SqlBulkCopy).");
}
```

---

### `src/Ecr.Infrastructure/Persistence/BulkCellLoader.cs`
MODULE: infrastructure-persistence | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Масове завантаження через <see cref="SqlBulkCopy"/> (D-05).
/// </summary>
/// <remarks>
/// <c>Id</c> для <c>TableRow</c> береться з <c>SEQUENCE</c>, а не
/// <c>IDENTITY</c>: значення потрібні **до** вставки, щоб завантажити рядки і
/// комірки одним проходом. З <c>IDENTITY</c> довелося б робити два кроки з
/// <c>OUTPUT</c>, а <c>OUTPUT</c> конфліктує з тригерами (B02 §2.3).
/// </remarks>
public sealed class BulkCellLoader(string connectionString, int batchSize)
{
    /// <summary>Завантажує комірки.</summary>
    public Task LoadAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: SqlBulkCopy з BatchSize = batchSize, SqlBulkCopyOptions.TableLock, " +
            "DestinationTableName = \"doc.CellValue\"; колонки мапити явно за іменами. " +
            "Дані подавати через IDataReader-обгортку, а не DataTable: 108 млн рядків " +
            "у DataTable не поміщаються в пам'ять.");

    /// <summary>Резервує діапазон ідентифікаторів із послідовності.</summary>
    public Task<long> ReserveIdsAsync(string sequenceName, int count, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: sp_sequence_get_range — один виклик на весь батч, не на рядок.");
}
```

---

### `src/Ecr.Infrastructure/Persistence/SeedRunner.cs`
MODULE: infrastructure-persistence | STAGE: 1
CONTRACT: 02a-db-schema.md#seed

```csharp
namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Ідемпотентний seed. Без нього застосунок не стартує: немає ані мов, ані
/// прав, ані базових одиниць.
/// </summary>
public sealed class SeedRunner(EcrDbContext db)
{
    /// <summary>Виконує seed. Повторний запуск не створює дублікатів.</summary>
    public Task RunAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: виконати MERGE-скрипти з 02a-db-schema.md#seed у порядку:\n" +
            "1) sys_ecr.Language; 2) sec.Permission (повний каталог); 3) sec.Role (7 вбудованих);\n" +
            "4) sec.PasswordPolicy; 5) uom.Dimension (11); 6) uom.Unit базові; 7) uom.Unit похідні;\n" +
            "8) оновити Dimension.BaseUnitId; 9) doc.PeriodPolicy 'ECR-Standard'.\n" +
            "⚠ Небезпечні права (Calculation.EditScript/Publish, Security.*, Integration.Manage, " +
            "System.RunJob) у вбудовані ролі НЕ додавати: вони видаються іменованим особам " +
            "окремо (ФВ-6.12). Порожні за ними ролі — це навмисно, а не пропуск.\n" +
            "⚠ FactorToBase наявних одиниць змінювати заборонено: на них спираються фікстури.");
}
```

---

## 4. Кеш

### `src/Ecr.Infrastructure/Caching/MetadataCache.cs`
MODULE: infrastructure-caching | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш метаданих шаблону.
/// </summary>
/// <remarks>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію **непотрібною**: презентаційна
/// правка створює новий ключ, а не псує старий. Це прибирає когерентність кешу
/// між інстансами як клас проблеми — і саме тому ≥2 інстанси тут дешеві (D-16).
/// </remarks>
public sealed class MetadataCache(IMemoryCache memory, EcrDbContext db) : IMetadataCache
{
    /// <inheritdoc />
    public Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прочитати PresentationRevision одним легким запитом; ключ = $\"v{id}:r{rev}\"; " +
            "при промаху — завантажити повну структуру ОДНИМ набором запитів (аркуші, таблиці, " +
            "колонки, рядки, формули, стилі), побудувати індекси ColumnsById і RowsByKey, " +
            "покласти в кеш без абсолютного терміну. " +
            "Не використовувати IDistributedCache: знімок великий, а серіалізація дорожча за перечитування.");

    /// <inheritdoc />
    public Task InvalidateAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прибрати записи з обома ревізіями. Потрібно лише після Publish і міграції — " +
            "у звичайній роботі не викликається.");
}
```

---

### `src/Ecr.Infrastructure/Caching/AccessProfileCache.cs`
MODULE: infrastructure-caching | STAGE: 3

```csharp
using Ecr.Application.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш профілів доступу. Профіль будується **раз на сесію**: резолвити права
/// на кожну комірку — гарантована смерть продуктивності, бо бюджет відкриття
/// таблиці дає на права 50 мс на весь запит (ФВ-6.10).
/// </summary>
public sealed class AccessProfileCache(IMemoryCache memory)
{
    /// <summary>Повертає профіль із кешу або будує його.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="securityStamp">Штамп безпеки — частина ключа.</param>
    /// <param name="factory">Побудова профілю при промаху.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<AccessProfile> GetOrCreateAsync(
        int userId, string securityStamp, Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: ключ = $\"access:{userId}:{securityStamp}\". Зміна ролей або пароля змінює " +
            "SecurityStamp, тому старий запис просто перестає використовуватися — явна " +
            "інвалідація не потрібна.");
}
```

---

## 5. Безпека

### `src/Ecr.Infrastructure/Security/AccessDecisionService.cs`
MODULE: infrastructure-security | STAGE: 3
CONTRACT: 02-contracts.md#access-contract
SCOPE: **єдина** точка рішень про доступ.
NOT IN SCOPE: перевірки ролей будь-де ще — вони заборонені архітектурним тестом.

```csharp
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Реалізація <see cref="IAccessDecisionService"/>: поєднує RBAC, стан періоду,
/// правила періодів шаблону, статус документа і структурні обмеження.
/// </summary>
/// <remarks>
/// Повертає **причину**, а не <c>bool</c>: користувач має розуміти, чому
/// комірка сіра, інакше він піде до адміністратора, а той — до розробника.
/// </remarks>
public sealed class AccessDecisionService(
    EcrDbContext db,
    IMetadataCache metadata,
    Caching.AccessProfileCache profileCache) : IAccessDecisionService
{
    /// <inheritdoc />
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: зібрати ролі користувача, їхні функціональні права і ресурсні гранти; " +
            "РОЗГОРНУТИ успадкування Project → Sheet → Table → Column у плоску мапу; " +
            "окремо зібрати заборони (IsDeny) — вони виграють на будь-якому рівні (ФВ-6.6); " +
            "ключ кешу = userId + securityStamp.");

    /// <inheritdoc />
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень гранта на проєкт документа має бути >= Read.");

    /// <inheritdoc />
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — порядок перевірок від найдешевшої до найдорожчої, повертати ПЕРШУ причину:\n" +
            "1) Project.Status == Archived → ProjectArchived;\n" +
            "2) Project.IsArchiving → ArchivingInProgress;\n" +
            "3) Period.State: Scheduled → PeriodNotOpenYet, Closed → PeriodClosed;\n" +
            "   ⚠ закритий період блокує ВСІХ, включно з Manage (02c A7);\n" +
            "4) PeriodAccessRuleDef для аркуша й номера періоду → OutOfAccessWindow;\n" +
            "5) ApprovalState аркуша: Submitted → DocumentSubmitted, Approved → DocumentApproved;\n" +
            "   ⚠ Grace дає час на правки НЕПОДАНИХ документів, а не право змінити подану форму (D-67);\n" +
            "6) ColumnDef.IsComputed → CalculatedCell; IsReadOnly → ColumnReadOnly;\n" +
            "7) RowDef.IsReadOnly → RowReadOnly;\n" +
            "8) profile.LevelFor(Column|Table|Sheet|Project) < Write → NoGrant;\n" +
            "⚠ Project.CurrentPeriod у цьому ланцюгу НЕ бере участі: інакше «пін» став би " +
            "прихованим правом редагувати закрите (D-77).");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: обчислити спільні для зрізу умови ОДИН раз (проєкт, період, правила періодів, " +
            "статус аркуша, грант на таблицю), далі пройтися по колонках і рядках у пам'яті. " +
            "Поштучний виклик CanEditCellAsync у циклі — антипатерн: він не вкладається в бюджет.");

    /// <inheritdoc />
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень >= Submit; немає незакритих Error валідації.");

    /// <inheritdoc />
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень >= Approve; стан аркуша = Submitted.");
}
```

---

### `src/Ecr.Infrastructure/Security/PasswordHasher.cs`
MODULE: infrastructure-security | STAGE: 3

```csharp
using System.Security.Cryptography;
using Ecr.Application.Security;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Хешування паролів локальних облікових записів: PBKDF2-HMAC-SHA512.
/// </summary>
/// <remarks>
/// Формат: <c>{версія}.{ітерації}.{сіль-base64}.{хеш-base64}</c> — параметри
/// зберігаються поруч, щоб їх можна було посилити без міграції всіх паролів.
/// </remarks>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 64;
    private const int Iterations = 210_000;

    /// <inheritdoc />
    public string Hash(string password)
        => throw new NotImplementedException(
            "TODO: RandomNumberGenerator.GetBytes(SaltSize); Rfc2898DeriveBytes.Pbkdf2 з " +
            "HashAlgorithmName.SHA512; зібрати рядок за форматом вище.");

    /// <inheritdoc />
    public bool Verify(string password, string hash)
        => throw new NotImplementedException(
            "TODO: розібрати формат; перерахувати; порівняти CryptographicOperations.FixedTimeEquals. " +
            "Звичайне порівняння масивів дає таймінг-атаку.");

    /// <inheritdoc />
    public bool NeedsRehash(string hash)
        => throw new NotImplementedException("TODO: розібрати параметри і порівняти з поточними.");
}
```

---

### `src/Ecr.Infrastructure/Security/SecurityStampValidator.cs`
MODULE: infrastructure-security | STAGE: 3

```csharp
namespace Ecr.Infrastructure.Security;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на **кожен** запит: відкликання ролі має
/// діяти негайно, а не після закінчення cookie (ФВ-6.10, тест безпеки №2).
/// </summary>
public sealed class SecurityStampValidator(EcrDbContext db)
{
    /// <summary>Чи актуальний штамп із cookie.</summary>
    public Task<bool> IsCurrentAsync(int userId, string stampFromCookie, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: легкий запит SELECT SecurityStamp FROM sec.[User] WHERE Id = @id; " +
            "порівняти. Кешувати на кілька секунд можна, довше — ні: сенс саме в негайності.");
}
```

---

## 6. Фонові задачі

### `src/Ecr.Infrastructure/Jobs/QuartzJobScheduler.cs`
MODULE: infrastructure-jobs | STAGE: 5
CONTRACT: 02-contracts.md#ports

```csharp
using Ecr.Application.Ports;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Реалізація <see cref="IBackgroundJobScheduler"/> на Quartz (Apache-2.0).
/// </summary>
/// <remarks>
/// Порт існує саме для того, щоб заміна на Hangfire коштувала день, якщо ІБ
/// погодить LGPL (D-09). Тому специфіка Quartz не має протікати назовні.
/// </remarks>
public sealed class QuartzJobScheduler(ISchedulerFactory schedulerFactory) : IBackgroundJobScheduler
{
    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob
        => throw new NotImplementedException(
            "TODO: створити JobDetail з унікальним ключем, покласти payload у JobDataMap як JSON, " +
            "запланувати негайний тригер; повернути ключ як jobId.");

    /// <inheritdoc />
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob
        => throw new NotImplementedException("TODO: CronScheduleBuilder; ідемпотентно за ключем задачі.");

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: scheduler.DeleteJob за ключем; якщо задача вже виконується — позначити скасування " +
            "через CancellationToken, а не вбивати потік.");

    /// <inheritdoc />
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
        => throw new NotImplementedException("TODO: читати з itg.JobProgress, а не з внутрішнього стану Quartz.");
}
```

---

### `src/Ecr.Infrastructure/Jobs/PeriodStateJob.cs`
MODULE: infrastructure-jobs | STAGE: 3

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переводить періоди між станами і оновлює <c>Project.CurrentPeriod</c>.
/// </summary>
/// <remarks>
/// Стан періоду — **збережене значення**, а не функція від <c>now()</c> у
/// запиті (ФВ-1.12). Інакше кожна перевірка доступу рахувала б offsets, а межа
/// «останнього дня» залежала б від того, о котрій виконано запит.
/// </remarks>
public sealed class PeriodStateJob(
    EcrDbContext db,
    PeriodStateCalculator calculator,
    IClock clock) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: для кожного активного проєкту:\n" +
            "1) взяти TimeZoneInfo за Project.TimeZoneId — межі рахуються В ПОЯСІ МАЙДАНЧИКА (D-68);\n" +
            "2) для кожного періоду обчислити цільовий стан calculator.Calculate;\n" +
            "3) змінені стани зберегти пакетно через ExecuteUpdate;\n" +
            "4) якщо CurrentPeriodMode == Auto — оновити CurrentPeriodId через " +
            "   calculator.SelectCurrentPeriod; при Pinned не чіпати;\n" +
            "5) записати MaintenanceRun.\n" +
            "Задача має запускатися за розкладом у поясі майданчика, не о UTC-опівночі.");
}
```

---

### `src/Ecr.Infrastructure/Jobs/ArchiveJob.cs`
MODULE: infrastructure-jobs | STAGE: 5

```csharp
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Архівація закритого року.
/// </summary>
/// <remarks>
/// ⚠ Сам DDL і <c>TRUNCATE … WITH (PARTITIONS)</c> виконує **збережена
/// процедура під окремим principal** (`D-66`): обліковий запис застосунку не
/// має ані DDL-прав, ані права запису в <c>arc.*</c>. Ця задача лише **викликає**
/// процедуру, стежить за прогресом і алертить.
/// </remarks>
public sealed class ArchiveJob(EcrDbContext db, ISqlCapabilities capabilities) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити, що проєкт у Closed і сплив YearGraceOffsetDays;\n" +
            "2) визначити діапазон PeriodKey року за PeriodKind (не жорстко YYYY01..YYYY12: " +
            "   для квартальних це YYYY01..YYYY04, R-A6);\n" +
            "3) EXEC arc.usp_ArchiveYear із розміром батча capabilities.ArchiveBatchSize;\n" +
            "4) стежити за itg.ArchiveRun і повідомляти прогрес;\n" +
            "5) при Failed — алерт і ЗУПИНКА: дані джерела на місці, повторний запуск " +
            "   продовжить із LastDonePeriodKey (АРХ-3a).\n" +
            "Повна зупинка системи не потрібна; потрібне вікно низької активності.");
}
```

---

### `src/Ecr.Infrastructure/Jobs/RecalculationJob.cs`
MODULE: infrastructure-jobs | STAGE: 4

```csharp
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перерахунок: інкрементний за dirty-set або повний за адміністративною
/// командою.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, і формулювання «не гірше» тут не застосовується.
/// Звідси вимоги: паралельне виконання за рівнями топологічного графа, пакетне
/// читання входів, <c>SqlBulkCopy</c> результатів, проміжні значення в пам'яті.
/// </remarks>
public sealed class RecalculationJob(
    Ecr.Application.Recalculation.RecalculationService recalc,
    ISqlCapabilities capabilities) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: розібрати payload (документ+період або проєкт+рік); " +
            "виконати рівні графа ПАРАЛЕЛЬНО (Parallel.ForEachAsync із обмеженням " +
            "MaxParallelRecalculation), рівень за рівнем; " +
            "результати писати SqlBulkCopy, а не SaveChanges у циклі; " +
            "ІЗОЛЯЦІЯ від інтерактивного піку обов'язкова: на Enterprise — Resource Governor, " +
            "на Standard — знижена стеля воркерів у дні піку (АРХ-7). " +
            "Перерахунок на 10 хвилин не має права з'їсти бюджет p95 операторів.");
}
```

> **Решта задач** — за тим самим зразком:
> `CollectionJob` (ідемпотентний збір, catch-up, журнал покриття),
> `ConsistencyCheckJob` (щоніч, перевіряє інваріанти **і архів**; знахідка —
> баг, а не шум), `ReportSnapshotJob` (побудова зрізів після завершення
> `CalculationRun`), `PartitionCheckJob` (перевіряє **запас** партицій і
> алертить; сам `SPLIT` робить SQL Agent — `D-66`), `NotificationJob`
> (алерти визначеній групі; мовчазний збій неприпустимий).

### `src/Ecr.Infrastructure/Jobs/OrphanScanJob.cs`
MODULE: infrastructure-jobs | STAGE: 4
CONTRACT: [`02-contracts.md#ports`](02-contracts.md#ports) — `IOrphanScanner`
SCOPE: нічний прохід, що ставить і **знімає** `doc.TableRow.IsOrphaned`.
NOT IN SCOPE: перевірка при читанні зрізу — вона там заборонена.

```csharp
// src/Ecr.Infrastructure/Jobs/OrphanScanJob.cs
namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Знаходить рядки, що посилаються на записи реєстру, які перестали бути
/// чинними у своєму періоді (<c>ФВ-8.13</c>, <c>D-98</c>).
/// </summary>
/// <remarks>
/// Чому це задача, а не перевірка при читанні: бюджет зрізу — 400 мс p95 на
/// ~5 000 комірок. Темпоральна перевірка на кожен рядок при кожному відкритті
/// таблиці зжерла б його цілком. Ознака зберігається, а не рахується щоразу.
/// <para>
/// Задача **симетрична**: вона так само знімає ознаку з рядків, що знову стали
/// чинними. Інакше виправлення довідника не розблокувало б <c>Submit</c>, і
/// користувач лишився б із помилкою, причину якої вже усунуто.
/// </para>
/// </remarks>
public sealed class OrphanScanJob(IOrphanScanner scanner) : IBackgroundJob
{
    public string Code => "orphan-scan";

    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) прохід по відкритих і Grace-періодах — закриті не чіпати, " +
            "їхні дані вже подані і ознака нічого не змінить;\n" +
            "2) set-based UPDATE, не рядок за рядком: обсяг — мільйони рядків;\n" +
            "3) ставити І знімати IsOrphaned одним проходом, OrphanedAt = null при знятті;\n" +
            "4) прогрес по періодах, щоб задачу було видно в черзі;\n" +
            "5) підсумок у журнал: скільки поставлено, скільки знято. " +
            "Ненульове зняття — нормально; ненульова постановка — привід подивитися, " +
            "що сталося з довідником.");
}
```

---

## 7. Старт застосунку

### `src/Ecr.Infrastructure/Startup/SqlCapabilitiesProbe.cs`
MODULE: infrastructure-startup | STAGE: 1
CONTRACT: 02-contracts.md#ports

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Визначає можливості СУБД при старті (АРХ-7).
/// </summary>
/// <remarks>
/// Редакція впливає **лише на операційні стратегії**, ніколи — на модель даних,
/// семантику чи числа. Тому результат використовують тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>; у бізнес-коді звертатися сюди
/// заборонено.
/// </remarks>
public sealed class SqlCapabilitiesProbe : ISqlCapabilities
{
    /// <summary>Виконує запит і заповнює властивості.</summary>
    public Task ProbeAsync(string connectionString, SqlEditionMode configuredMode, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: SELECT SERVERPROPERTY('Edition'), ('EngineEdition'), ('ProductMajorVersion'), " +
            "DATABASEPROPERTYEX(DB_NAME(),'IsReadCommittedSnapshotOn').\n" +
            "EffectiveMode: configuredMode != Auto → configuredMode; інакше EngineEdition == 3 → " +
            "Enterprise, інакше Standard.\n" +
            "⚠ Developer/Evaluation повідомляють EngineEdition = 3 і виглядають як Enterprise — " +
            "тому в проді режим фіксують явно.\n" +
            "SupportsOnlineIndexRebuild і SupportsResourceGovernor = (EffectiveMode == Enterprise).\n" +
            "ArchiveBatchSize: Standard 500_000, Enterprise 2_000_000.");

    public SqlEditionMode EffectiveMode { get; private set; }
    public string EditionName { get; private set; } = string.Empty;
    public int ProductMajorVersion { get; private set; }
    public bool IsReadCommittedSnapshotOn { get; private set; }
    public bool SupportsOnlineIndexRebuild { get; private set; }
    public bool SupportsResourceGovernor { get; private set; }
    public int ArchiveBatchSize { get; private set; }
}
```

---

### `src/Ecr.Infrastructure/Startup/SchemaValidator.cs`
MODULE: infrastructure-startup | STAGE: 1

```csharp
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Перевірки при старті (ФВ-7.9). Мета — **впасти зрозуміло**, а не працювати
/// на несумісному середовищі й з'ясувати це на першому записі.
/// </summary>
public sealed class SchemaValidator(EcrDbContext db, ISqlCapabilities capabilities)
{
    /// <summary>Виконує послідовність перевірок.</summary>
    /// <param name="startupMode"><c>Validate</c> у прод, <c>Migrate</c> у dev/test.</param>
    /// <exception cref="InvalidOperationException">
    /// Середовище непридатне; повідомлення пояснює, що саме і як виправити.
    /// </exception>
    public Task ValidateAsync(string startupMode, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — послідовність із B01 §6.3:\n" +
            "1) retry-очікування доступності БД (БД піднімається довше застосунку);\n" +
            "2) у БД є міграція, якої немає у збірці → ФАТАЛЬНО (відкат версії застосунку);\n" +
            "3) Validate: pending.Any() → ФАТАЛЬНО зі списком; Migrate: sp_getapplock → " +
            "   Migrate() → release (щоб два інстанси не мігрували одночасно);\n" +
            "4) ⛔ Standard із ProductMajorVersion < 13 → ЗУПИНКА СТАРТУ: немає партиціонування, " +
            "   columnstore і компресії, тобто модель архівації не працює в принципі (АРХ-7);\n" +
            "5) RCSI вимкнено → Critical у health і запис у журнал (вмикання — операція DBA);\n" +
            "6) відсутні файлові групи або схеми партиціонування → ЗУПИНКА з інструкцією, " +
            "   який скрипт виконати;\n" +
            "7) немає запасу партицій на наступний період → Warning у health.");
}
```

---

### `src/Ecr.Infrastructure/Startup/MetadataWarmup.cs`
MODULE: infrastructure-startup | STAGE: 1

```csharp
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Прогріває кеш метаданих активних версій. Без цього перші користувачі після
/// деплою платять за завантаження схеми.
/// </summary>
public sealed class MetadataWarmup(EcrDbContext db, IMetadataCache cache)
{
    /// <summary>Завантажує знімки всіх версій, на яких є активні проєкти.</summary>
    public Task WarmupAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: знайти TemplateVersionId активних проєктів; для кожного викликати cache.GetAsync. " +
            "Помилка прогріву не має валити старт — це Warning, не Critical.");
}
```

---

### `src/Ecr.Infrastructure/DependencyInjection.cs`
MODULE: infrastructure | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure;

/// <summary>Реєстрація інфраструктури в контейнері.</summary>
public static class DependencyInjection
{
    /// <summary>Додає EF Core, сховища, кеш, безпеку і планувальник.</summary>
    public static IServiceCollection AddEcrInfrastructure(this IServiceCollection services, IConfiguration configuration)
        => throw new NotImplementedException(
            "TODO:\n" +
            "services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(cs, sql => {\n" +
            "    sql.CommandTimeout(...); sql.EnableRetryOnFailure(); }));\n" +
            "AddScoped<ICellStore, NormalizedCellStore>();  // Hybrid — лише за результатом гейта\n" +
            "AddSingleton<IMetadataCache, MetadataCache>();\n" +
            "AddScoped<IAccessDecisionService, AccessDecisionService>();\n" +
            "AddSingleton<IPasswordHasher, PasswordHasher>();\n" +
            "AddSingleton<ISqlCapabilities, SqlCapabilitiesProbe>();\n" +
            "AddScoped<IUnitOfWork, UnitOfWork>();\n" +
            "AddScoped<IAuditWriter, AuditWriter>();\n" +
            "AddSingleton<IBackgroundJobScheduler, QuartzJobScheduler>();\n" +
            "AddQuartz(...) + AddQuartzHostedService();\n" +
            "AddDistributedSqlServerCache(...)  // без Redis (D-06)\n" +
            "AddMemoryCache();\n" +
            "AddSingleton<IClock, SystemClock>();\n" +
            "⚠ IExternalDataSink НЕ реєструється: його не існує (D-44).");
}
```
