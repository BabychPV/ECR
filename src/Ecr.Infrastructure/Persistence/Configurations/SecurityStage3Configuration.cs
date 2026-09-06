using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="RolePermission"/>.</summary>
/// <remarks>
/// Ключ складений із <c>RoleId</c> і коду права: сурогат тут нічого не додав би,
/// а дозволив би вставити ту саму пару двічі.
/// </remarks>
public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RolePermission", "sec");
        builder.HasKey(x => new { x.RoleId, x.PermissionCode }).HasName("PK_RolePermission");
        builder.Property(x => x.PermissionCode).HasMaxLength(64).IsRequired();

        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
               .HasConstraintName("FK_RolePerm_Role");
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionCode)
               .HasConstraintName("FK_RolePerm_Perm");
    }
}

/// <summary>Конфігурація <see cref="RoleAssignment"/>.</summary>
/// <remarks>
/// ⚠ Повертається в модель на Етапі 3 (<c>Q-042</c>). До цього була вилучена:
/// у сутності бракувало <c>PrincipalSid</c>, і ФВ-6.15 — «основний спосіб
/// призначення для доменних користувачів — на AD-групу» — була нездійсненною
/// за побудовою.
/// </remarks>
public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RoleAssignment", "sec", t => t.HasCheckConstraint(
            "CK_RoleAssign_Principal",
            // Рівно один адресат: або особа, або група. Обидва разом зробили б
            // призначення двозначним, жоден — «нічиїм».
            "(UserId IS NOT NULL AND PrincipalSid IS NULL) OR " +
            "(UserId IS NULL AND PrincipalSid IS NOT NULL)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.PrincipalSid).HasMaxLength(200);
        builder.Property(x => x.ScopeJson);

        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
               .HasConstraintName("FK_RoleAssign_Role");
        // Навігація, а не голий ключ: без неї EF не підставить UserId
        // для користувача, якого ще не збережено, і призначення довелося б
        // записувати другою транзакцією.
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_RoleAssign_User");

        builder.HasIndex(x => x.UserId).HasDatabaseName("IX_RoleAssignment_User");
        builder.HasIndex(x => x.PrincipalSid).HasDatabaseName("IX_RoleAssignment_Sid");
    }
}

/// <summary>Конфігурація <see cref="LoginAttempt"/>.</summary>
public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LoginAttempt", "sec");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Provider).HasConversion<byte>();
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.FailReason).HasMaxLength(200);

        // ⚠ Немає зовнішнього ключа на sec.User — навмисно: спроба під
        // неіснуючим іменем має записатися так само, як під справжнім. Інакше
        // підбір імен не лишав би сліду.
        builder.HasIndex(x => new { x.UserName, x.AttemptedAt })
               .IsDescending(false, true)
               .HasDatabaseName("IX_LoginAttempt_User");
    }
}

/// <summary>Конфігурація <see cref="ApprovalRoute"/>.</summary>
public sealed class ApprovalRouteConfiguration : IEntityTypeConfiguration<ApprovalRoute>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalRoute> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApprovalRoute", "wf");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.LocalizedText(x => x.NameL10n);
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_AR_Act");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_ApprovalRoute");

        // ⚠ Обчислювана властивість: у базі її немає і бути не має —
        // збережений пріоритет розійшовся б з областю дії при першій правці.
        builder.Ignore(x => x.Specificity);

        // ⛔ Друга половина області дії маршруту (`ФВ-5.17`). FK на проєкт, а
        // не просто число: маршрут неіснуючого проєкту — це маршрут, який
        // ніколи не спрацює, і знайшли б його лише тоді, коли документ не
        // затверджується.
        builder.HasOne<Domain.Entities.Documents.Project>()
               .WithMany()
               .HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_AR_Project");

        // ⚠ Індекс саме за областю дії: резолюція маршруту — запит виду
        // «ProjectId = @p OR ProjectId IS NULL», і він виконується на КОЖНЕ
        // подання та затвердження.
        builder.HasIndex(x => new { x.ProjectId, x.TemplateVersionId })
               .HasDatabaseName("IX_ApprovalRoute_Scope");

        // ⚠ FK лишається `Restrict`, як у схемі від Етапу 3, а кроки
        // прибирає явно `WorkflowStore.RemoveRouteAsync`. Оголосити в моделі
        // каскад, якого в базі немає, було б гірше за будь-який із варіантів:
        // EF вважав би, що діти зникнуть самі, і видалення падало б на
        // зовнішньому ключі вже в продуктиві.
        builder.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.ApprovalRouteId)
               .HasConstraintName("FK_AS_Route");
    }
}

/// <summary>Конфігурація <see cref="ApprovalStep"/>.</summary>
public sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApprovalStep", "wf");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsOptional).HasDefaultValue(false, "DF_AS_Opt");
        builder.HasIndex(x => new { x.ApprovalRouteId, x.Ordinal })
               .IsUnique().HasDatabaseName("UQ_ApprovalStep");
    }
}

/// <summary>Конфігурація <see cref="ValidationResult"/>.</summary>
public sealed class ValidationResultConfiguration : IEntityTypeConfiguration<ValidationResult>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ValidationResult> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ValidationResult", "wf");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MessagesJson).IsRequired();

        builder.HasOne<Domain.Entities.Documents.Document>().WithMany()
               .HasForeignKey(x => x.DocumentId).HasConstraintName("FK_VRes_Doc");

        builder.HasIndex(x => new { x.DocumentId, x.PeriodKey, x.RunAt })
               .HasDatabaseName("IX_ValidationResult_Doc");
    }
}

/// <summary>Конфігурація <see cref="SubmissionSnapshot"/>.</summary>
/// <remarks>
/// ⚠ Таблиця живе у схемі <c>calc</c>, але створюється на Етапі 3 разом із
/// поданням: без неї подання неможливе, а без подання Етап 3 не закривається.
/// Решта <c>calc.*</c> лишається на Етап 4 — вона про розрахунки, а не про
/// робочий процес.
/// </remarks>
public sealed class SubmissionSnapshotConfiguration : IEntityTypeConfiguration<SubmissionSnapshot>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SubmissionSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SubmissionSnapshot", "calc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MethodologyVersionsJson).IsRequired();
        builder.Property(x => x.PayloadJson).IsRequired();
        builder.Property(x => x.ContentHash).HasMaxLength(32).IsRequired();

        builder.HasOne<Domain.Entities.Documents.Document>().WithMany()
               .HasForeignKey(x => x.DocumentId).HasConstraintName("FK_SS_Doc");

        // Зрізи накопичуються, і читають їх завжди по трійці «документ × аркуш
        // × період» від найновішого (ФВ-9.17).
        builder.HasIndex(x => new { x.DocumentId, x.SheetDefId, x.PeriodKey, x.SubmittedAt })
               .IsDescending(false, false, false, true)
               .HasDatabaseName("IX_SubmissionSnapshot_Sheet");
    }
}
