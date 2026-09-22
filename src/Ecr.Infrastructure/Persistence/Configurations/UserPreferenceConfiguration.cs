using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="UserPreference"/> (<c>BE-20</c>).</summary>
public sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⚠ Без CHECK ISJSON: до SQL Server 2022 він відкидає скаляри ("dark", 12),
        // а саме такі значення тут типові. Валідність JSON стереже обробник.
        builder.ToTable("UserPreference", "sec");
        builder.HasKey(x => new { x.UserId, x.Key }).HasName("PK_UserPreference");

        builder.Property(x => x.Key).HasMaxLength(UserPreference.KeyMaxLength).IsUnicode(false);
        builder.Property(x => x.ValueJson).IsRequired();

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.UserId)
               .HasConstraintName("FK_UserPreference_User");
    }
}
