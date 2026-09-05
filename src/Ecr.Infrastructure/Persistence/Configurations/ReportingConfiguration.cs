using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="ReportDef"/>.</summary>
public sealed class ReportDefConfiguration : IEntityTypeConfiguration<ReportDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReportDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportDef", "rpt");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsRegulatory).HasDefaultValue(false);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_ReportDef");
    }
}

/// <summary>Конфігурація <see cref="ReportVersion"/>.</summary>
public sealed class ReportVersionConfiguration : IEntityTypeConfiguration<ReportVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReportVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportVersion", "rpt");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ColumnsJson).IsRequired();
        builder.Property(x => x.RulesJson).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").IsRequired();

        builder.HasIndex(x => new { x.ReportDefId, x.Version })
               .IsUnique().HasDatabaseName("UQ_ReportVersion");

        builder.HasOne<ReportDef>().WithMany().HasForeignKey(x => x.ReportDefId)
               .HasConstraintName("FK_RV_Def");
    }
}

/// <summary>Конфігурація <see cref="ReportSnapshot"/>.</summary>
public sealed class ReportSnapshotConfiguration : IEntityTypeConfiguration<ReportSnapshot>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReportSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportSnapshot", "rpt");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasDefaultValue(Domain.Enums.DocumentStatus.Draft);
        builder.Property(x => x.IsCurrent).HasDefaultValue(false);
        builder.Property(x => x.RowCount).HasColumnName("RowCount").HasDefaultValue(0);
        builder.Property(x => x.ContentHash).HasColumnType("varbinary(32)");
        builder.Property(x => x.BuiltAt).HasColumnType("datetime2(3)").IsRequired();

        // ⚠ Унікальний ФІЛЬТРОВАНИЙ індекс: поточний зріз для трійки
        // «версія × проєкт × період» може бути лише один. Без цього два
        // паралельні прогони дали б два «поточні», і регуляторна вʼюха
        // повернула б подвоєні рядки — не помилку, а просто вдвічі більше
        // число.
        builder.HasIndex(x => new { x.ReportVersionId, x.ProjectId, x.PeriodKey })
               .IsUnique()
               .HasFilter("[IsCurrent] = 1")
               .HasDatabaseName("UX_ReportSnapshot_Current");

        builder.HasOne<ReportVersion>().WithMany().HasForeignKey(x => x.ReportVersionId)
               .HasConstraintName("FK_Snap_Version");
        builder.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_Snap_Project");
    }
}

/// <summary>Конфігурація <see cref="ReportRow"/>.</summary>
public sealed class ReportRowConfiguration : IEntityTypeConfiguration<ReportRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReportRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportRow", "rpt");

        // Складений ключ без сурогата: рядок зрізу ідентифікується своїм
        // місцем у ньому, і додатковий Id лише збільшив би найбільшу
        // таблицю схеми.
        builder.HasKey(x => new { x.SnapshotId, x.RowNo, x.ColumnCode })
               .HasName("PK_ReportRow");

        builder.Property(x => x.ColumnCode).HasMaxLength(64);
        builder.Property(x => x.ValueString).HasMaxLength(1000);
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(28,10)");
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");

        builder.HasOne<ReportSnapshot>().WithMany().HasForeignKey(x => x.SnapshotId)
               .HasConstraintName("FK_RepRow_Snap");
    }
}

/// <summary>Конфігурація <see cref="DocumentIndexValue"/>.</summary>
public sealed class DocumentIndexValueConfiguration : IEntityTypeConfiguration<DocumentIndexValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentIndexValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentIndexValue", "doc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ValueString).HasMaxLength(400);
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(28,10)");
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.DocumentId, x.ColumnDefId })
               .IsUnique().HasDatabaseName("UQ_DocumentIndexValue");

        // Індекс пошуку: саме він робить фільтр по документах можливим без
        // жодного некластерного індексу на doc.CellValue.
        builder.HasIndex(x => new { x.ColumnDefId, x.ValueString })
               .HasDatabaseName("IX_DocumentIndexValue_Search")
               .IncludeProperties(x => x.DocumentId);

        builder.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_DocIx_Doc");
        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.ColumnDefId)
               .HasConstraintName("FK_DocIx_Column");
    }
}
