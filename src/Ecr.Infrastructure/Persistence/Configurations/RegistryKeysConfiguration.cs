using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="RegistryKeyDef"/> — складених ключів довідника (RT-01).</summary>
/// <remarks>
/// ⛔ Імена DEFAULT, індексів і FK — за <c>02a-db-schema.md</c> §3: безіменне
/// обмеження неможливо прибрати скриптом, не з'ясувавши спершу його випадкове
/// ім'я на конкретній базі.
/// </remarks>
public sealed class RegistryKeyDefConfiguration : IEntityTypeConfiguration<RegistryKeyDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryKeyDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryKeyDef", "cfg");
        builder.HasKey(x => x.Id).HasName("PK_RegistryKeyDef");
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").IsRequired();

        // ⚠ `ValueGeneratedNever` на кожному bool із DEFAULT: значення ЗАВЖДИ
        // надсилається з коду, а DEFAULT лишається для вставок повз EF. Чесно:
        // на EF 10 `false` доходить до бази і без нього — мутацією перевірено,
        // `Ключ_зберігається_з_частинами_в_порядку_і_прапорцями_як_задано`
        // лишається зеленим. Але це правило відправки EF, а не контракт, і для
        // переліків із DEFAULT воно вже підводило (`EnumDefaultSentinelTests`):
        // `IgnoreCase = false`, мовчки замінений на `1`, дав би ключ, який
        // перевіряє інакше, ніж заявлено. Тест тримає саме збережене значення.
        builder.Property(x => x.IsPrimary).HasDefaultValue(false, "DF_RegKey_Pri").ValueGeneratedNever();
        builder.Property(x => x.IgnoreCase).HasDefaultValue(true, "DF_RegKey_Case").ValueGeneratedNever();
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_RegKey_Act").ValueGeneratedNever();

        builder.HasIndex(x => new { x.RegistryDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_RegistryKeyDef");

        // ⛔ Первинний ключ — рівно один АКТИВНИЙ на довідник. Гарантія бази, а
        // не перевірка обробника: опис змінюють і чернетка, і публікація, і
        // скрипт міграції.
        builder.HasIndex(x => x.RegistryDefId)
               .IsUnique()
               .HasFilter("[IsPrimary] = 1 AND [IsActive] = 1")
               .HasDatabaseName("UX_RegistryKeyDef_Primary");

        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegKey_Def");

        builder.HasMany(x => x.Fields).WithOne().HasForeignKey(f => f.RegistryKeyDefId)
               .HasConstraintName("FK_RegKeyField_Key");
        builder.Navigation(x => x.Fields).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="RegistryKeyField"/> — частин ключа.</summary>
public sealed class RegistryKeyFieldConfiguration : IEntityTypeConfiguration<RegistryKeyField>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryKeyField> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryKeyField", "cfg");
        builder.HasKey(x => new { x.RegistryKeyDefId, x.Ordinal }).HasName("PK_RegistryKeyField");

        // Позицію задає ключ (1…8), а не база.
        builder.Property(x => x.Ordinal).ValueGeneratedNever();

        // Одне поле — одна частина: ключ (A, A) дав би той самий хеш, що й (A),
        // лише довший.
        builder.HasIndex(x => new { x.RegistryKeyDefId, x.RegistryFieldDefId })
               .IsUnique().HasDatabaseName("UQ_RegistryKeyField_Field");

        builder.HasOne<RegistryFieldDef>().WithMany().HasForeignKey(x => x.RegistryFieldDefId)
               .HasConstraintName("FK_RegKeyField_Field");
    }
}

/// <summary>Конфігурація <see cref="RegistryEntryKey"/> — похідних рядків унікальності.</summary>
/// <remarks>
/// <c>RegistryEntryId</c> у сутності — <c>long</c>, у схемі — <c>int</c>: та сама
/// конверсія значення, що й у решти <c>dic.*</c> (див.
/// <c>RegistryEntryConfiguration</c>).
/// </remarks>
public sealed class RegistryEntryKeyConfiguration : IEntityTypeConfiguration<RegistryEntryKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryEntryKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryEntryKey", "dic");
        builder.HasKey(x => x.Id).HasName("PK_RegistryEntryKey");

        builder.Property(x => x.RegistryEntryId).HasConversion<int>();
        builder.Property(x => x.KeyHash).HasMaxLength(RegistryEntryKey.KeyHashLength).IsFixedLength().IsRequired();
        builder.Property(x => x.KeyText).HasMaxLength(RegistryEntryKey.MaxKeyTextLength).IsRequired();
        builder.Property(x => x.ValidFromKey).HasColumnType("date");
        builder.Property(x => x.ValidTo).HasColumnType("date");

        // Один рядок на пару (ключ, запис): ключ запису перераховується на місці.
        builder.HasIndex(x => new { x.RegistryKeyDefId, x.RegistryEntryId })
               .IsUnique().HasDatabaseName("UQ_RegistryEntryKey_Entry");

        // ⛔ Гарантія бази (§4.3, крок 5): гонку, яку не закрило блокування
        // служби ключів, ловить саме цей індекс. Фільтр `IsLive = 1` —
        // видалений запис ключ звільняє; без фільтра видалення блокувало б
        // ключ назавжди.
        builder.HasIndex(x => new { x.RegistryKeyDefId, x.KeyHash, x.ValidFromKey })
               .IsUnique()
               .HasFilter("[IsLive] = 1")
               .HasDatabaseName("UX_RegistryEntryKey_Live");

        // Пошук і діапазонне блокування при перевірці перетину вікон (§4.4).
        builder.HasIndex(x => new { x.RegistryKeyDefId, x.KeyHash })
               .HasDatabaseName("IX_RegistryEntryKey_Hash")
               .IncludeProperties(x => new { x.RegistryEntryId, x.ValidFromKey, x.ValidTo, x.IsLive });

        builder.HasOne(x => x.Entry).WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_RegEntryKey_Entry");
        builder.HasOne<RegistryKeyDef>().WithMany().HasForeignKey(x => x.RegistryKeyDefId)
               .HasConstraintName("FK_RegEntryKey_Key");
    }
}
