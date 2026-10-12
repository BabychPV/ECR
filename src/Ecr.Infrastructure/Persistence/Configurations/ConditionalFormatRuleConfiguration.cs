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
                "Operator IN (N'gt', N'ge', N'lt', N'le', N'eq', N'ne', N'between', N'empty', N'notEmpty', N'textEq', N'textNe')");
            t.HasCheckConstraint("CK_CondFmt_Scope", "Scope IN (N'column', N'row')");
            t.HasCheckConstraint("CK_CondFmt_Pattern", "Pattern IS NULL OR Pattern = N'hatch'");
            // Область «рядки» без таблиці й ключів безглузда; ключі без області «рядки» — теж.
            t.HasCheckConstraint(
                "CK_CondFmt_RowScope",
                "(Scope = N'row' AND TableCode IS NOT NULL AND RowKeys IS NOT NULL) "
                + "OR (Scope = N'column' AND RowKeys IS NULL)");
            t.HasCheckConstraint("CK_CondFmt_AnyColumn", "ColumnCode <> N'*' OR TableCode IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ColumnCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Operator).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Value).HasMaxLength(ConditionalFormatRule.MaxOperandLength);
        builder.Property(x => x.ValueTo).HasMaxLength(ConditionalFormatRule.MaxOperandLength);
        builder.Property(x => x.BackgroundHex).HasMaxLength(7).IsUnicode(false);
        builder.Property(x => x.ForegroundHex).HasMaxLength(7).IsUnicode(false);
        builder.Property(x => x.IsBold).HasDefaultValue(false, "DF_CondFmt_Bold");

        // CELL-STYLE L6: область правила. Старі рядки отримують 'column' і NULL — поведінка не змінюється.
        builder.Property(x => x.Scope).HasMaxLength(8).IsUnicode(false).IsRequired()
               .HasDefaultValue(ConditionalFormatRule.ScopeColumn, "DF_CondFmt_Scope");
        builder.Property(x => x.TableCode).HasMaxLength(ConditionalFormatRule.MaxCodeLength);
        builder.Property(x => x.RefColumnCode).HasMaxLength(ConditionalFormatRule.MaxCodeLength);
        builder.Property(x => x.Pattern).HasMaxLength(16).IsUnicode(false);
        // Ключі рядків — JSON-масив тексту (до 500 ключів по 64 символи).
        builder.Property(x => x.RowKeys)
               .HasConversion(
                   keys => keys == null ? null : System.Text.Json.JsonSerializer.Serialize(keys, (System.Text.Json.JsonSerializerOptions?)null),
                   json => json == null
                       ? null
                       : (IReadOnlyList<string>?)System.Text.Json.JsonSerializer.Deserialize<List<string>>(json, (System.Text.Json.JsonSerializerOptions?)null),
                   new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<string>?>(
                       (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                       a => a == null ? 0 : a.Aggregate(0, (h, k) => HashCode.Combine(h, k)),
                       a => a == null ? null : a.ToList()))
               .HasColumnType("nvarchar(max)");
        builder.HasIndex(x => new { x.TemplateVersionId, x.TableCode }).HasDatabaseName("IX_CondFmt_Table");
        builder.HasIndex(x => new { x.TemplateVersionId, x.ColumnCode, x.Ordinal })
               .IsUnique().HasDatabaseName("UQ_CondFmt_Order");
        builder.HasOne<TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => x.TemplateVersionId)
               .HasConstraintName("FK_CondFmt_TV");
    }
}