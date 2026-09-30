using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="RowWindowMap"/> — прив'язки «атрибут → колонка,
/// вікно = рядок» (HSE301 §4.4, міграція <c>HSE301M2RowWindow</c>).
/// </summary>
public sealed class RowWindowMapConfiguration : IEntityTypeConfiguration<RowWindowMap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RowWindowMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⚠ Кожне обмеження — окремим викликом ToTable (див. EntityFieldMapConfiguration).
        builder.ToTable("RowWindowMap", "ext", t => t.HasCheckConstraint(
            "CK_RWM_Summary",
            // Перелік закритий: число, якого не знає задача підтягування, дало б
            // відмову під час збору, а не під час налаштування.
            "Summary BETWEEN 0 AND 4"));
        builder.ToTable("RowWindowMap", "ext", t => t.HasCheckConstraint(
            "CK_RWM_Window",
            // Та сама колонка на початку й у кінці — вікно завжди порожнє.
            "StartColumnDefId <> EndColumnDefId"));
        builder.ToTable("RowWindowMap", "ext", t => t.HasCheckConstraint(
            "CK_RWM_Policy",
            "MinPercentGood BETWEEN 0 AND 100 AND RefetchWithinDays BETWEEN 0 AND 366 "
            + "AND (MaxGapSeconds IS NULL OR MaxGapSeconds > 0)"));

        builder.HasKey(x => x.Id);

        // ⚠ HasDefaultValue + ValueGeneratedNever: DEFAULT схеми — для скриптів
        // і DBA, а EF надсилає значення завжди. Без ValueGeneratedNever явно
        // задані `IsStep = false`, `IsActive = false` чи `MinPercentGood = 0`
        // (CLR-замовчування) EF не надіслав би, і в рядок мовчки ліг би DEFAULT
        // (`EnumDefaultSentinelTests`).
        builder.Property(x => x.IsStep).HasDefaultValue(false, "DF_RWM_Step").ValueGeneratedNever();
        builder.Property(x => x.MinPercentGood).HasPrecision(5, 2)
               .HasDefaultValue(RowWindowMap.DefaultMinPercentGood, "DF_RWM_MinGood").ValueGeneratedNever();
        builder.Property(x => x.RefetchWithinDays)
               .HasDefaultValue(RowWindowMap.DefaultRefetchWithinDays, "DF_RWM_Refetch").ValueGeneratedNever();
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_RWM_Act").ValueGeneratedNever();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.Ignore(x => x.MaxGap);

        // ⛔ Одна прив'язка на колонку-ціль: дві писали б у ту саму комірку, і
        // число залежало б від того, котра задача відпрацювала останньою.
        // TableDefId у ключі зайвий за змістом (колонка належить рівно одній
        // таблиці), але дає seek «прив'язки таблиці» без окремого індексу.
        builder.HasIndex(x => new { x.TableDefId, x.TargetColumnDefId })
               .IsUnique().HasDatabaseName("UQ_RowWindowMap_Target");

        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_RWM_Table");

        // ⛔ Ключі на колонки СКЛАДЕНІ — (TableDefId, ColumnDefId) на
        // UQ_ColumnDef_ForFk, як FK_CellValue_Column: ключ лише на Id пропустив
        // би колонку вікна з чужої таблиці, і вікно бралося б із рядка, якого в
        // цій таблиці немає.
        builder.HasOne<ColumnDef>().WithMany()
               .HasForeignKey(x => new { x.TableDefId, x.TargetColumnDefId })
               .HasPrincipalKey(c => new { c.TableDefId, c.Id })
               .HasConstraintName("FK_RWM_Target");
        builder.HasOne<ColumnDef>().WithMany()
               .HasForeignKey(x => new { x.TableDefId, x.StartColumnDefId })
               .HasPrincipalKey(c => new { c.TableDefId, c.Id })
               .HasConstraintName("FK_RWM_Start");
        builder.HasOne<ColumnDef>().WithMany()
               .HasForeignKey(x => new { x.TableDefId, x.EndColumnDefId })
               .HasPrincipalKey(c => new { c.TableDefId, c.Id })
               .HasConstraintName("FK_RWM_End");
        builder.HasOne<ColumnDef>().WithMany()
               .HasForeignKey(x => new { x.TableDefId, x.SelectorColumnDefId })
               .HasPrincipalKey(c => new { c.TableDefId, c.Id })
               .HasConstraintName("FK_RWM_Selector");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.TargetUnitId)
               .HasConstraintName("FK_RWM_Unit");

        builder.HasMany(x => x.Sources).WithOne().HasForeignKey(s => s.RowWindowMapId)
               .HasConstraintName("FK_RWS_Map");
        builder.Navigation(x => x.Sources).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="RowWindowSource"/> — значення селектора → атрибут.</summary>
