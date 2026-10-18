using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="PermitTypeDef"/> — типів дозволів (PM-02, міграція <c>PRM01_Permits</c>).</summary>
/// <remarks>
/// ⛔ Імена обмежень — за <c>02a-db-schema.md</c>: безіменне обмеження неможливо
/// прибрати скриптом, не з'ясувавши спершу його випадкове ім'я на конкретній базі.
/// </remarks>
public sealed class PermitTypeDefConfiguration : IEntityTypeConfiguration<PermitTypeDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitTypeDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitTypeDef", "cfg", t =>
        {
            t.HasCheckConstraint("CK_PermitType_Category", "Category BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_PermitType_Status", "Status BETWEEN 0 AND 1");

            // Публікація без моменту й автора — стан, якого не має бути ніколи
            // (за зразком CK_TV_Published).
            t.HasCheckConstraint(
                "CK_PermitType_Published",
                "Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL)");
        });
        builder.HasKey(x => x.Id).HasName("PK_PermitTypeDef");
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.LocalizedText(x => x.NameL10n);
        builder.Property(x => x.PublishedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        // ⚠ `ValueGeneratedNever`: значення ЗАВЖДИ надсилається з коду, а DEFAULT
        // лишається для вставок повз EF (EnumDefaultSentinelTests: CLR-замовчування
        // переліку EF інакше вважає «незаданим» і підставляє DEFAULT схеми).
        builder.Property(x => x.Version).HasDefaultValue(1, "DF_PermitType_Ver").ValueGeneratedNever();
        builder.Property(x => x.Status).HasDefaultValueSql("0", "DF_PermitType_Status").ValueGeneratedNever();

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_PermitTypeDef");

        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.HeaderRegistryDefId)
               .HasConstraintName("FK_PermitType_HeaderReg");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.PublishedByUserId)
               .HasConstraintName("FK_PermitType_PublishedBy");
    }
}

/// <summary>Конфігурація <see cref="PermitTypeTable"/> — таблиць типу дозволу.</summary>
public sealed class PermitTypeTableConfiguration : IEntityTypeConfiguration<PermitTypeTable>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitTypeTable> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitTypeTable", "cfg");
        builder.HasKey(x => x.Id).HasName("PK_PermitTypeTable");
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.LocalizedText(x => x.NameL10n);

        builder.HasIndex(x => new { x.PermitTypeDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_PermitTypeTable");

        // Реєстр — таблиця рівно одного типу: інакше правка складу одного типу
        // мовчки змінила б інший.
        builder.HasIndex(x => x.RegistryDefId)
               .IsUnique().HasDatabaseName("UQ_PermitTypeTable_Registry");

        builder.HasOne<PermitTypeDef>().WithMany().HasForeignKey(x => x.PermitTypeDefId)
               .HasConstraintName("FK_PermitTypeTable_Type");
        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_PermitTypeTable_Reg");
    }
}

/// <summary>Конфігурація <see cref="PermitFieldMeta"/> — надбудови дозволу над полем реєстру.</summary>
public sealed class PermitFieldMetaConfiguration : IEntityTypeConfiguration<PermitFieldMeta>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitFieldMeta> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitFieldMeta", "cfg", t =>
        {
            t.HasCheckConstraint("CK_PermitField_Scope", "ListScope IS NULL OR ListScope BETWEEN 0 AND 2");

            // Область переліку узгоджена з посиланнями: 0 і 1 — реєстр, 2 — код
            // сестринської таблиці; без області обидва порожні. Тримає й база —
            // для імпорту й скриптів, повз домен. ⚠ `ListScope IS NOT NULL AND` у 2-й і 3-й
            // гілках обов'язкове: без нього при ListScope = NULL вони дають UNKNOWN, а CHECK
            // пропускає UNKNOWN — і «область порожня, а посилання є» проходило б у базу.
            t.HasCheckConstraint(
                "CK_PermitField_List",
                "(ListScope IS NULL AND ListRegistryDefId IS NULL AND ListSiblingTableCode IS NULL)"
                + " OR (ListScope IS NOT NULL AND ListScope IN (0, 1) AND ListRegistryDefId IS NOT NULL AND ListSiblingTableCode IS NULL)"
                + " OR (ListScope IS NOT NULL AND ListScope = 2 AND ListSiblingTableCode IS NOT NULL AND ListRegistryDefId IS NULL)");

            t.HasCheckConstraint(
                "CK_PermitField_Num",
                "(Precision IS NULL OR Precision BETWEEN 1 AND 38) AND (Scale IS NULL OR (Precision IS NOT NULL AND Scale <= Precision))");
        });

        // Ключ — саме поле реєстру; значення надсилає код, а не база.
        builder.HasKey(x => x.RegistryFieldDefId).HasName("PK_PermitFieldMeta");
        builder.Property(x => x.RegistryFieldDefId).ValueGeneratedNever();
        builder.Property(x => x.GroupCode).HasMaxLength(64);
        builder.Property(x => x.ListSiblingTableCode).HasMaxLength(64);

        // Перелік полів типу — єдиний запит читання (ForeignKeyIndexConvention вимкнено).
        builder.HasIndex(x => x.PermitTypeDefId).HasDatabaseName("IX_PermitFieldMeta_Type");

        builder.HasOne<RegistryFieldDef>().WithOne().HasForeignKey<PermitFieldMeta>(x => x.RegistryFieldDefId)
               .HasConstraintName("FK_PermitField_Field");
        builder.HasOne<PermitTypeDef>().WithMany().HasForeignKey(x => x.PermitTypeDefId)
               .HasConstraintName("FK_PermitField_Type");
        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.ListRegistryDefId)
               .HasConstraintName("FK_PermitField_ListReg");
    }
}

