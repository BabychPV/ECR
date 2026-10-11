using Ecr.Domain.Entities.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="ApprovalEvent"/> — журналу переходів (<c>BE-11</c>).</summary>
public sealed class ApprovalEventConfiguration : IEntityTypeConfiguration<ApprovalEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApprovalEvent", "wf");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FromStatus).HasConversion<byte>();
        builder.Property(x => x.ToStatus).HasConversion<byte>();
        builder.Property(x => x.Action).HasConversion<byte>();

        // Довжина — як у `RejectedReason`/`ReopenReason` стану: причина
        // копіюється звідти без обрізання.
        builder.Property(x => x.Reason).HasMaxLength(1000);

        // Ті самі ключі й те саме правило видалення (Restrict, глобально в
        // `EcrDbContext`), що й у `wf.ApprovalState`.
        builder.HasOne<Domain.Entities.Documents.Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_ApprEvent_Doc");

        builder.HasOne<Domain.Entities.Configuration.SheetDef>()
               .WithMany()
               .HasForeignKey(x => x.SheetDefId)
               .HasConstraintName("FK_ApprEvent_Sheet");

        // Історію читають по документу за період, від найновішого.
        builder.HasIndex(x => new { x.DocumentId, x.PeriodKey, x.At })
               .IsDescending(false, false, true)
               .HasDatabaseName("IX_ApprovalEvent_Document");

        // X6-03: «хто затвердив» у переліку документів (`DocumentStore`, підзапит `approverDisplayName`) для
        // кожного документа сторінки читає його події за `DocumentId` від найновішої (`At DESC`) із відбором за
        // `Action` і `ByUserId` та вкладеним NOT EXISTS по тому ж документу. Індекс вище має період між
        // `DocumentId` і `At`, тож порядок `At` не віддає, а `Action`/`ByUserId` з нього доводиться добирати
        // з кластерного індексу на кожну подію. Покриваючий (DocumentId, At DESC) INCLUDE (Action, ByUserId)
        // віддає потрібний порядок і поля без key lookup.
        builder.HasIndex(x => new { x.DocumentId, x.At })
               .IsDescending(false, true)
               .IncludeProperties(x => new { x.Action, x.ByUserId })
               .HasDatabaseName("IX_ApprovalEvent_DocumentAt");
    }
}
