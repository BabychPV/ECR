using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="EditAssignment"/> (<c>doc.EditAssignment</c>, EA-1): передача зрізу
/// документа виконавцям.
/// </summary>
/// <remarks>
/// ⚠ Усі FK — NO ACTION (конвенція бази, <c>EcrDbContext.OnModelCreating</c>): видалення чернетки
/// прибирає передачі явно у <c>DocumentDeletionStore</c>, зняття набору — обнулює
/// <c>AppliedSetId</c> явно (EA-4). Стани й кореляції продубльовані CHECK-обмеженнями: у таблицю
/// пишуть не лише через домен (сід, фікстури, руки DBA).
/// </remarks>
public sealed class EditAssignmentConfiguration : IEntityTypeConfiguration<EditAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EditAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EditAssignment", "doc", t =>
        {
            // Таблиця — лише з аркушем (код таблиці унікальний у межах аркуша).
            t.HasCheckConstraint("CK_EditAssignment_Table", "TableCode IS NULL OR SheetCode IS NOT NULL");
            t.HasCheckConstraint("CK_EditAssignment_Month", "MonthNumber IS NULL OR MonthNumber BETWEEN 1 AND 12");

            // Рік*100+№, як PeriodKey.IsValid: 1900..9999 і номер 1..99.
            t.HasCheckConstraint(
                "CK_EditAssignment_Period",
                "PeriodKey IS NULL OR (PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99)");

            // Стан валідації й відмітки йдуть разом: «Валідовано» без часу й автора неможливе, і навпаки.
            t.HasCheckConstraint(
                "CK_EditAssignment_Validation",
                "(ValidationStatus = 0 AND ValidatedAt IS NULL AND ValidatedByUserId IS NULL) "
                + "OR (ValidationStatus = 1 AND ValidatedAt IS NOT NULL AND ValidatedByUserId IS NOT NULL)");
            t.HasCheckConstraint("CK_EditAssignment_SliceKey", "LEN(SliceKey) > 0");
            t.HasCheckConstraint("CK_EditAssignment_Note", "Note IS NULL OR LEN(Note) > 0");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.DocumentId).IsRequired();
        builder.Property(x => x.SheetCode).HasMaxLength(EditAssignmentSlice.MaxCodeLength);
        builder.Property(x => x.TableCode).HasMaxLength(EditAssignmentSlice.MaxCodeLength);

        // Колонка PeriodKey (як doc.TableInstance): властивість названа PeriodKeyValue, щоб не
        // затіняти тип ValueObjects.PeriodKey.
        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.SliceKey).HasMaxLength(EditAssignmentSlice.MaxKeyLength).IsRequired();

        builder.Property(x => x.ValidationStatus).HasConversion<byte>();
        builder.Property(x => x.IsExclusive).HasDefaultValue(true, "DF_EditAssignment_Excl");
        builder.Property(x => x.ValidationStatus).HasDefaultValue(
            Domain.Enums.EditAssignmentValidationStatus.NotValidated, "DF_EditAssignment_Valid");

        builder.Property(x => x.Note).HasMaxLength(EditAssignment.MaxNoteLength);
        builder.Property(x => x.AppliedSetName).HasMaxLength(EditorSet.MaxNameLength);

        builder.Property(x => x.ValidatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ExpiresAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ModifiedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Зріз унікальний у документі: upsert за SliceKey ідемпотентний (T12).
        builder.HasIndex(x => new { x.DocumentId, x.SliceKey })
               .IsUnique().HasDatabaseName("UQ_EditAssignment");

        // Дія «Передати» на кілька зрізів: список і відклик групи.
        builder.HasIndex(x => x.HandoffId).HasDatabaseName("IX_EditAssignment_Handoff");

        // FK не індексуються конвенцією: зняття набору шукає передачі за AppliedSetId.
        builder.HasIndex(x => x.AppliedSetId)
               .HasDatabaseName("IX_EditAssignment_AppliedSet")
               .HasFilter("[AppliedSetId] IS NOT NULL");

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_EditAssignment_Document");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.DelegatedByUserId)
               .HasConstraintName("FK_EditAssignment_DelegatedBy");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.ValidatedByUserId)
               .HasConstraintName("FK_EditAssignment_ValidatedBy");

        builder.HasOne<EditorSet>()
               .WithMany()
               .HasForeignKey(x => x.AppliedSetId)
               .HasConstraintName("FK_EditAssignment_Set");

        builder.HasMany(x => x.Recipients)
               .WithOne()
               .HasForeignKey(r => r.AssignmentId)
               .HasConstraintName("FK_EditAssignmentRecipient_Assignment");
        builder.Navigation(x => x.Recipients).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Похідне значення, не колонка.
        builder.Ignore(x => x.Slice);
    }
}

