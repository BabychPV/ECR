using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація версії шаблону.</summary>
public sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TemplateVersion", "cfg", t => t.HasCheckConstraint(
            "CK_TV_Published",
            "Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL)"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Version).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.SourceWorkbookHash).HasColumnType("varbinary(32)");

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.PresentationRevision).HasDefaultValue(0, "DF_TV_PresRev");

        // Версія унікальна в межах шаблону: «1.0.0.0» двічі означало б, що
        // посилання на версію перестало бути однозначним.
        builder.HasIndex(x => new { x.TemplateId, x.Version })
               .IsUnique()
               .HasDatabaseName("UQ_TemplateVersion");

        // ⚠ .WithMany(t => t.Versions), а НЕ .WithMany(). Без явної
        // навігації EF бачить Template.Versions як окремий зв'язок і додає
        // до таблиці тіньову колонку TemplateId1 разом з індексом на неї.
        builder.HasOne<Template>()
               .WithMany(t => t.Versions)
               .HasForeignKey(x => x.TemplateId)
               .HasConstraintName("FK_TemplateVersion_Template");

        // Навігація на аркуші — через приватне поле: колекція не має
        // редагуватися ззовні агрегата.
        builder.HasMany(x => x.Sheets)
               .WithOne()
               .HasForeignKey(s => s.TemplateVersionId)
               .HasConstraintName("FK_SheetDef_Version");
        builder.Navigation(x => x.Sheets).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(x => x.IsStructurallyFrozen);
        builder.Ignore(x => x.CacheKey);
    }
}
