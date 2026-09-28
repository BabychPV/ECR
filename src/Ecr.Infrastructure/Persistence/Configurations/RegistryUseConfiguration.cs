using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="RegistryUse"/> — ребер «хто використовує довідник»
/// (RT-05, FEATURE-REGISTRY-TABLES §3.2, міграція <c>RK04RegistryUseAndRunAsOf</c>).
/// </summary>
/// <remarks>
/// ⛔ Імена обмежень та індексів — за <c>02a-db-schema.md</c>: безіменне
/// обмеження неможливо прибрати скриптом, не з'ясувавши спершу його випадкове
/// ім'я на конкретній базі.
/// </remarks>
public sealed class RegistryUseConfiguration : IEntityTypeConfiguration<RegistryUse>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryUse> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⛔ Вид закритий на рівні бази: ребро невідомого виду не прочитав би
        // жоден споживач, і довідник виглядав би невикористаним.
        builder.ToTable("RegistryUse", "cfg", t => t.HasCheckConstraint(
            "CK_RegUse_Kind",
            $"[SourceKind] BETWEEN {RegistryUse.TemplateFormulaSource} AND {RegistryUse.RegistryRuleSource}"));
        builder.HasKey(x => x.Id).HasName("PK_RegistryUse");
        builder.Property(x => x.FormulaCode).HasMaxLength(RegistryUse.FormulaCodeMaxLength);
        builder.Property(x => x.FieldPath).HasMaxLength(RegistryUse.FieldPathMaxLength);

        // «Де використано» (RT-19) і завантажувач знімка (RT-22) читають за
        // довідником: INCLUDE дає відповідь без звернення до кластерного ключа.
        builder.HasIndex(x => x.RegistryDefId)
               .HasDatabaseName("IX_RegistryUse_Registry")
               .IncludeProperties(x => new { x.SourceKind, x.SourceId, x.FormulaCode, x.FieldPath });

        // Публікація джерела (RT-23b) переписує його ребра: видалення за джерелом.
        builder.HasIndex(x => new { x.SourceKind, x.SourceId })
               .HasDatabaseName("IX_RegistryUse_Source");

        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegUse_Def");
    }
}
