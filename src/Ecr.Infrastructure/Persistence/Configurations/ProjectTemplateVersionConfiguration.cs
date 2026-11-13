using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="ProjectTemplateVersion"/> (<c>doc.ProjectTemplateVersion</c>, RPT-STABLE Part B).</summary>
public sealed class ProjectTemplateVersionConfiguration : IEntityTypeConfiguration<ProjectTemplateVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProjectTemplateVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProjectTemplateVersion", "doc");

        // ✎ B74 PT01: шкала на пару (проєкт, шаблон). FK на doc.ProjectTemplate — у P6 (проєкти з фікстур EF рядка
        // ProjectTemplate не мають; до P6 їх доповнює блок ремонту 99-epilogue.sql).
        builder.HasKey(x => new { x.ProjectId, x.TemplateId, x.FromPeriodKey }).HasName("PK_ProjectTemplateVersion");

        builder.HasOne<Ecr.Domain.Entities.Documents.Project>()
               .WithMany()
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_ProjectTemplateVersion_Project");

        builder.HasOne<Template>()
               .WithMany()
               .HasForeignKey(x => x.TemplateId)
               .HasConstraintName("FK_ProjectTemplateVersion_Template");

        // Версія належить саме цьому шаблону: складений FK на AK_TemplateVersion_Id_Template.
        builder.HasOne<TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => new { x.TemplateVersionId, x.TemplateId })
               .HasPrincipalKey(v => new { v.Id, v.TemplateId })
               .HasConstraintName("FK_ProjectTemplateVersion_TemplateVersion");

        // Під складений FK (і пошук за версією - лічильники, видалення версії); TemplateId - під FK на шаблон.
        builder.HasIndex(x => new { x.TemplateVersionId, x.TemplateId }).HasDatabaseName("IX_ProjectTemplateVersion_TemplateVersion");
        builder.HasIndex(x => x.TemplateId).HasDatabaseName("IX_ProjectTemplateVersion_Template");
    }
}

/// <summary>Конфігурація <see cref="ProjectTemplate"/> (<c>doc.ProjectTemplate</c>, B74 PT01).</summary>
public sealed class ProjectTemplateConfiguration : IEntityTypeConfiguration<ProjectTemplate>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProjectTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProjectTemplate", "doc");
        builder.HasKey(x => x.Id).HasName("PK_ProjectTemplate");

        builder.HasIndex(x => new { x.ProjectId, x.TemplateId }).IsUnique().HasDatabaseName("UQ_ProjectTemplate");

        // «Проєкти на версії» (лічильники TemplateVersionStore, P2) і перевірка FK при видаленні версії.
        builder.HasIndex(x => new { x.CurrentTemplateVersionId, x.TemplateId }).HasDatabaseName("IX_ProjectTemplate_CurrentVersion");
        builder.HasIndex(x => x.TemplateId).HasDatabaseName("IX_ProjectTemplate_Template");

        // .WithMany(p => p.Templates) обов'язково: інакше навігація стала б другим зв'язком із тіньовою ProjectId1.
        builder.HasOne<Ecr.Domain.Entities.Documents.Project>()
               .WithMany(p => p.Templates)
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_ProjectTemplate_Project");

        builder.HasOne<Template>()
               .WithMany()
               .HasForeignKey(x => x.TemplateId)
               .HasConstraintName("FK_ProjectTemplate_Template");

        builder.HasOne<TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => new { x.CurrentTemplateVersionId, x.TemplateId })
               .HasPrincipalKey(v => new { v.Id, v.TemplateId })
               .HasConstraintName("FK_ProjectTemplate_TemplateVersion");
    }
}

/// <summary>Конфігурація <see cref="MonthInputAlias"/> (<c>cfg.MonthInputAlias</c>, Land-year L0 у PT01).</summary>
public sealed class MonthInputAliasConfiguration : IEntityTypeConfiguration<MonthInputAlias>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MonthInputAlias> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MonthInputAlias", "cfg", t => t.HasCheckConstraint(
            "CK_MonthInputAlias_Month", "MonthNumber BETWEEN 1 AND 12"));
        builder.HasKey(x => x.Id).HasName("PK_MonthInputAlias");
        builder.Property(x => x.Alias).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => new { x.TableDefId, x.Alias, x.MonthNumber })
               .IsUnique().HasDatabaseName("UQ_MonthInputAlias");
        builder.HasIndex(x => x.ColumnDefId).HasDatabaseName("IX_MonthInputAlias_Column");

        builder.HasOne<TableDef>()
               .WithMany()
               .HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_MonthInputAlias_Table");

        builder.HasOne<ColumnDef>()
               .WithMany()
               .HasForeignKey(x => x.ColumnDefId)
               .HasConstraintName("FK_MonthInputAlias_Column");
    }
}

/// <summary>Конфігурація <see cref="ColumnRename"/> (<c>cfg.ColumnRename</c>, RPT-STABLE Part B).</summary>
public sealed class ColumnRenameConfiguration : IEntityTypeConfiguration<ColumnRename>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ColumnRename> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ColumnRename", "cfg");
        builder.HasKey(x => new { x.FromColumnDefId, x.ToColumnDefId }).HasName("PK_ColumnRename");
        builder.HasIndex(x => x.ToColumnDefId).HasDatabaseName("IX_ColumnRename_To");
    }
}
