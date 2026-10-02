using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="DataSource"/>.</summary>
/// <remarks>
/// ⛔ <c>SecretName</c> — лише ім'я секрету (ФВ-6.11). Значення в базі не
/// зберігається ніколи: воно потрапило б у резервну копію і в експорт
/// конфігурації.
/// </remarks>
public sealed class DataSourceConfiguration : IEntityTypeConfiguration<DataSource>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DataSource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DataSource", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Endpoint).HasMaxLength(400).IsRequired();
        builder.Property(x => x.SecondaryEndpoint).HasMaxLength(400);
        builder.Property(x => x.SecretName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Catalog).HasColumnName("Catalog").HasMaxLength(200);
        builder.Property(x => x.MaxParallel).HasDefaultValue(4);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_DataSource");
    }
}

/// <summary>Конфігурація <see cref="SourceEntity"/>.</summary>
public sealed class SourceEntityConfiguration : IEntityTypeConfiguration<SourceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceEntity", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(400);
        builder.Property(x => x.EntityPath).HasMaxLength(400);
        builder.Property(x => x.SourceKind).HasDefaultValue(Domain.Enums.RegistrySourceKind.External);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

        // D-212: політика синку довідника. ⚠ Без HasDefaultValue у моделі:
        // умовчання (0 / false) дорівнює CLR-умовчанню, і EF кладе DEFAULT у
        // AddColumn сам — для наявних рядків цього досить, а модель не
        // отримує sentinel-пастки «значення 0 не відправляється».
        builder.Property(x => x.OnMissingInSource).HasColumnType("tinyint");
        builder.Property(x => x.ValidFromAttribute).HasMaxLength(SourceEntity.MaxValidityAttributeLength);
        builder.Property(x => x.ValidToAttribute).HasMaxLength(SourceEntity.MaxValidityAttributeLength);
        builder.ToTable("SourceEntity", "ext", t => t.HasCheckConstraint(
            "CK_SE_OnMissingInSource", "[OnMissingInSource] IN (0, 1, 2)"));

        builder.HasIndex(x => new { x.DataSourceId, x.Code })
               .IsUnique().HasDatabaseName("UQ_SourceEntity");

        builder.HasOne<DataSource>().WithMany().HasForeignKey(x => x.DataSourceId)
               .HasConstraintName("FK_SE_DataSource");
        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_SE_Registry");
    }
}

/// <summary>Конфігурація <see cref="EntityFieldMap"/> — мапінгу полів із одиницями.</summary>
public sealed class EntityFieldMapConfiguration : IEntityTypeConfiguration<EntityFieldMap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EntityFieldMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EntityFieldMap", "ext", t => t.HasCheckConstraint(
            "CK_EFM_Target",
            // Ціль мусить бути рівно одна і відповідати виду: мапінг «на
            // колонку», що вказує на поле довідника, тихо не спрацював би.
            "(TargetKind = 0 AND TargetColumnDefId IS NOT NULL) OR "
            + "(TargetKind = 1 AND TargetRegistryFieldDefId IS NOT NULL)"));

        // ⚠ Кожне обмеження — окремим викликом: `t.HasCheckConstraint` повертає
        // будівник обмеження, а не таблиці, і ланцюжок не збирається.
        builder.ToTable("EntityFieldMap", "ext", t => t.HasCheckConstraint(
                "CK_EFM_Transform",
                // ⛔ Перелік закритий (`D-118`). Довільний код перетворення —
                // це можливість вписати щось, чого обробник не знає, і
                // дізнатися про це під час збору, а не при налаштуванні.
                // HSE301 M1: + згортки за часом (`D-172`), у кінець переліку.
                "TransformCode IS NULL OR TransformCode IN "
                + "(N'Sum', N'Avg', N'Min', N'Max', N'Last', N'First', "
                + "N'TimeWeightedAvg', N'TimeIntegral')"));

        builder.ToTable("EntityFieldMap", "ext", t => t.HasCheckConstraint(
                "CK_EFM_Materialization",
                // ⛔ Рядок і агрегація нерозривні. Мапінг, що називає рядок і
                // не каже, як згортати точки періоду, дав би вибір коду — а
                // код його зробити не може: він не знає, величина миттєва чи
                // накопичувальна. Це помилка конфігурації, і ловиться вона
                // тут, а не збором.
                "TargetRowKey IS NULL OR TransformCode IS NOT NULL"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceField).HasMaxLength(200).IsRequired();
        builder.Property(x => x.TransformCode).HasMaxLength(64);

        // ⚠ 100 символів — стільки ж, скільки в `doc.TableRow.RowKey`: ключ,
        // довший за той, на який він посилається, не знайшов би нічого.
        builder.Property(x => x.TargetRowKey).HasMaxLength(100);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

        // HSE301 §4.1: форма ряду між точками; наявні мапінги — лінійні.
        builder.Property(x => x.IsStep).HasDefaultValue(false);

        // ФВ-16.9: позначка «чекає рішення про одиницю». Id без FK навмисно:
        // одиницю можуть прибрати з довідника до рішення, і тоді «прийняти»
        // має відповісти 422, а не впасти на зовнішньому ключі.
        builder.Property(x => x.PendingSourceUnitCode).HasMaxLength(64);
        builder.Property(x => x.PendingSourceUnitDetectedAt).HasColumnType("datetime2(3)");
        builder.Ignore(x => x.HasPendingSourceUnitChange);

        builder.HasIndex(x => new { x.SourceEntityId, x.SourceField })
               .IsUnique().HasDatabaseName("UQ_EntityFieldMap");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_EFM_Entity");
        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.TargetColumnDefId)
               .HasConstraintName("FK_EFM_Column");
        builder.HasOne<RegistryFieldDef>().WithMany().HasForeignKey(x => x.TargetRegistryFieldDefId)
               .HasConstraintName("FK_EFM_Field");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.SourceUnitId)
               .HasConstraintName("FK_EFM_SrcUnit");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.TargetUnitId)
               .HasConstraintName("FK_EFM_TgtUnit");
    }
}

