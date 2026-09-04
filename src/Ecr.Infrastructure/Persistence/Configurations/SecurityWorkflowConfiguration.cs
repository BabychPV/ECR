using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="User"/>.</summary>
/// <remarks>
/// ⚠ <c>SecurityStamp</c> перевіряється на кожен запит: саме він робить
/// відкликання ролі негайним, а не «через 8 годин» (ФВ-6.4).
/// Пароль і його хеш не потрапляють у жоден лог і в жодну відповідь (ФВ-6.11).
/// </remarks>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("User", "sec", t => t.HasCheckConstraint(
            "CK_User_Provider",
            "(Provider = 0 AND WindowsSid IS NOT NULL AND PasswordHash IS NULL) OR " +
            "(Provider = 1 AND PasswordHash IS NOT NULL AND WindowsSid IS NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.Provider).HasConversion<byte>();
        builder.Property(x => x.WindowsSid).HasMaxLength(200);
        builder.Property(x => x.PasswordHash).HasMaxLength(400);
        builder.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.FailedAttempts).HasDefaultValue(0, "DF_User_Failed");
        builder.Property(x => x.MustChangePassword).HasDefaultValue(false, "DF_User_MustChg");
        builder.Property(x => x.IsBootstrapAdmin).HasDefaultValue(false, "DF_User_Bootstrap");
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_User_Active");

        builder.HasIndex(x => x.UserName).IsUnique().HasDatabaseName("UQ_User_Name");

        // SID унікальний лише серед доменних користувачів: у локальних він null,
        // а кілька null у фільтрованому індексі не конфліктують.
        builder.HasIndex(x => x.WindowsSid)
               .IsUnique()
               .HasFilter("[WindowsSid] IS NOT NULL")
               .HasDatabaseName("UX_User_Sid");

        // Bootstrap-адміністратор може бути лише один (D-97). Фільтр за
        // одиницею, а не унікальність по всьому стовпцю: нулів тут тисячі.
        builder.HasIndex(x => x.IsBootstrapAdmin)
               .IsUnique()
               .HasFilter("[IsBootstrapAdmin] = 1")
               .HasDatabaseName("UX_User_Bootstrap");
    }
}

/// <summary>Конфігурація <see cref="Role"/>.</summary>
public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Role", "sec");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsBuiltIn).HasDefaultValue(false, "DF_Role_Built");
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_Role_Act");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Role");
    }
}

/// <summary>Конфігурація <see cref="ResourceGrant"/>.</summary>
/// <remarks>
/// ⚠ <c>IsDeny</c> виграє на будь-якому рівні успадкування: заборона, яку
/// можна перекрити дозволом нижче, не є забороною (ФВ-6.7).
/// </remarks>
public sealed class ResourceGrantConfiguration : IEntityTypeConfiguration<ResourceGrant>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ResourceGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ResourceGrant", "sec");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ResourceKind).HasConversion<byte>();
        builder.Property(x => x.Level).HasConversion<byte>();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsDeny).HasDefaultValue(false, "DF_Grant_Deny");

        builder.HasIndex(x => new { x.RoleId, x.ResourceKind, x.ResourceId })
               .IsUnique().HasDatabaseName("UQ_ResourceGrant");

        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
               .HasConstraintName("FK_Grant_Role");
    }
}

/// <summary>Конфігурація <see cref="ApprovalState"/>.</summary>
/// <remarks>
/// ⚠ Це <b>єдине джерело істини</b> про робочий стан (<c>D-93</c>), і ключ у
/// нього — трійка «документ × аркуш × період»: подання відбувається на рівні
/// аркуша за період, а не документа цілком (<c>D-38</c>).
/// </remarks>
public sealed class ApprovalStateConfiguration : IEntityTypeConfiguration<ApprovalState>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApprovalState", "wf", t => t.HasCheckConstraint(
            "CK_ApprState_Reopen", "ReopenedAt IS NULL OR ReopenReason IS NOT NULL"));

        // Ключ — сурогат; трійка «документ × аркуш × період» тримається
        // окремим UNIQUE (02a-db-schema.md).
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.DocumentId, x.SheetDefId, x.PeriodKey })
               .IsUnique().HasDatabaseName("UQ_ApprovalState");

        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.RejectedReason).HasMaxLength(1000);
        builder.Property(x => x.ReopenReason).HasMaxLength(1000);
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
