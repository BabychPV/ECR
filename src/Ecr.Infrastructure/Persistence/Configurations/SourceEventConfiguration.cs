using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="SourceEventMap"/> — мапінгу «шаблон подій → динамічна
/// таблиця документа» (FEATURE-HSE301-VIEW §4.7.3, міграція <c>HSE301M5SourceEvents</c>).
/// </summary>
public sealed class SourceEventMapConfiguration : IEntityTypeConfiguration<SourceEventMap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEventMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⚠ Кожне обмеження — окремим викликом ToTable (див. EntityFieldMapConfiguration).
        builder.ToTable("SourceEventMap", "ext", t => t.HasCheckConstraint(
            "CK_SEM_VolumeMode",
            // Перелік закритий: режим, якого не знає синхронізація, дав би
            // відмову під час прогону, а не під час налаштування.
            "VolumeMode BETWEEN 0 AND 2"));
        builder.ToTable("SourceEventMap", "ext", t => t.HasCheckConstraint(
            "CK_SEM_Filter",
            // Звуження — три значення разом або жодного (SourceEventMap.SetFilter).
            "(FilterAttribute IS NULL AND FilterScope IS NULL AND FilterValue IS NULL) "
            + "OR (FilterAttribute IS NOT NULL AND FilterScope BETWEEN 0 AND 1 AND FilterValue IS NOT NULL)"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.FilterAttribute).HasMaxLength(SourceEventMap.MaxAttributeLength);
        builder.Property(x => x.FilterValue).HasMaxLength(SourceEventMap.MaxValueLength);

        // ⚠ HasDefaultValue + ValueGeneratedNever — як у RowWindowMapConfiguration:
        // явно заданий `IsActive = false` EF інакше не надіслав би (`EnumDefaultSentinelTests`).
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_SEM_Act").ValueGeneratedNever();
        builder.Property(x => x.RowVersion).IsRowVersion();

        // ⛔ Один мапінг на «шаблон × документ × таблиця»: два мапінги того самого
        // шаблону в ту саму таблицю писали б кожну подію двічі.
        builder.HasIndex(x => new { x.SourceEntityId, x.DocumentId, x.TableDefId })
               .IsUnique().HasDatabaseName("UQ_SourceEventMap");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_SEM_Entity");
        builder.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_SEM_Document");
        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_SEM_Table");

        builder.HasMany(x => x.Fields).WithOne().HasForeignKey(f => f.SourceEventMapId)
               .HasConstraintName("FK_SEFM_Map");
        builder.Navigation(x => x.Fields).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="SourceEventFieldMap"/> — поля «атрибут → колонка».</summary>
public sealed class SourceEventFieldMapConfiguration : IEntityTypeConfiguration<SourceEventFieldMap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEventFieldMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceEventFieldMap", "ext", t => t.HasCheckConstraint(
            "CK_SEFM_Kinds",
            "AttributeScope BETWEEN 0 AND 1 AND ValueKind BETWEEN 0 AND 3"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceAttribute).HasMaxLength(SourceEventMap.MaxAttributeLength).IsRequired();

        // ⛔ Одне поле на колонку: два атрибути в ту саму комірку дали б значення,
        // яке залежить від порядку обходу.
        builder.HasIndex(x => new { x.SourceEventMapId, x.TargetColumnDefId })
               .IsUnique().HasDatabaseName("UQ_SEFM_Target");

        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.TargetColumnDefId)
               .HasConstraintName("FK_SEFM_Column");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.SourceUnitId)
               .HasConstraintName("FK_SEFM_SourceUnit");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.TargetUnitId)
               .HasConstraintName("FK_SEFM_TargetUnit");

        builder.HasMany(x => x.Values).WithOne().HasForeignKey(v => v.SourceEventFieldMapId)
               .HasConstraintName("FK_SEVM_Field");
        builder.Navigation(x => x.Values).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="SourceEventValueMap"/> — явних відповідностей значень.</summary>
public sealed class SourceEventValueMapConfiguration : IEntityTypeConfiguration<SourceEventValueMap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEventValueMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceEventValueMap", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceValue).HasMaxLength(SourceEventMap.MaxValueLength).IsRequired();

        // Домен — long, dic.RegistryEntry.Id у базі — int (RegistryEntryConfiguration).
        builder.Property(x => x.RegistryEntryId).HasConversion<int>();

        // Зіставлення бази _CI_: «зима» і «ЗИМА» — одне значення, як і в домені.
        builder.HasIndex(x => new { x.SourceEventFieldMapId, x.SourceValue })
               .IsUnique().HasDatabaseName("UQ_SEVM_Value");

        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_SEVM_Entry");
    }
}

/// <summary>
/// Конфігурація <see cref="SourceEventLink"/> — зв'язку «подія джерела ↔ рядок».
/// </summary>
/// <remarks>
/// ⚠ Зовнішнього ключа на <c>doc.TableInstance</c> немає навмисно — див.
/// <see cref="SourceEventLink"/> (архівація звільняє партиції через <c>TRUNCATE</c>).
/// </remarks>
public sealed class SourceEventLinkConfiguration : IEntityTypeConfiguration<SourceEventLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEventLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceEventLink", "ext", t => t.HasCheckConstraint(
            "CK_SEL_Status",
            "Status IN (N'Synced', N'Open', N'Missing', N'PeriodClosed', N'PeriodChanged', "
            + "N'PeriodNotOpen', N'Unmapped', N'RowLimit')"));
        builder.ToTable("SourceEventLink", "ext", t => t.HasCheckConstraint(
            "CK_SEL_Row",
            // Рядок — три значення разом або жодного.
            "(PeriodKey IS NULL AND TableInstanceId IS NULL AND RowKey IS NULL) "
            + "OR (PeriodKey IS NOT NULL AND TableInstanceId IS NOT NULL AND RowKey IS NOT NULL)"));
        builder.ToTable("SourceEventLink", "ext", t => t.HasCheckConstraint(
            "CK_SEL_StatusRow",
            // Те саме правило, що SourceEventLink.IsTransitionAllowed: стани «рядок є»
            // без рядка і стани «рядка ще немає» з рядком не бувають.
            "(Status NOT IN (N'Synced', N'Unmapped', N'Missing', N'PeriodChanged') OR TableInstanceId IS NOT NULL) "
            + "AND (Status NOT IN (N'Open', N'PeriodNotOpen', N'RowLimit') OR TableInstanceId IS NULL)"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceEventId).HasMaxLength(SourceEventLink.MaxSourceEventIdLength).IsRequired();
        builder.Property(x => x.RowKey).HasMaxLength(SourceEventLink.MaxRowKeyLength);
        builder.Property(x => x.EventName).HasMaxLength(SourceEventLink.MaxEventNameLength);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.Ignore(x => x.HasRow);

        // ⛔ Одна подія джерела — один зв'язок у межах мапінгу: повтор синхронізації
        // не дублює ні зв'язку, ні рядка (§4.7.4, крок 2).
        builder.HasIndex(x => new { x.SourceEventMapId, x.SourceEventId })
               .IsUnique().HasDatabaseName("UQ_SEL_Event");

        // Реєстр подій (§10.4): «з якої події цей рядок» — за рядком.
        builder.HasIndex(x => new { x.TableInstanceId, x.RowKey })
               .HasDatabaseName("IX_SEL_Row");

        builder.HasOne<SourceEventMap>().WithMany().HasForeignKey(x => x.SourceEventMapId)
               .HasConstraintName("FK_SEL_Map");
    }
}
