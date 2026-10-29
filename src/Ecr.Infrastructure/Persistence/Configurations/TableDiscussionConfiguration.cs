using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація чату таблиці документа (TC): <c>doc.TableDiscussionMessage</c>, <c>doc.TableDiscussion</c> (зведення),
/// <c>doc.TableDiscussionRead</c>, <c>doc.TableDiscussionMention</c>.
/// </summary>
/// <remarks>
/// ⚠ Усі FK — NO ACTION (конвенція бази): видалення чернетки прибирає чат явно у <c>DocumentDeletionStore</c>.
/// Таблиці не мають жодного зв'язку з <c>doc.CellValue</c> і розрахунками — запис у чат їх не торкається.
/// </remarks>
public sealed class TableDiscussionMessageConfiguration : IEntityTypeConfiguration<TableDiscussionMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableDiscussionMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableDiscussionMessage", "doc", t =>
        {
            t.HasCheckConstraint("CK_TableDiscussionMessage_Body", "LEN(Body) BETWEEN 1 AND 4000");
            t.HasCheckConstraint(
                "CK_TableDiscussionMessage_Period",
                "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99");
            t.HasCheckConstraint(
                "CK_TableDiscussionMessage_Deleted",
                "(DeletedAt IS NULL AND DeletedByUserId IS NULL) OR (DeletedAt IS NOT NULL AND DeletedByUserId IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.TableDefCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(TableDiscussionMessage.MaxBodyLength).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.EditedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.DeletedAt).HasColumnType("datetime2(3)");
        builder.Ignore(x => x.IsDeleted);
        builder.Ignore(x => x.EditableUntil);

        // Ключ пагінації й опитування: чат однієї таблиці за спаданням id; PeriodKey/AuthorUserId/DeletedAt/CreatedAt у листі —
        // фільтр періоду, «непрочитано» (чужі живі) і час останнього живого читаються без звернення до рядка.
        builder.HasIndex(x => new { x.DocumentId, x.TableDefCode, x.Id })
               .IsDescending(false, false, true)
               .HasDatabaseName("IX_TableDiscussionMessage_Chat")
               .IncludeProperties(x => new { x.PeriodKey, x.AuthorUserId, x.CreatedAt, x.DeletedAt });

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_TableDiscussionMessage_Document");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.AuthorUserId)
               .HasConstraintName("FK_TableDiscussionMessage_Author");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.DeletedByUserId)
               .HasConstraintName("FK_TableDiscussionMessage_DeletedBy");
    }
}

/// <summary>Конфігурація зведення чату (<c>doc.TableDiscussion</c>).</summary>
public sealed class TableDiscussionStateConfiguration : IEntityTypeConfiguration<TableDiscussionState>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableDiscussionState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableDiscussion", "doc");
        builder.HasKey(x => new { x.DocumentId, x.TableDefCode });
        builder.Property(x => x.TableDefCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LastMessageAt).HasColumnType("datetime2(3)");

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_TableDiscussion_Document");
    }
}

/// <summary>Конфігурація позначки «прочитано» (<c>doc.TableDiscussionRead</c>).</summary>
public sealed class TableDiscussionReadConfiguration : IEntityTypeConfiguration<TableDiscussionRead>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableDiscussionRead> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableDiscussionRead", "doc");
        builder.HasKey(x => new { x.UserId, x.DocumentId, x.TableDefCode });
        builder.Property(x => x.TableDefCode).HasMaxLength(64).IsRequired();

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_TableDiscussionRead_User");

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_TableDiscussionRead_Document");
    }
}

/// <summary>Конфігурація згадок (<c>doc.TableDiscussionMention</c>).</summary>
public sealed class TableDiscussionMentionConfiguration : IEntityTypeConfiguration<TableDiscussionMention>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableDiscussionMention> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableDiscussionMention", "doc");
        builder.HasKey(x => new { x.MessageId, x.UserId });
        builder.Property(x => x.TableDefCode).HasMaxLength(64).IsRequired();

        // «Мої задачі» і підсумок: згадки користувача (за документом і таблицею).
        builder.HasIndex(x => new { x.UserId, x.DocumentId, x.TableDefCode })
               .HasDatabaseName("IX_TableDiscussionMention_User");

        builder.HasOne<TableDiscussionMessage>()
               .WithMany()
               .HasForeignKey(x => x.MessageId)
               .HasConstraintName("FK_TableDiscussionMention_Message");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_TableDiscussionMention_User");
    }
}