public sealed class RowWindowSourceConfiguration : IEntityTypeConfiguration<RowWindowSource>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RowWindowSource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RowWindowSource", "ext");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SelectorValue).HasMaxLength(RowWindowMap.MaxSelectorValueLength);
        builder.Property(x => x.SourceField).HasMaxLength(RowWindowMap.MaxSourceFieldLength).IsRequired();

        // ⛔ Одне значення селектора — одне джерело. Фільтра `IS NOT NULL` тут
        // НЕМАЄ навмисно: унікальний індекс SQL Server вважає NULL рівними, тож
        // джерело «для всіх рядків» (SelectorValue = NULL) у прив'язки теж одне.
        // EF за замовчуванням додав би такий фільтр — його знято явно.
        builder.HasIndex(x => new { x.RowWindowMapId, x.SelectorValue })
               .IsUnique().HasFilter(null).HasDatabaseName("UQ_RowWindowSource");

        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_RWS_Entity");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.SourceUnitId)
               .HasConstraintName("FK_RWS_Unit");
    }
}

/// <summary>
/// Конфігурація <see cref="RowWindowValue"/> — провенансу підтягувань;
/// партиційованої таблиці.
/// </summary>
/// <remarks>
/// ⚠ Ключ складений із <c>PeriodKey</c> і <c>Id</c>: партиційна функція вимагає
/// ключа партиціонування в кластерному індексі. На <c>ps_ByPeriodKey</c> таблицю
/// переносить <c>07-partition-tables.sql</c> — EF розміщення не виражає.
/// </remarks>
public sealed class RowWindowValueConfiguration : IEntityTypeConfiguration<RowWindowValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RowWindowValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RowWindowValue", "ext", t => t.HasCheckConstraint(
            "CK_RWV_Status",
            "Status IN (N'Fetched', N'Partial', N'NoData', N'KeptManual', "
            + "N'SourceError', N'InvalidWindow', N'NotApplicable')"));
        builder.ToTable("RowWindowValue", "ext", t => t.HasCheckConstraint(
            "CK_RWV_Kinds",
            "Summary BETWEEN 0 AND 4 AND ComputedBy BETWEEN 0 AND 1"));
        builder.ToTable("RowWindowValue", "ext", t => t.HasCheckConstraint(
            "CK_RWV_Numbers",
            "PointCount >= 0 AND (PercentGood IS NULL OR PercentGood BETWEEN 0 AND 100)"));

        builder.HasKey(x => new { x.PeriodKey, x.Id }).HasName("PK_RowWindowValue");

        // IDENTITY, а не SEQUENCE: запис поштучний (одна подія — один рядок) і
        // йде через EF, масового завантаження, заради якого існують
        // doc.TableRowSeq і calc.CalculationResultSeq, тут немає.
        builder.Property(x => x.Id).UseIdentityColumn();

        builder.Property(x => x.RowKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceField).HasMaxLength(RowWindowMap.MaxSourceFieldLength).IsRequired();
        builder.Property(x => x.SourceUnitSymbol).HasMaxLength(64);
        // Відсоток, не вимірювана величина: decimal(5,2), а не (34,16) конвенції.
        builder.Property(x => x.PercentGood).HasPrecision(5, 2);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(32).IsUnicode(false);
        builder.Property(x => x.IsCurrent).HasDefaultValue(true, "DF_RWV_Current").ValueGeneratedNever();

        // ⛔ Чинний запис комірки — рівно один. Індекс УНІКАЛЬНИЙ і з PeriodKey
        // першим: унікальний індекс партиційованої таблиці мусить містити ключ
        // партиції (інакше SQL Server не вирівняє його зі схемою), а запит
        // «чинне значення рядка» однаково знає період екземпляра.
        builder.HasIndex(x => new { x.PeriodKey, x.TableInstanceId, x.RowKey, x.ColumnDefId })
               .IsUnique()
               .HasFilter("[IsCurrent] = 1")
               .HasDatabaseName("UX_RowWindowValue_Current");

        builder.HasOne<RowWindowMap>().WithMany().HasForeignKey(x => x.RowWindowMapId)
               .HasConstraintName("FK_RWV_Map");
        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
               .HasConstraintName("FK_RWV_Entity");
        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.ColumnDefId)
               .HasConstraintName("FK_RWV_Column");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.TargetUnitId)
               .HasConstraintName("FK_RWV_Unit");
    }
}