/// <summary>Конфігурація <see cref="CollectionSchedule"/>.</summary>
public sealed class CollectionScheduleConfiguration : IEntityTypeConfiguration<CollectionSchedule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CollectionSchedule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CollectionSchedule", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CronExpression).HasMaxLength(CollectionSchedule.MaxCronLength).IsRequired();
        builder.Property(x => x.LookbackDays).HasDefaultValue(7);
        builder.Property(x => x.IsEnabled).HasDefaultValue(true);
        builder.Property(x => x.LastRunAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.Watermark).HasColumnType("datetime2(3)");
        builder.Property(x => x.LastError).HasMaxLength(CollectionSchedule.MaxLastErrorLength);
        builder.Property(x => x.LastErrorAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Один розклад на сутність: два означали б два незалежні watermark, і
        // проміжок між ними не покривав би ніхто.
        builder.HasIndex(x => x.SourceEntityId)
               .IsUnique().HasDatabaseName("UQ_CollectionSchedule");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_CS_Entity");

        // ФВ-13.15: залежність від іншого розкладу. Самопосилання без каскаду (SQL Server
        // забороняє каскад у циклі): знімає залежність прикладний шар перед видаленням.
        builder.HasOne<CollectionSchedule>().WithMany().HasForeignKey(x => x.DependsOnScheduleId)
               .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_CS_DependsOn");
    }
}

/// <summary>Конфігурація <see cref="RawDataPoint"/> — сирих даних джерела.</summary>
public sealed class RawDataPointConfiguration : IEntityTypeConfiguration<RawDataPoint>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RawDataPoint> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RawDataPoint", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourcePath).HasMaxLength(400).IsRequired();
        builder.Property(x => x.Timestamp).HasColumnName("Timestamp").HasColumnType("datetime2(3)");
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(34,16)");
        builder.Property(x => x.ValueString).HasMaxLength(1000);
        builder.Property(x => x.Quality).HasMaxLength(32);
        builder.Property(x => x.RetrievedAt).HasColumnType("datetime2(3)").IsRequired();

        // ⚠ Природний ключ — це й є ідемпотентність збору (ФВ-11.3). Без нього
        // наздоганяння пропущеного вікна подвоювало б точки, і сума за період
        // ставала б удвічі більшою після кожного відновлення.
        builder.HasIndex(x => new { x.SourceEntityId, x.SourcePath, x.Timestamp })
               .IsUnique().HasDatabaseName("UQ_RawDataPoint");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_RDP_Entity");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId)
               .HasConstraintName("FK_RDP_Unit");
    }
}

/// <summary>Конфігурація <see cref="ConsistencyRule"/>.</summary>
public sealed class ConsistencyRuleConfiguration : IEntityTypeConfiguration<ConsistencyRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConsistencyRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ConsistencyRule", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Expression).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_ConsistencyRule");
    }
}

/// <summary>Конфігурація legacy-мапінгу аркушів.</summary>
public sealed class LegacySheetMappingConfiguration : IEntityTypeConfiguration<LegacySheetMapping>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegacySheetMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LegacySheetMapping", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LegacyName).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.SheetDefId).IsUnique().HasDatabaseName("UQ_LegacySheetMapping");
        builder.HasOne<SheetDef>().WithMany().HasForeignKey(x => x.SheetDefId)
               .HasConstraintName("FK_LSM_Sheet");
    }
}

