using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="Methodology"/>.</summary>
/// <remarks>
/// ⚠ Дев'ять сутностей <c>calc.*</c> повертаються в модель на Етапі 4 —
/// це `calc`-частина <c>Q-027</c>. До цього їх у моделі не було взагалі, і
/// міграція не створювала жодної таблиці схеми <c>calc</c>.
/// </remarks>
public sealed class MethodologyConfiguration : IEntityTypeConfiguration<Methodology>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Methodology> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Methodology", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Group).HasColumnName("Group").HasMaxLength(64);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

        // ⚠ Значення за замовчуванням у БАЗІ, а не в моделі: наявні рядки
        // корпусу — це 44 методології `ECW_C**`, і всі вони DataDriven. У
        // моделі default не оголошено навмисно — інакше EF пропускав би
        // властивість при вставці саме тоді, коли вона дорівнює нулю.
        builder.Property(x => x.Kind).HasColumnName("Kind");

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Methodology");
    }
}

/// <summary>Конфігурація <see cref="MethodologyImport"/> — видимості чужих формул.</summary>
public sealed class MethodologyImportConfiguration : IEntityTypeConfiguration<MethodologyImport>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyImport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyImport", "calc");
        builder.HasKey(x => x.Id);

        // Двічі оголошений той самий імпорт нічого не додає, але подвоїв би
        // кандидатів у резолвінгу `!Name` і зробив би однозначне посилання
        // «неоднозначним».
        builder.HasIndex(x => new { x.MethodologyVersionId, x.ImportedMethodologyId })
               .IsUnique().HasDatabaseName("UQ_MethodologyImport");

        // ⚠ Обидва боки Restrict — глобально, як усе в цій моделі
        // (`EcrDbContext.OnModelCreating`). Тут це особливо доречно для
        // FK_MI_Imported: видалення бібліотеки, на яку посилаються 265 виразів
        // корпусу, має впертися в помилку, а не тихо забрати з ними видимість
        // її формул.
        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MI_Version");
        builder.HasOne<Methodology>().WithMany().HasForeignKey(x => x.ImportedMethodologyId)
               .HasConstraintName("FK_MI_Imported");
    }
}

/// <summary>Конфігурація <see cref="MethodologyDependency"/> — ребра графа методологій.</summary>
public sealed class MethodologyDependencyConfiguration : IEntityTypeConfiguration<MethodologyDependency>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyDependency> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyDependency", "calc", t => t.HasCheckConstraint(
            "CK_MD_NoSelfLoop", "FromMethodologyId <> ToMethodologyId"));

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.FromMethodologyId, x.ToMethodologyId })
               .IsUnique().HasDatabaseName("UQ_MethodologyDependency");

        // ⚠ Обидва боки Restrict (глобально, `EcrDbContext.OnModelCreating`):
        // ребро — це знання про порядок перерахунку. Каскадне видалення тихо
        // зробило б порядок неповним, а неповний порядок дає торішнє число без
        // жодної помилки в журналі.
        builder.HasOne<Methodology>().WithMany().HasForeignKey(x => x.FromMethodologyId)
               .HasConstraintName("FK_MD_From");
        builder.HasOne<Methodology>().WithMany().HasForeignKey(x => x.ToMethodologyId)
               .HasConstraintName("FK_MD_To");
    }
}