/// <summary>Конфігурація <see cref="PermitSheetBinding"/> — прив'язок таблиць аркуша до дозволів.</summary>
public sealed class PermitSheetBindingConfiguration : IEntityTypeConfiguration<PermitSheetBinding>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitSheetBinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitSheetBinding", "cfg", t =>
        {
            t.HasCheckConstraint("CK_PermitBinding_Mode", "Mode BETWEEN 0 AND 1");
            t.HasCheckConstraint("CK_PermitBinding_Rule", "SelectionRule BETWEEN 0 AND 1");

            // Правило «з шапки» потребує поля шапки; при іншому правилі поле шапки
            // нікого не читає.
            t.HasCheckConstraint(
                "CK_PermitBinding_Header",
                "(SelectionRule = 1 AND HeaderFieldDefId IS NOT NULL) OR (SelectionRule <> 1 AND HeaderFieldDefId IS NULL)");
        });
        builder.HasKey(x => x.Id).HasName("PK_PermitSheetBinding");
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_PermitBinding_Act").ValueGeneratedNever();

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_PermitSheetBinding_Code");

        // Таблиця аркуша в версії шаблону — не більше ніж у одній прив'язці.
        builder.HasIndex(x => new { x.TemplateVersionId, x.SheetTableDefId })
               .IsUnique().HasDatabaseName("UQ_PermitSheetBinding_Sheet");
        builder.HasIndex(x => x.PermitTypeTableId).HasDatabaseName("IX_PermitSheetBinding_Table");

        builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.TemplateVersionId)
               .HasConstraintName("FK_PermitBinding_TV");
        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.SheetTableDefId)
               .HasConstraintName("FK_PermitBinding_Sheet");
        builder.HasOne<PermitTypeDef>().WithMany().HasForeignKey(x => x.PermitTypeDefId)
               .HasConstraintName("FK_PermitBinding_Type");
        builder.HasOne<PermitTypeTable>().WithMany().HasForeignKey(x => x.PermitTypeTableId)
               .HasConstraintName("FK_PermitBinding_Table");
        builder.HasOne<HeaderFieldDef>().WithMany().HasForeignKey(x => x.HeaderFieldDefId)
               .HasConstraintName("FK_PermitBinding_Header");

        // Каскаду немає: усі FK — Restrict (EcrDbContext.OnModelCreating, ФВ-7.6).
        // Обробник видалення прив'язки спершу явно видаляє її колонки.
        builder.HasMany(x => x.Columns).WithOne().HasForeignKey(c => c.BindingId)
               .HasConstraintName("FK_PermitBindCol_Binding");
        builder.Navigation(x => x.Columns).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="PermitSheetBindingColumn"/> — колонок прив'язки.</summary>
public sealed class PermitSheetBindingColumnConfiguration : IEntityTypeConfiguration<PermitSheetBindingColumn>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitSheetBindingColumn> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitSheetBindingColumn", "cfg",
            t => t.HasCheckConstraint("CK_PermitBindCol_Role", "Role BETWEEN 0 AND 2"));
        builder.HasKey(x => new { x.BindingId, x.PermitFieldCode }).HasName("PK_PermitSheetBindingColumn");
        builder.Property(x => x.PermitFieldCode).HasMaxLength(64).IsRequired();

        // Одна колонка аркуша — одна роль у прив'язці.
        builder.HasIndex(x => new { x.BindingId, x.SheetColumnDefId })
               .IsUnique().HasDatabaseName("UQ_PermitBindCol_Column");

        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.SheetColumnDefId)
               .HasConstraintName("FK_PermitBindCol_Column");
    }
}