/// <summary>Конфігурація legacy-мапінгу таблиць.</summary>
public sealed class LegacyTableMappingConfiguration : IEntityTypeConfiguration<LegacyTableMapping>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegacyTableMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LegacyTableMapping", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventFrameTemplate).HasMaxLength(200);
        builder.Property(x => x.ElementTemplate).HasMaxLength(200);
        builder.Property(x => x.ElementName).HasMaxLength(200);
        builder.HasIndex(x => x.TableDefId).IsUnique().HasDatabaseName("UQ_LegacyTableMapping");
        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_LTM_Table");
    }
}

/// <summary>Конфігурація legacy-мапінгу рядків.</summary>
public sealed class LegacyRowMappingConfiguration : IEntityTypeConfiguration<LegacyRowMapping>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegacyRowMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LegacyRowMapping", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AfAttributeName).HasMaxLength(100);
        builder.HasIndex(x => x.RowDefId).IsUnique().HasDatabaseName("UQ_LegacyRowMapping");
        builder.HasOne<RowDef>().WithMany().HasForeignKey(x => x.RowDefId)
               .HasConstraintName("FK_LRM_Row");
    }
}

/// <summary>Конфігурація legacy-мапінгу колонок.</summary>
public sealed class LegacyColumnMappingConfiguration : IEntityTypeConfiguration<LegacyColumnMapping>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegacyColumnMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LegacyColumnMapping", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LegacyExcelColumn).HasMaxLength(10);
        builder.Property(x => x.AfAttributeName).HasMaxLength(100);
        builder.HasIndex(x => x.ColumnDefId).IsUnique().HasDatabaseName("UQ_LegacyColumnMapping");
        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.ColumnDefId)
               .HasConstraintName("FK_LCM_Column");
    }
}

/// <summary>Конфігурація <see cref="CollectionRun"/>.</summary>
public sealed class CollectionRunConfiguration : IEntityTypeConfiguration<CollectionRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CollectionRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CollectionRun", "itg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RangeFrom).HasColumnType("datetime2(3)");
        builder.Property(x => x.RangeTo).HasColumnType("datetime2(3)");
        builder.Property(x => x.StartedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.FinishedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.PointsRetrieved).HasDefaultValue(0);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.IsCatchUp).HasDefaultValue(false);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_CRun_Entity");

        // Журнал прогонів (CollectionRunReader): фільтр за сутністю + «новіші першими»
        // за Id. Без індексу — зворотний скан PK з фільтром після seek.
        builder.HasIndex(x => new { x.SourceEntityId, x.Id }, "IX_CollectionRun_SourceEntityId_Id")
               .IsDescending(false, true)
               .IncludeProperties(x => new { x.StartedAt, x.Status, x.PointsRetrieved, x.FinishedAt });
    }
}

/// <summary>Конфігурація <see cref="CollectionCoverage"/> — журналу покриття.</summary>
public sealed class CollectionCoverageConfiguration : IEntityTypeConfiguration<CollectionCoverage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CollectionCoverage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CollectionCoverage", "itg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasMaxLength(64);
        builder.Property(x => x.Details).HasMaxLength(1000);
        builder.Property(x => x.CoveredFrom).HasColumnType("datetime2(3)");
        builder.Property(x => x.CoveredTo).HasColumnType("datetime2(3)");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_CCov_Entity");
        builder.HasOne<CollectionRun>().WithMany().HasForeignKey(x => x.CollectionRunId)
               .HasConstraintName("FK_CCov_Run");

        // Деталь прогону: покриття одного прогону в порядку CoveredFrom — seek без сортування.
        builder.HasIndex(x => new { x.CollectionRunId, x.CoveredFrom }, "IX_CollectionCoverage_CollectionRunId")
               .IncludeProperties(x => x.CoveredTo);

        // Острови покриття джерела (P5, `CollectionStore.ReadCoverageIslandsAsync`):
        // успішні інтервали (`Status IS NULL`) однієї сутності в порядку CoveredTo.
        builder.HasIndex(x => new { x.SourceEntityId, x.CoveredTo }, "IX_CollectionCoverage_SourceEntity_CoveredTo")
               .IncludeProperties(x => x.CoveredFrom)
               .HasFilter("[Status] IS NULL");
    }
}