/// <summary>Конфігурація <see cref="MethodologyVersion"/>.</summary>
public sealed class MethodologyVersionConfiguration : IEntityTypeConfiguration<MethodologyVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyVersion", "calc", t =>
        {
            // ⛔ Чотири очі — на рівні БАЗИ, а не лише коду (D-40). Публікація
            // версії методології змінює вже подані числа; правило, яке тримає
            // тільки застосунок, обходиться будь-яким скриптом.
            t.HasCheckConstraint(
                "CK_MV_FourEyes",
                "PublishedByUserId IS NULL OR PublishedByUserId <> CreatedByUserId");

            // Опублікована версія без дати чинності, часу публікації або
            // причини — стан, який неможливо ні пояснити, ні відтворити.
            t.HasCheckConstraint(
                "CK_MV_Published",
                "Status <> 1 OR (PublishedAt IS NOT NULL AND EffectiveFrom IS NOT NULL "
                + "AND ChangeReason IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Level).HasColumnName("Level");
        builder.Property(x => x.NumericMode).HasDefaultValue(Domain.Enums.NumericMode.Legacy);
        builder.Property(x => x.CalendarMode).HasDefaultValue(Domain.Enums.CalendarMode.Actual);
        builder.Property(x => x.TraceLevel).HasDefaultValue(Domain.Enums.TraceLevel.ErrorsOnly);
        builder.Property(x => x.EffectiveFrom).HasColumnType("date");
        builder.Property(x => x.ChangeReason).HasMaxLength(1000);
        builder.Property(x => x.ContentHash).HasColumnType("varbinary(32)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.MethodologyId, x.Version })
               .IsUnique().HasDatabaseName("UQ_MethodologyVersion");

        // .WithMany(m => m.Versions) обов'язково: інакше Methodology.Versions
        // стає другим зв'язком і тягне тіньову колонку MethodologyId1.
        builder.HasOne<Methodology>().WithMany(m => m.Versions)
               .HasForeignKey(x => x.MethodologyId)
               .HasConstraintName("FK_MV_Methodology");
    }
}

/// <summary>Конфігурація <see cref="MethodologyFormula"/>.</summary>
public sealed class MethodologyFormulaConfiguration : IEntityTypeConfiguration<MethodologyFormula>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyFormula> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⛔ Одиниця результату несумісна з текстовим результатом, і тримати це
        // лише в домені мало: рядки методологій кладе ще й імпортер корпусу,
        // який ходить у базу масовою вставкою повз сутності.
        builder.ToTable("MethodologyFormula", "calc", t => t.HasCheckConstraint(
            "CK_MF_TextHasNoUnit", "ResultType = 0 OR OutputUnitId IS NULL"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Expression).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.EvaluationOrder).HasDefaultValue(0);
        builder.Property(x => x.ResultType).HasColumnName("ResultType");

        builder.HasIndex(x => new { x.MethodologyVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_MethodologyFormula");

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MF_Version");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.OutputUnitId)
               .HasConstraintName("FK_MF_Unit");
    }
}

/// <summary>
/// Конфігурація <see cref="MethodologyTestCaseEntity"/> — тестів методології.
/// </summary>
/// <remarks>
/// ⚠ Таблиця додана після `P-08`: ФВ-13.7 прямо на неї посилалася, а в схемі
/// її не було. Форма дослівно повторює ту, що описана в проблемі, — щоб
/// рішення людини не довелося переписувати.
/// </remarks>
public sealed class MethodologyTestCaseConfiguration : IEntityTypeConfiguration<MethodologyTestCaseEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyTestCaseEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TestCase", "calc", t => t.HasCheckConstraint(
            "CK_TC_Tolerance", "Tolerance >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.InputJson).IsRequired();
        builder.Property(x => x.ExpectedJson).IsRequired();
        builder.Property(x => x.Tolerance).HasColumnType("decimal(28,10)").HasDefaultValue(0m);

        // ⚠ Унікальність у межах ВЕРСІЇ, а не методології: тест належить
        // конкретній версії, і клон версії має право змінити очікуване число.
        builder.HasIndex(x => new { x.MethodologyVersionId, x.Code })
               .IsUnique()
               .HasDatabaseName("UQ_TestCase");

        // ⛔ `Restrict`: **золотий набір переживає версію, якою його
        // перевіряли** (`H-2`). Тут стояв `Cascade` з поясненням «тест без
        // версії не означає нічого» — і після директиви №05 це перестало бути
        // правдою. Набір більше не «тести, які написав автор чернетки»: це
        // **вивантаження з чинної системи** — реальний рядок і число, яке дав
        // чинний CLR (`B19` §3). Такий кейс не належить версії, він інваріант,
        // який кожна наступна версія має відтворити.
        //
        // ⚠ Поведінка від цього рядка **не змінюється**, і це найважливіше в
        // ньому. `EcrDbContext.OnModelCreating` після всіх конфігурацій
        // проходить усі зовнішні ключі й ставить `Restrict` кожному — тож
        // `Cascade` тут не діяв ніколи. Тобто рядок був не помилкою поведінки,
        // а **неправдою в джерелі**: той, хто його читав, вважав, що тести
        // зникають разом із версією. Сторож `DeleteBehaviorTests` більше не
        // дає оголосити тут поведінку, якої не буде.
        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_TC_Version")
               .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Конфігурація <see cref="MethodologyConstant"/> — контекстних коефіцієнтів.</summary>
public sealed class MethodologyConstantConfiguration : IEntityTypeConfiguration<MethodologyConstant>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyConstant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyConstant", "calc", t =>
        {
            t.HasCheckConstraint(
                "CK_MC_Period", "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo");

            // ⛔ Перелік станів навмисно НЕПОВНИЙ: `Kind = 0` із `Value IS NULL`
            // дозволений. Це рядок, який імпорт не зміг розібрати
            // (`n_ECW_C11_13_ = '-'`, `k22_HSE30X_Int_FG_ = ''`), і він має
            // дожити до публікації, щоб людина ухвалила рішення. Заборонити
            // його тут означало б або впустити тихий нуль, або обірвати імпорт
            // 6507 констант на трьох дефектних рядках.
            //
            // ⚠ Текст і мітка одиниці не мають: вимір — властивість числа.
            t.HasCheckConstraint(
                "CK_MC_Kind",
                "(Kind = 0 AND UnitId IS NOT NULL AND (Value IS NOT NULL OR TextValue IS NOT NULL)) "
                + "OR (Kind <> 0 AND Value IS NULL AND UnitId IS NULL AND TextValue IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(64);
        builder.Property(x => x.Kind).HasColumnName("Kind");
        builder.Property(x => x.Value).HasColumnType("decimal(28,10)");

        // 400 — та сама межа, що в `calc.CalculationInput.ValueString`: у
        // корпусі найдовше нечислове значення — `'LPG - СУГ'`.
        builder.Property(x => x.TextValue).HasMaxLength(400);
        builder.Property(x => x.ValidFrom).HasColumnType("date");
        builder.Property(x => x.ValidTo).HasColumnType("date");
        builder.Property(x => x.SubstanceEntryId).HasConversion<int?>();
        builder.Property(x => x.Source).HasColumnName("Source").HasMaxLength(400);

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MC_Version");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId)
               .HasConstraintName("FK_MC_Unit");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.SubstanceEntryId)
               .HasConstraintName("FK_MC_Substance");
    }
}

