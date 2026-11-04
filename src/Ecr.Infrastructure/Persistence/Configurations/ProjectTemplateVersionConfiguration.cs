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
        builder.HasKey(x => new { x.ProjectId, x.FromPeriodKey }).HasName("PK_ProjectTemplateVersion");

        builder.HasOne<Ecr.Domain.Entities.Documents.Project>()
               .WithMany()
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_ProjectTemplateVersion_Project");

        builder.HasOne<TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => x.TemplateVersionId)
               .HasConstraintName("FK_ProjectTemplateVersion_TemplateVersion");

        builder.HasIndex(x => x.TemplateVersionId).HasDatabaseName("IX_ProjectTemplateVersion_TemplateVersion");
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