/// <summary>Конфігурація <see cref="ArchiveRun"/>.</summary>
public sealed class ArchiveRunConfiguration : IEntityTypeConfiguration<ArchiveRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ArchiveRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ArchiveRun", "itg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Direction).HasMaxLength(16).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.FinishedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowsMoved).HasDefaultValue(0L);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        builder.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_ARun_Project");
    }
}

/// <summary>Конфігурація <see cref="MaintenanceRun"/>.</summary>
public sealed class MaintenanceRunConfiguration : IEntityTypeConfiguration<MaintenanceRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MaintenanceRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MaintenanceRun", "itg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.JobCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.FinishedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
    }
}

/// <summary>Конфігурація <see cref="ScanCursor"/>.</summary>
/// <remarks>
/// ⚠ Індексу немає навмисно, і це не забутий рядок: таблиця має рівно стільки
/// рядків, скільки в системі відновлюваних сканувань (одиниці), і читається
/// вона завжди за первинним ключем. Індекс тут коштував би супроводу, не
/// даючи жодного плану, якого не дає <c>PK</c>.
/// </remarks>
public sealed class ScanCursorConfiguration : IEntityTypeConfiguration<ScanCursor>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScanCursor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ScanCursor", "itg");

        // Ключ — код сканування: курсор на кожне сканування рівно один, і саме
        // це має тримати база, а не домовленість у коді.
        builder.HasKey(x => x.ScanCode).HasName("PK_ScanCursor");
        builder.Property(x => x.ScanCode).HasMaxLength(64);
        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.LastCycleCompletedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.CyclesCompleted).HasDefaultValue(0);
    }
}

