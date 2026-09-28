using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="RecalculationApproval"/> (ФВ-9.7, аудит S1).</summary>
public sealed class RecalculationApprovalConfiguration : IEntityTypeConfiguration<RecalculationApproval>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RecalculationApproval> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RecalculationApproval", "calc", t =>
        {
            // ⛔ Чотири очі — на рівні БАЗИ, як `CK_MV_FourEyes`: правило, яке
            // тримає лише застосунок, обходиться будь-яким скриптом.
            t.HasCheckConstraint(
                "CK_RecalcApproval_FourEyes",
                "ConfirmedByUserId IS NULL OR ConfirmedByUserId <> RequestedByUserId");

            // Використане без підтвердження — стан, якого не має бути ніколи.
            t.HasCheckConstraint(
                "CK_RecalcApproval_UsedConfirmed",
                "UsedAt IS NULL OR ConfirmedByUserId IS NOT NULL");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reason).HasMaxLength(RecalculationApproval.ReasonMaxLength).IsRequired();

        builder.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
               .HasConstraintName("FK_RecalcApproval_Project");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId)
               .HasConstraintName("FK_RecalcApproval_RequestedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ConfirmedByUserId)
               .HasConstraintName("FK_RecalcApproval_ConfirmedBy");

        // Перелік живих погоджень проєкту — єдиний запит читання.
        builder.HasIndex(x => new { x.ProjectId, x.ExpiresAt })
               .HasDatabaseName("IX_RecalcApproval_Project");
    }
}