/// <summary>Конфігурація <see cref="MethodologySubstance"/>.</summary>
public sealed class MethodologySubstanceConfiguration : IEntityTypeConfiguration<MethodologySubstance>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologySubstance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologySubstance", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SubstanceEntryId).HasConversion<int>();

        builder.HasIndex(x => new { x.MethodologyVersionId, x.SubstanceEntryId })
               .IsUnique().HasDatabaseName("UQ_MethodologySubstance");

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MS_Version");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.SubstanceEntryId)
               .HasConstraintName("FK_MS_Substance");
    }
}

/// <summary>Конфігурація <see cref="MethodologyOutput"/>.</summary>
public sealed class MethodologyOutputConfiguration : IEntityTypeConfiguration<MethodologyOutput>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyOutput> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyOutput", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => new { x.MethodologyVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_MethodologyOutput");

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MO_Version");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId)
               .HasConstraintName("FK_MO_Unit");
    }
}

/// <summary>Конфігурація <see cref="MethodologyRule"/> — прив'язки правилами (ФВ-13.3).</summary>
public sealed class MethodologyRuleConfiguration : IEntityTypeConfiguration<MethodologyRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MethodologyRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MethodologyRule", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.MatchJson).IsRequired();
        builder.Property(x => x.Priority).HasDefaultValue(100);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

        builder.HasIndex(x => new { x.MethodologyVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_MethodologyRule");

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_MR_Version");
    }
}

/// <summary>Конфігурація <see cref="ScriptVersion"/>.</summary>
public sealed class ScriptVersionConfiguration : IEntityTypeConfiguration<ScriptVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScriptVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ScriptVersion", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceCode).IsRequired();
        builder.Property(x => x.CompiledAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.HasGreenTest).HasDefaultValue(false);
        builder.Property(x => x.ContentHash).HasColumnType("varbinary(32)").IsRequired();

        // Один скрипт на версію: два означали б, що незрозуміло, який із них
        // рахував уже подані числа.
        builder.HasIndex(x => x.MethodologyVersionId)
               .IsUnique().HasDatabaseName("UQ_ScriptVersion");

        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_SV_Version");
    }
}

/// <summary>Конфігурація <see cref="CalculationRun"/>.</summary>
public sealed class CalculationRunConfiguration : IEntityTypeConfiguration<CalculationRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CalculationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalculationRun", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnType("datetime2(3)").IsRequired();
        builder.Property(x => x.FinishedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        builder.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_CR_Project");
    }
}

