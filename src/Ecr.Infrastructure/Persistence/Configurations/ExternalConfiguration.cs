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

        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceField).HasMaxLength(200).IsRequired();
        builder.Property(x => x.TransformCode).HasMaxLength(64);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

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
        builder.Property(x => x.CronExpression).HasMaxLength(100).IsRequired();
        builder.Property(x => x.LookbackDays).HasDefaultValue(7);
        builder.Property(x => x.IsEnabled).HasDefaultValue(true);
        builder.Property(x => x.LastRunAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.Watermark).HasColumnType("datetime2(3)");

        // Один розклад на сутність: два означали б два незалежні watermark, і
        // проміжок між ними не покривав би ніхто.
        builder.HasIndex(x => x.SourceEntityId)
               .IsUnique().HasDatabaseName("UQ_CollectionSchedule");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_CS_Entity");
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
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(28,10)");
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
        builder.Property(x => x.CoveredFrom).HasColumnType("datetime2(3)");
        builder.Property(x => x.CoveredTo).HasColumnType("datetime2(3)");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_CCov_Entity");
        builder.HasOne<CollectionRun>().WithMany().HasForeignKey(x => x.CollectionRunId)
               .HasConstraintName("FK_CCov_Run");
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

        builder.HasIndex(x => new { x.State, x.CreatedAt }).HasDatabaseName("IX_Outbox_State");
    }
}
