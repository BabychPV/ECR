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
        builder.ToTable("RegistryEntry", "dic", t =>
        {
            t.HasCheckConstraint(
                "CK_RegEntry_Period",
                "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom < ValidTo");

            // RK03 (D-158, FEATURE-REGISTRY-TABLES §3.6): системна історія. Вона
            // пишеться базою на КОЖНОМУ шляху запису — ручному, CSV, імпорті,
            // синку, прямому SQL, — тому відтворити прогін «станом на» можна
            // незалежно від того, хто й як змінив довідник.
            t.IsTemporal(Temporal<RegistryEntry>("RegistryEntryHistory"));
        });
        TemporalPeriod(builder);
        builder.Property(x => x.ChangedByUserId);

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

        // RK02 (D-157): коди записів довідників із `CodeMode = Auto` — `E` + 9
        // цифр цієї послідовності (§4.8). Одна на всі довідники: код унікальний
        // лише в межах довідника, тож пропуски між довідниками нічого не
        // ламають, а окрема послідовність на кожен довідник означала б DDL
        // під час створення довідника (D-66: застосунок DDL-прав не має).
        //
        // ⚠ Оголошена тут, а не поруч із `TableRowSeq` в `EcrDbContext` під
        // `IsSqlServer()`: це послідовність саме записів довідника, а
        // провайдера, крім SQL Server, у дереві немає (див. коментар там).
        var model = builder.Metadata.Model;
        var codeSequence = model.FindSequence("RegistryEntryCodeSeq", "dic")
                           ?? model.AddSequence("RegistryEntryCodeSeq", "dic");
        codeSequence.Type = typeof(long);
        codeSequence.StartValue = 1;
        codeSequence.IncrementBy = 1;
    }

    /// <summary>
    /// Налаштування системної історії таблиці <c>dic.*</c>: історична таблиця в тій
    /// самій схемі, колонки періоду <c>PeriodStart</c>/<c>PeriodEnd</c>.
    /// </summary>
    /// <param name="historyTable">Ім'я історичної таблиці в схемі <c>dic</c>.</param>
    /// <returns>Дія для <c>IsTemporal</c>.</returns>
    internal static Action<TemporalTableBuilder<TEntity>> Temporal<TEntity>(string historyTable)
        where TEntity : class
        => tt =>
        {
            tt.UseHistoryTable(historyTable, "dic");
            tt.HasPeriodStart(PeriodStart).HasColumnName(PeriodStart);
            tt.HasPeriodEnd(PeriodEnd).HasColumnName(PeriodEnd);
        };

    /// <summary>Точність колонок періоду — <c>datetime2(3)</c>.</summary>
    /// <param name="builder">Будівник сутності з системною історією.</param>
    /// <remarks>
    /// ⛔ <c>datetime2(3)</c>, а не типові для EF <c>datetime2(7)</c>: так вимагає
    /// <c>D-68</c> для всіх міток часу (FEATURE-REGISTRY-TABLES §3.2). Дві зміни
    /// рядка в межах однієї мілісекунди дають історичний рядок нульової
    /// тривалості — його не видно на жоден момент «станом на», і це допустимо.
    /// </remarks>
    internal static void TemporalPeriod<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<DateTime>(PeriodStart).HasPrecision(3);
        builder.Property<DateTime>(PeriodEnd).HasPrecision(3);
    }

    /// <summary>Колонка початку системного періоду.</summary>
    internal const string PeriodStart = "PeriodStart";

    /// <summary>Колонка кінця системного періоду.</summary>
    internal const string PeriodEnd = "PeriodEnd";
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

        // RK03 (D-158): системна історія значень — див. RegistryEntryConfiguration.
        builder.ToTable("RegistryValue", "dic", t =>
            t.IsTemporal(RegistryEntryConfiguration.Temporal<RegistryValue>("RegistryValueHistory")));
        RegistryEntryConfiguration.TemporalPeriod(builder);
        builder.Property(x => x.ChangedByUserId);

        builder.HasKey(x => x.Id).HasName("PK_RegistryValue");

        builder.Property(x => x.RegistryEntryId).HasConversion<int>();
        builder.Property(x => x.ValueString).HasMaxLength(1000);

        // decimal(34,16) — та сама точність, що в комірках. Інша тут означала б,
        // що ліміт дозволу і виміряне значення округляються по-різному, і
        // порівняння «перевищено чи ні» залежало б від того, звідки взяли число.
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(34,16)");
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
