using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="Project"/>.</summary>
public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Project", "doc", t =>
        {
            t.HasCheckConstraint("CK_Project_Period", "PeriodStart <= PeriodEnd");
            t.HasCheckConstraint(
                "CK_Project_Pinned",
                "CurrentPeriodMode <> 1 OR " +
                "(CurrentPeriodId IS NOT NULL AND CurrentPeriodPinnedReason IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Project_Code");
        builder.LocalizedText(x => x.NameL10n);

        builder.Property(x => x.PeriodKind).HasConversion<byte>();
        builder.Property(x => x.CurrentPeriodMode).HasConversion<byte>();
        builder.Property(x => x.Status).HasConversion<byte>();

        // Пояс майданчика — не косметика: межі періодів рахуються в ньому,
        // і після відкриття першого періоду він не змінюється (ECR-PRD-0409).
        builder.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();

        builder.Property(x => x.TagsJson).HasMaxLength(500);
        builder.Property(x => x.ExternalSettingsJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.CurrentPeriodPinnedReason).HasMaxLength(400);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.YearGraceOffsetDays).HasDefaultValue(45, "DF_Project_YearGrace");
        builder.Property(x => x.TimeZoneId).HasDefaultValue("Central Asia Standard Time", "DF_Project_Tz");
        builder.Property(x => x.CurrentPeriodMode).HasDefaultValueSql("0", "DF_Project_CPMode");
        builder.Property(x => x.IsArchiving).HasDefaultValue(false, "DF_Project_Arch");
        builder.Navigation(x => x.Periods).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="PeriodPolicy"/>.</summary>
public sealed class PeriodPolicyConfiguration : IEntityTypeConfiguration<PeriodPolicy>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PeriodPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PeriodPolicy", "doc", t => t.HasCheckConstraint(
            "CK_PP_Order", "GraceOffsetDays <= HardCloseOffsetDays"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.OpenOffsetDays).HasDefaultValue(0, "DF_PP_Open");
        builder.Property(x => x.GraceOffsetDays).HasDefaultValue(15, "DF_PP_Grace");
        builder.Property(x => x.HardCloseOffsetDays).HasDefaultValue(45, "DF_PP_Hard");
        builder.Property(x => x.YearGraceOffsetDays).HasDefaultValue(45, "DF_PP_Year");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_PeriodPolicy");
    }
}

/// <summary>Конфігурація <see cref="Period"/>.</summary>
public sealed class PeriodConfiguration : IEntityTypeConfiguration<Period>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Period> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Period", "doc", t =>
        {
            t.HasCheckConstraint("CK_Period_Range", "PeriodStart <= PeriodEnd");
            t.HasCheckConstraint("CK_Period_Seq", "Sequence BETWEEN 1 AND 12");
        });
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.State).HasConversion<byte>();
        builder.Property(x => x.ReopenReason).HasMaxLength(400);

        // Період унікальний у проєкті за ключем: два «січня 2026» в одному
        // проєкті означали б два різні набори даних під одним ім'ям.
        builder.HasIndex(x => new { x.ProjectId, x.PeriodKeyValue })
               .IsUnique().HasDatabaseName("UQ_Period");

        // .WithMany(p => p.Periods) обов'язково: без явної навігації
        // Project.Periods стає другим зв'язком і тягне тіньову колонку
        // ProjectId1 разом з індексом на неї.
        builder.HasOne<Project>()
               .WithMany(p => p.Periods)
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_Period_Project");

    }
}

/// <summary>Конфігурація <see cref="Document"/>.</summary>
public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Document", "doc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BusinessKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.Property(x => x.NameL10n)
               .HasConversion(
                   v => v == null ? null : v.ToJson(),
                   v => v == null ? null : Domain.ValueObjects.LocalizedText.FromJson(v))
               .HasColumnType("nvarchar(max)");

        builder.HasIndex(x => new { x.ProjectId, x.BusinessKey })
               .IsUnique().HasDatabaseName("UQ_Document");

        builder.HasOne<Project>()
               .WithMany()
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_Document_Project");

        builder.Navigation(x => x.Sheets).UsePropertyAccessMode(PropertyAccessMode.Field);

        // ⚠ Скалярного Status тут немає навмисно (D-93): єдине джерело істини
        // про робочий стан — wf.ApprovalState за (DocumentId, SheetDefId,
        // PeriodKey). Поле-статус рано чи пізно показало б Approved на
        // документі з половиною аркушів у Draft.
    }
}

/// <summary>Конфігурація <see cref="DocumentSheet"/>.</summary>
public sealed class DocumentSheetConfiguration : IEntityTypeConfiguration<DocumentSheet>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentSheet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentSheet", "doc");

        // Ключ — сурогатний Id, а пара «документ × аркуш» тримається окремим
        // UNIQUE. Складений PK виглядав би природніше, але схема — єдине
        // джерело істини про форму ключа (08-workflow §7).
        builder.HasKey(x => x.Id);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsIncluded).HasDefaultValue(true, "DF_DocSheet_Inc");

        builder.HasIndex(x => new { x.DocumentId, x.SheetDefId })
               .IsUnique().HasDatabaseName("UQ_DocumentSheet");

        builder.HasOne<Document>()
               .WithMany(d => d.Sheets)
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_DocSheet_Document");
    }
}

/// <summary>Конфігурація <see cref="TableInstance"/>.</summary>
/// <remarks>
/// Ключ починається з партиційного стовпця — інакше індекс не вирівняний.
/// </remarks>
public sealed class TableInstanceConfiguration : IEntityTypeConfiguration<TableInstance>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableInstance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableInstance", "doc");
        builder.HasKey(x => new { x.PeriodKeyValue, x.Id });
        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");

        // Id приходить із SEQUENCE, а не з IDENTITY: значення потрібне ДО
        // вставки, щоб вантажити рядки і комірки одним проходом SqlBulkCopy.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.PeriodKeyValue, x.DocumentId, x.TableDefId })
               .IsUnique().HasDatabaseName("UQ_TableInstance");
    }
}

/// <summary>Конфігурація <see cref="TableRow"/>.</summary>
public sealed class TableRowConfiguration : IEntityTypeConfiguration<TableRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableRow", "doc");
        builder.HasKey(x => new { x.PeriodKeyValue, x.Id });
        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.RowKeyValue).HasColumnName("RowKey").HasMaxLength(100).IsRequired();

        // ⚠ RowVersion — саме те, на чому тримається оптимістичне блокування.
        // Він піднімається від UPDATE рядка, тому «дотик» при зміні комірок
        // обов'язковий (B04 §2.4).
        builder.Property(x => x.RowVersion).IsRowVersion();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_TableRow_Del");
        builder.Property(x => x.IsOrphaned).HasDefaultValue(false, "DF_TableRow_Orph");

        builder.HasIndex(x => new { x.PeriodKeyValue, x.TableInstanceId, x.RowKeyValue })
               .IsUnique().HasDatabaseName("UQ_TableRow_Key");

        builder.HasOne<TableInstance>()
               .WithMany()
               .HasForeignKey(x => new { x.PeriodKeyValue, x.TableInstanceId })
               .HasConstraintName("FK_TableRow_Instance");

        builder.Ignore(x => x.PeriodKey);
        builder.Ignore(x => x.RowKey);
    }
}