/// <summary>
/// Конфігурація <see cref="EditAssignmentRecipient"/> (<c>doc.EditAssignmentRecipient</c>, EA-1):
/// виконавець зі своїм станом.
/// </summary>
public sealed class EditAssignmentRecipientConfiguration : IEntityTypeConfiguration<EditAssignmentRecipient>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EditAssignmentRecipient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EditAssignmentRecipient", "doc", t =>
        {
            // Assigned — лише початковий стан (без відміток); Filled має час; Returned — час, автор і причина.
            t.HasCheckConstraint(
                "CK_EditAssignmentRecipient_State",
                "(Status = 0 AND FilledAt IS NULL AND ReturnedAt IS NULL AND ReturnedByUserId IS NULL "
                + "AND ReturnReason IS NULL AND ReturnCount = 0) "
                + "OR (Status = 1 AND FilledAt IS NOT NULL) "
                + "OR (Status = 2 AND ReturnedAt IS NOT NULL AND ReturnedByUserId IS NOT NULL "
                + "AND ReturnReason IS NOT NULL AND ReturnCount >= 1)");
            t.HasCheckConstraint(
                "CK_EditAssignmentRecipient_Reason",
                "ReturnReason IS NULL OR LEN(LTRIM(RTRIM(ReturnReason))) > 0");
        });

        builder.HasKey(x => new { x.AssignmentId, x.UserId });

        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.ReturnReason).HasMaxLength(EditAssignment.MaxNoteLength);
        builder.Property(x => x.ReturnCount).HasDefaultValue((short)0, "DF_EditAssignmentRecipient_RetCnt");

        builder.Property(x => x.AssignedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.FilledAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ReturnedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.StatusChangedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        // «Мої задачі»: передачі користувача за станом (Assigned/Returned — виконати; Filled — очікує).
        builder.HasIndex(x => new { x.UserId, x.Status })
               .HasDatabaseName("IX_EditAssignmentRecipient_User")
               .IncludeProperties(x => x.AssignmentId);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_EditAssignmentRecipient_User");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.AssignedByUserId)
               .HasConstraintName("FK_EditAssignmentRecipient_AssignedBy");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.ReturnedByUserId)
               .HasConstraintName("FK_EditAssignmentRecipient_ReturnedBy");
    }
}

/// <summary>Конфігурація <see cref="EditorSet"/> (<c>doc.EditorSet</c>, EA-1): набір виконавців власника.</summary>
public sealed class EditorSetConfiguration : IEntityTypeConfiguration<EditorSet>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EditorSet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EditorSet", "doc", t => t.HasCheckConstraint(
            "CK_EditorSet_Name", "LEN(LTRIM(RTRIM(Name))) > 0"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(EditorSet.MaxNameLength).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ModifiedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Імена наборів унікальні в межах власника (порівняння — за колацією бази, без урахування регістру).
        builder.HasIndex(x => new { x.OwnerUserId, x.Name })
               .IsUnique().HasDatabaseName("UQ_EditorSet_Owner");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.OwnerUserId)
               .HasConstraintName("FK_EditorSet_Owner");

        builder.HasMany(x => x.Members)
               .WithOne()
               .HasForeignKey(m => m.SetId)
               .HasConstraintName("FK_EditorSetMember_Set");
        builder.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="EditorSetMember"/> (<c>doc.EditorSetMember</c>, EA-1).</summary>
public sealed class EditorSetMemberConfiguration : IEntityTypeConfiguration<EditorSetMember>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EditorSetMember> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EditorSetMember", "doc");
        builder.HasKey(x => new { x.SetId, x.UserId });

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_EditorSetMember_User");
    }
}
