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
