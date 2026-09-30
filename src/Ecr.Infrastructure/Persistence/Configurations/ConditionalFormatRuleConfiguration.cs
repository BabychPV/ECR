using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="ConditionalFormatRule"/> (ФВ-2.6/2.7).</summary>
/// <remarks>
/// ⚠ Таблиця має тригер незмінності (заморожена версія) — <c>HasTrigger</c>
/// обов'язковий, сам тригер — у <c>Sql/10-triggers.sql</c>.
/// </remarks>
public sealed class ConditionalFormatRuleConfiguration : IEntityTypeConfiguration<ConditionalFormatRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConditionalFormatRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ConditionalFormatRule", "cfg", t =>
        {
            t.HasTrigger("TR_ConditionalFormatRule_Immutable");
            t.HasCheckConstraint(
                "CK_CondFmt_Operator",
                "Operator IN (N'gt', N'ge', N'lt', N'le', N'eq', N'ne', N'between', N'empty', N'notEmpty')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ColumnCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Operator).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Value).HasMaxLength(ConditionalFormatRule.MaxOperandLength);
        builder.Property(x => x.ValueTo).HasMaxLength(ConditionalFormatRule.MaxOperandLength);
        builder.Property(x => x.BackgroundHex).HasMaxLength(7).IsUnicode(false);
        builder.Property(x => x.ForegroundHex).HasMaxLength(7).IsUnicode(false);
        builder.Property(x => x.IsBold).HasDefaultValue(false, "DF_CondFmt_Bold");
        builder.HasIndex(x => new { x.TemplateVersionId, x.ColumnCode, x.Ordinal })
               .IsUnique().HasDatabaseName("UQ_CondFmt_Order");
        builder.HasOne<TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => x.TemplateVersionId)
               .HasConstraintName("FK_CondFmt_TV");
    }
}