/// <summary>Конфігурація <see cref="CalculationResult"/> — партиційованої таблиці.</summary>
/// <remarks>
/// ⚠ Ключ складений із <c>PeriodKey</c> і <c>Id</c>: партиційна функція
/// вимагає, щоб ключ партиціонування входив у кластерний індекс. Партиційну
/// схему EF не виражає — її створює скрипт (`D-66`), а модель лишається
/// узгодженою за формою ключа.
/// </remarks>
public sealed class CalculationResultConfiguration : IEntityTypeConfiguration<CalculationResult>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CalculationResult> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalculationResult", "calc");
        builder.HasKey(x => new { x.PeriodKey, x.Id }).HasName("PK_CalculationResult");

        // Id береться з SEQUENCE, а не з IDENTITY: значення потрібне ДО
        // вставки, щоб завантажити результати і трейс одним проходом
        // SqlBulkCopy.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SourceRowKey).HasMaxLength(100);
        builder.Property(x => x.OutputCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Value).HasColumnType("decimal(28,10)");
        builder.Property(x => x.SubstanceEntryId).HasConversion<int?>();

        builder.HasIndex(x => new { x.PeriodKey, x.DocumentId, x.MethodologyVersionId, x.OutputCode })
               .HasDatabaseName("IX_CalculationResult_Lookup")
               .IncludeProperties(x => new { x.Value, x.UnitId, x.SubstanceEntryId, x.SourceRowKey });

        builder.HasOne<CalculationRun>().WithMany().HasForeignKey(x => x.CalculationRunId)
               .HasConstraintName("FK_CRes_Run");
        builder.HasOne<MethodologyVersion>().WithMany().HasForeignKey(x => x.MethodologyVersionId)
               .HasConstraintName("FK_CRes_MV");
        builder.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId)
               .HasConstraintName("FK_CRes_Unit");
    }
}

/// <summary>Конфігурація <see cref="CalculationInputRow"/> — доказової бази відтворюваності.</summary>
public sealed class CalculationInputConfiguration : IEntityTypeConfiguration<CalculationInputRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CalculationInputRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalculationInput", "calc");
        builder.HasKey(x => new { x.PeriodKey, x.Id }).HasName("PK_CalculationInput");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SourceRowKey).HasMaxLength(100);
        builder.Property(x => x.ArgumentCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Value).HasColumnType("decimal(28,10)");
        builder.Property(x => x.ValueString).HasMaxLength(400);

        builder.HasOne<CalculationRun>().WithMany().HasForeignKey(x => x.CalculationRunId)
               .HasConstraintName("FK_CIn_Run");
    }
}

/// <summary>Конфігурація <see cref="CalculationStep"/> — трейсу.</summary>
public sealed class CalculationStepConfiguration : IEntityTypeConfiguration<CalculationStep>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CalculationStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalculationStep", "calc");
        builder.HasKey(x => new { x.PeriodKey, x.Id }).HasName("PK_CalculationStep");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.StepCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Expression).HasMaxLength(2000);
        builder.Property(x => x.Value).HasColumnType("decimal(28,10)");
        builder.Property(x => x.Masked).HasColumnName("MaskedZero").HasDefaultValue(Domain.Enums.MaskedZeroReason.None);

        // ⛔ Фільтрований індекс, а не звичайний. Замаскованих кроків мало —
        // решта трейсу це `MaskedZero = 0`, — а питання до них рівно одне:
        // «покажи всі». Звичайний індекс по колонці, яка майже завжди нуль,
        // коштував би місця на кожному з мільйонів рядків і не пришвидшив би
        // нічого; фільтрований важить стільки, скільки самих знахідок.
        //
        // ⚠ Саме заради цього запиту крок і робиться: директива обіцяє, що
        // випадків знайдеться чимало, і що це найцінніший побічний результат
        // міграції. Обіцянка здійсненна лише тоді, коли їх можна перелічити.
        builder.HasIndex(x => new { x.PeriodKey, x.Masked })
               .HasDatabaseName("IX_CStep_Masked")
               .HasFilter("[MaskedZero] <> 0");

        builder.HasOne<CalculationRun>().WithMany().HasForeignKey(x => x.CalculationRunId)
               .HasConstraintName("FK_CStep_Run");
    }
}
