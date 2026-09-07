using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="RegistryEntry"/> — записів довідників.
/// </summary>
/// <remarks>
/// ⚠ Сутність повертається в модель на Етапі 4 разом із трьома сусідніми
/// (`Q-027`, `dic`-частина). До цього була вилучена через <c>Ordinal</c>,
/// якого не було в сутності, а в схемі він <c>NOT NULL</c>.
/// <para>
/// <c>Id</c> у сутності — <c>long</c>, у схемі — <c>int IDENTITY</c>.
/// Розбіжність знята **конверсією значення**, а не зміною жодного з двох
/// контрактів: збільшувати колонку до <c>bigint</c> не можна (це схема), а
/// звужувати <c>Id</c> до <c>int</c> — теж (це сигнатури
/// <c>IOrphanScanner.RescanForEntryAsync</c> і <c>RegistryEntryDto</c>).
/// Діапазону <c>int</c> вистачає з запасом: записів довідника — десятки тисяч.
/// </para>
/// </remarks>
public sealed class RegistryEntryConfiguration : IEntityTypeConfiguration<RegistryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⛔ Строге `<`, а не `<=`: межа ВИКЛЮЧНА (`[ValidFrom, ValidTo)`,
        // крок I.10). `ValidFrom = ValidTo` — це вікно з нуля днів, тобто
        // запис, якого ніколи не видно в списку; ловити його треба базою, а не
        // на екрані, бо масова вставка імпортера повз домен не проходить.
        builder.ToTable("RegistryEntry", "dic", t => t.HasCheckConstraint(
            "CK_RegEntry_Period",
            "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom < ValidTo"));

        builder.HasKey(x => x.Id).HasName("PK_RegistryEntry");
        builder.Property(x => x.Id).HasConversion<int>().ValueGeneratedOnAdd();

        builder.Property(x => x.Code).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ValidFrom).HasColumnType("date");
        builder.Property(x => x.ValidTo).HasColumnType("date");
        builder.Property(x => x.Ordinal).HasDefaultValue(0);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.IsDeleted).HasDefaultValue(false);
        builder.Property(x => x.ParentEntryId).HasConversion<int?>();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.RegistryDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_RegistryEntry");

        // ⚠ Індекс покриває саме те, що читає резолвінг: усі записи довідника
        // з вікном чинності. Без INCLUDE кожен рядок списку коштував би
        // звернення до купи, а список відкривається на кожну комірку-lookup.
        builder.HasIndex(x => new { x.RegistryDefId, x.IsActive, x.IsDeleted })
               .HasDatabaseName("IX_RegistryEntry_Lookup")
               .IncludeProperties(x => new { x.Code, x.Ordinal, x.ValidFrom, x.ValidTo });

        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegEntry_Def");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.ParentEntryId)
               .HasConstraintName("FK_RegEntry_Parent");
    }
}

/// <summary>Конфігурація <see cref="RegistryValue"/> — значень полів запису.</summary>
/// <remarks>
/// Імена колонок відрізняються від імен властивостей рівно там, де їх
/// приведено до схеми (<c>ValueNumeric</c>, <c>ValueRefEntryId</c>,
/// <c>ValueUnitId</c>); властивості сутності вже названі так само, тож
/// <c>HasColumnName</c> тут не потрібен.
/// </remarks>
public sealed class RegistryValueConfiguration : IEntityTypeConfiguration<RegistryValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryValue", "dic");
        builder.HasKey(x => x.Id).HasName("PK_RegistryValue");

        builder.Property(x => x.RegistryEntryId).HasConversion<int>();
        builder.Property(x => x.ValueString).HasMaxLength(1000);

        // decimal(28,10) — та сама точність, що в комірках. Інша тут означала б,
        // що ліміт дозволу і виміряне значення округляються по-різному, і
        // порівняння «перевищено чи ні» залежало б від того, звідки взяли число.
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(28,10)");
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");
        builder.Property(x => x.ValueRefEntryId).HasConversion<int?>();

        builder.HasIndex(x => new { x.RegistryEntryId, x.RegistryFieldDefId })
               .IsUnique().HasDatabaseName("UQ_RegistryValue");

        builder.HasOne(x => x.Entry).WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_RegValue_Entry");
        builder.HasOne<RegistryFieldDef>().WithMany().HasForeignKey(x => x.RegistryFieldDefId)
               .HasConstraintName("FK_RegValue_Field");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.ValueRefEntryId)
               .HasConstraintName("FK_RegValue_Ref");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.ValueUnitId)
               .HasConstraintName("FK_RegValue_Unit");
    }
}

/// <summary>Конфігурація <see cref="RegistryEntryLink"/> — каскадів і M:N.</summary>
public sealed class RegistryEntryLinkConfiguration : IEntityTypeConfiguration<RegistryEntryLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryEntryLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryEntryLink", "dic");
        builder.HasKey(x => x.Id).HasName("PK_RegistryEntryLink");

        builder.Property(x => x.LeftEntryId).HasConversion<int>();
        builder.Property(x => x.RightEntryId).HasConversion<int>();
        builder.Property(x => x.LinkKind).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => new { x.LeftEntryId, x.RightEntryId, x.LinkKind })
               .IsUnique().HasDatabaseName("UQ_RegistryEntryLink");

        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.LeftEntryId)
               .HasConstraintName("FK_RegLink_Left");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.RightEntryId)
               .HasConstraintName("FK_RegLink_Right");
    }
}

/// <summary>Конфігурація <see cref="RegistryExternalKey"/> — зовнішніх ідентифікаторів.</summary>
public sealed class RegistryExternalKeyConfiguration : IEntityTypeConfiguration<RegistryExternalKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryExternalKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryExternalKey", "dic");
        builder.HasKey(x => x.Id).HasName("PK_RegistryExternalKey");

        builder.Property(x => x.RegistryEntryId).HasConversion<int>();
        builder.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ExternalPath).HasMaxLength(400);
        builder.Property(x => x.LastSyncedAt).HasColumnType("datetime2(3)");

        // Унікальність за (джерело, зовнішній Id), а не за записом: один запис
        // довідника легально має ключі в кількох системах, але той самий GUID
        // у тій самій системі не може вказувати на два різні записи.
        builder.HasIndex(x => new { x.DataSourceId, x.ExternalId })
               .IsUnique().HasDatabaseName("UQ_RegistryExternalKey");

        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_RegExtKey_Entry");
    }
}