/// <summary>Конфігурація <see cref="JobProgress"/>.</summary>
public sealed class JobProgressConfiguration : IEntityTypeConfiguration<JobProgress>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<JobProgress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("JobProgress", "itg");

        // Ключ — сам JobId: він приходить від планувальника і вже унікальний.
        builder.HasKey(x => x.JobId).HasName("PK_JobProgress");
        builder.Property(x => x.JobId).HasMaxLength(100);
        builder.Property(x => x.JobCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Percent).HasDefaultValue(0);
        builder.Property(x => x.Message).HasMaxLength(400);
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.Error).HasColumnName("Error").HasMaxLength(2000);
        builder.Property(x => x.HeartbeatAt).HasColumnType("datetime2(3)");

        // BE-08: обидві nullable — наявні рядки лишаються валідними без backfill.
        builder.Property(x => x.CorrelationId).HasMaxLength(JobProgress.MaxCorrelationIdLength);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ErrorCode).HasMaxLength(JobProgress.MaxErrorCodeLength).IsUnicode(false);

        // ⛔ Та сама причина, що й в `IX_Outbox_Claim` (Q-241): прибирання на
        // старті фільтрує саме за парою (State, HeartbeatAt), і без індексу
        // воно сканувало б усю історію задач, яка не видаляється.
        builder.HasIndex(x => new { x.State, x.HeartbeatAt }).HasDatabaseName("IX_JobProgress_Stale");

        // Процес-власник: «{машина}/{GUID}». nvarchar — ім'я хоста не зобов'язане бути ASCII.
        builder.Property(x => x.InstanceId).HasMaxLength(JobProgress.MaxInstanceIdLength);

        // ⚠ «Мої задачі» (`ListRecentAsync`, шапка опитує кожні 3–30 с):
        // `WHERE CreatedByUserId = @u [AND State/JobCode] ORDER BY UpdatedAt DESC`
        // + TOP. `IX_JobProgress_Stale` веде за State і автора не бачить —
        // без цього індексу кожне опитування сканує всю історію задач.
        // State/JobCode у INCLUDE — необов'язкові фільтри екрана черги
        // перевіряються в індексі, до key lookup лише TOP рядків.
        // Нефільтрований: предиката на активні стани запит не має.
        builder.HasIndex(x => new { x.CreatedByUserId, x.UpdatedAt }, "IX_JobProgress_CreatedBy_UpdatedAt")
               .IsDescending(false, true)
               .IncludeProperties(x => new { x.State, x.JobCode });

        // Вимір 2026-10-01 (docs/build/perf/jobs-stale-health-2026-10-01.md, 500 тис. рядків):
        // `/health/ready` (анонімний) рахує `Message LIKE '%"key"%'` за State + UpdatedAt. Без
        // індексу — скан таблиці (47 тис. читань); з ним — діапазон (254 читання). Message у
        // INCLUDE, щоб LIKE йшов по індексу без key lookup.
        builder.HasIndex(x => new { x.State, x.UpdatedAt }, "IX_JobProgress_State_UpdatedAt")
               .IncludeProperties(x => x.Message);

        // Дочірні задачі розкладу (P4): `GetFanOutAsync` шукає їх за батьком. Стовпець —
        // persisted-проєкція Payload, щоб його можна було індексувати.
        builder.Property(x => x.FanOutParentJobId)
               .HasMaxLength(100)
               .HasComputedColumnSql("CASE WHEN ISJSON([Payload]) = 1 THEN CAST(JSON_VALUE([Payload], N'$.fanOutParentJobId') AS nvarchar(100)) END", stored: true);
        builder.HasIndex(x => x.FanOutParentJobId, "IX_JobProgress_FanOutParent")
               .IncludeProperties(x => x.State);

        ConfigureQueue(builder);
    }

    /// <summary>Колонки й індекси черги в базі (MI-02, D-208).</summary>
    /// <remarks>
    /// ⚠ Усі фільтровані індекси вимагають <c>SET QUOTED_IDENTIFIER ON</c> і
    /// <c>ANSI_NULLS ON</c> у сесії, що пише в таблицю: SqlClient має їх
    /// типово, <c>sqlcmd</c> — лише з <c>-I</c> (так і йде розгортання,
    /// <c>setup-dev-db.ps1</c>, <c>verify-sql-scripts.ps1</c>).
    /// </remarks>
    private static void ConfigureQueue(EntityTypeBuilder<JobProgress> builder)
    {
        // Рядок черги (Lane NOT NULL) без AvailableAt ніхто б ніколи не взяв:
        // claim фільтрує `AvailableAt <= SYSUTCDATETIME()`, а NULL не проходить.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_JobProgress_QueueShape", "[Lane] IS NULL OR [AvailableAt] IS NOT NULL"));

        builder.Property(x => x.Lane).HasMaxLength(JobProgress.MaxLaneLength).IsUnicode(false);
        builder.Property(x => x.Payload).HasColumnType("nvarchar(max)");
        builder.Property(x => x.AvailableAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.LeaseUntil).HasColumnType("datetime2(3)");
        builder.Property(x => x.CancelRequestedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.TargetKey).HasMaxLength(JobProgress.MaxTargetKeyLength);

        // ⛔ Два індекси, а не один на (TargetKey) за активним станом: на ціль
        // дозволено рівно «1 Running + 1 Queued позаду». Running-індекс — справжній
        // запобіжник claim (правка А «Аудиту»): NOT EXISTS іде без READPAST, а
        // гонитву, яку він пропустить, база відбиває 2601/2627.
        builder.HasIndex(x => x.TargetKey, "UX_JobProgress_Target_Queued")
               .IsUnique()
               .HasFilter("[State] = 'Queued' AND [TargetKey] IS NOT NULL");
        builder.HasIndex(x => x.TargetKey, "UX_JobProgress_Target_Running")
               .IsUnique()
               .HasFilter("[State] = 'Running' AND [TargetKey] IS NOT NULL");

        // Claim: WHERE Lane IN (…) AND State = … AND AvailableAt <= now ORDER BY
        // AvailableAt, JobId. Фільтр відсікає історію задач і дзеркало Quartz.
        builder.HasIndex(x => new { x.Lane, x.State, x.AvailableAt }, "IX_JobProgress_Claim")
               .IncludeProperties(x => new { x.TargetKey, x.LeaseUntil, x.Attempt, x.ReclaimCount })
               .HasFilter("[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')");
    }
}

/// <summary>
/// Конфігурація <see cref="NotificationOutboxItem"/> — черги сповіщень.
/// </summary>
/// <remarks>
/// ⚠ Індекс за станом і часом: задача щогодини бере лише <c>Pending</c>, і без
/// індексу вона сканувала б усю історію сповіщень, яка не видаляється.
/// </remarks>
public sealed class NotificationOutboxConfiguration : IEntityTypeConfiguration<NotificationOutboxItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationOutboxItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationOutbox", "itg");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(400).IsRequired();
        builder.Property(x => x.Recipients).HasMaxLength(4000);
        builder.Property(x => x.State).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.Property(x => x.ClaimedAt);
        builder.Property(x => x.ClaimToken);

        builder.HasIndex(x => new { x.State, x.CreatedAt }).HasDatabaseName("IX_Outbox_State");

        // ⛔ Q-241: `ReclaimStaleAsync` фільтрує саме за цією парою — без
        // окремого індексу вона сканувала б усю таблицю (`IX_Outbox_State`
        // впорядкований за `CreatedAt`, не за `ClaimedAt`).
        builder.HasIndex(x => new { x.State, x.ClaimedAt }).HasDatabaseName("IX_Outbox_Claim");
    }
}
