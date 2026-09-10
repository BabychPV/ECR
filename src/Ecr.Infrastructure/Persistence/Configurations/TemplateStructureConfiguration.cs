using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="Template"/>.</summary>
public sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Template", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Template_Code");
        builder.Property(x => x.TagsJson).HasMaxLength(1000);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_Template_Active");
        builder.LocalizedText(x => x.NameL10n);
        builder.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="SheetDef"/>.</summary>
public sealed class SheetDefConfiguration : IEntityTypeConfiguration<SheetDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SheetDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SheetDef", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SheetGroup).HasMaxLength(64);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsMandatory).HasDefaultValue(false, "DF_SheetDef_Mand");
        builder.Property(x => x.IsVisible).HasDefaultValue(true, "DF_SheetDef_Vis");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_SheetDef_Del");
        builder.LocalizedText(x => x.NameL10n);

        builder.HasIndex(x => new { x.TemplateVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_SheetDef");

        builder.HasMany(x => x.Tables)
               .WithOne()
               .HasForeignKey(t => t.SheetDefId)
               .HasConstraintName("FK_TableDef_Sheet");
        builder.Navigation(x => x.Tables).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="TableDef"/>.</summary>
public sealed class TableDefConfiguration : IEntityTypeConfiguration<TableDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableDef", "cfg", t => t.HasCheckConstraint(
            "CK_TableDef_MaxRows",
            "RowMode = 0 OR MaxDynamicRows IS NULL OR MaxDynamicRows > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LayoutKind).HasConversion<byte>();
        builder.Property(x => x.RowMode).HasConversion<byte>();
        builder.Property(x => x.StorageMode).HasConversion<byte>();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.StorageMode).HasDefaultValueSql("0", "DF_TableDef_Storage");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_TableDef_Del");
        builder.LocalizedText(x => x.NameL10n);

        builder.HasIndex(x => new { x.SheetDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_TableDef");

        builder.HasMany(x => x.Columns)
               .WithOne()
               .HasForeignKey(c => c.TableDefId)
               .HasConstraintName("FK_ColumnDef_Table");
        builder.Navigation(x => x.Columns).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.Rows)
               .WithOne()
               .HasForeignKey(r => r.TableDefId)
               .HasConstraintName("FK_RowDef_Table");
        builder.Navigation(x => x.Rows).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="ColumnDef"/>.</summary>
/// <remarks>
/// ⚠ Тут оголошений <b>альтернативний ключ</b> <c>(TableDefId, Id)</c>. Без
/// нього EF не побудує складений FK із <c>doc.CellValue</c>, який і не дає
/// комірці потрапити в колонку чужої таблиці (ТЗ §13.5 п.3).
///
/// ⚠ Таблиця має тригер незмінності, тому <c>HasTrigger</c> обов'язковий: EF
/// Core 7+ використовує <c>OUTPUT</c> при <c>SaveChanges</c> і на таблиці з
/// тригером без цього оголошення падає в рантаймі.
/// </remarks>
public sealed class ColumnDefConfiguration : IEntityTypeConfiguration<ColumnDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ColumnDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ColumnDef", "cfg", t =>
        {
            t.HasTrigger("TR_ColumnDef_Immutable");
            t.HasCheckConstraint("CK_ColumnDef_Month", "IsMonthColumn = 0 OR MonthNumber BETWEEN 1 AND 12");
            t.HasCheckConstraint("CK_ColumnDef_Lookup", "DataType <> 5 OR LookupRegistryDefId IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.TableDefId, x.Id }).HasName("UQ_ColumnDef_ForFk");

        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DataType).HasConversion<byte>();
        builder.Property(x => x.DefaultValue).HasMaxLength(400);
        builder.Property(x => x.DisplayFormat).HasMaxLength(50);
        builder.Property(x => x.LookupFilter).HasMaxLength(500);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsReadOnly).HasDefaultValue(false, "DF_ColumnDef_RO");
        builder.Property(x => x.IsRequired).HasDefaultValue(false, "DF_ColumnDef_Req");
        builder.Property(x => x.IsHidden).HasDefaultValue(false, "DF_ColumnDef_Hid");
        builder.Property(x => x.IsMonthColumn).HasDefaultValue(false, "DF_ColumnDef_Mon");
        builder.Property(x => x.IsBusinessKey).HasDefaultValue(false, "DF_ColumnDef_BK");
        builder.Property(x => x.IsScopeField).HasDefaultValue(false, "DF_ColumnDef_Sc");
        builder.Property(x => x.IsIndexed).HasDefaultValue(false, "DF_ColumnDef_Ix");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_ColumnDef_Del");
        builder.LocalizedText(x => x.HeaderL10n);
        builder.Ignore(x => x.IsComputed);

        builder.HasIndex(x => new { x.TableDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_ColumnDef");

        // ⛔ Q-222: був у 02a-db-schema.md (FK_ColumnDef_Cascade), ніколи не
        // потрапив у цю конфігурацію.
        builder.HasOne<ColumnDef>()
               .WithMany()
               .HasForeignKey(x => x.CascadeFromColumnId)
               .HasConstraintName("FK_ColumnDef_Cascade");
    }
}

/// <summary>Конфігурація <see cref="RowDef"/>.</summary>
public sealed class RowDefConfiguration : IEntityTypeConfiguration<RowDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RowDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RowDef", "cfg", t => t.HasTrigger("TR_RowDef_Immutable"));
        builder.HasKey(x => x.Id);

        // RowKeyValue ↔ колонка RowKey: властивість несе обгортку значеннєвого
        // типу, колонка — саме значення.
        builder.Property(x => x.RowKeyValue).HasColumnName("RowKey").HasMaxLength(100).IsRequired();
        builder.Property(x => x.RowKind).HasConversion<byte>();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsReadOnly).HasDefaultValue(false, "DF_RowDef_RO");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_RowDef_Del");
        builder.LocalizedText(x => x.LabelL10n);

        builder.HasIndex(x => new { x.TableDefId, x.RowKeyValue })
               .IsUnique().HasDatabaseName("UQ_RowDef");

        // ⛔ Q-222: був у 02a-db-schema.md (FK_RowDef_Parent), ніколи не
        // потрапив у цю конфігурацію.
        builder.HasOne<RowDef>()
               .WithMany()
               .HasForeignKey(x => x.ParentRowDefId)
               .HasConstraintName("FK_RowDef_Parent");
    }
}

/// <summary>Конфігурація <see cref="StyleDef"/>.</summary>
public sealed class StyleDefConfiguration : IEntityTypeConfiguration<StyleDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StyleDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("StyleDef", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FontName).HasMaxLength(64);
        builder.Property(x => x.FontSize).HasPrecision(4, 1);
        builder.Property(x => x.NumberFormat).HasMaxLength(50);
        builder.Property(x => x.BorderJson).HasMaxLength(500);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsBold).HasDefaultValue(false, "DF_Style_Bold");
        builder.Property(x => x.IsItalic).HasDefaultValue(false, "DF_Style_Ital");
        builder.Property(x => x.WrapText).HasDefaultValue(false, "DF_Style_Wrap");
        builder.HasIndex(x => new { x.TemplateVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_StyleDef");

        // ⛔ Q-222: був у 02a-db-schema.md (FK_StyleDef_TV), ніколи не
        // потрапив у цю конфігурацію.
        builder.HasOne<Domain.Entities.Configuration.TemplateVersion>()
               .WithMany()
               .HasForeignKey(x => x.TemplateVersionId)
               .HasConstraintName("FK_StyleDef_TV");
    }
}
