using Ecr.Domain.Entities.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="NotificationChannel"/> (<c>BE-32</c>).</summary>
/// <remarks>
/// ⚠ Перша таблиця <c>sys_ecr</c> у моделі EF. Решта схеми живе у
/// <c>08-system-tables.sql</c>, бо не має доменних сутностей; ця — має. Схему
/// створює той, хто прийде першим: і міграція, і скрипт роблять це під
/// <c>IF SCHEMA_ID(...) IS NULL</c>.
/// </remarks>
public sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationChannel", "sys_ecr", t => t.HasCheckConstraint(
            "CK_NotificationChannel_Kind", "Kind IN (1, 2)"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Kind).HasConversion<byte>();
        builder.Property(x => x.Name).HasMaxLength(NotificationChannel.NameMaxLength).IsRequired();
        builder.Property(x => x.IsEnabled).HasDefaultValue(true);
        builder.Property(x => x.SettingsJson).IsRequired();
        builder.Property(x => x.SecretProtected).HasColumnType("varbinary(max)");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.Ignore(x => x.HasSecret);

        // Назва — те, за чим адміністратор розрізняє канали в матриці правил.
        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_NotificationChannel_Name");
    }
}

/// <summary>Конфігурація <see cref="NotificationRule"/> (<c>BE-32</c>).</summary>
public sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationRule", "sys_ecr");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventKind).HasConversion<byte>();
        builder.Property(x => x.MinSeverity).HasConversion<byte>();
        builder.Property(x => x.IsEnabled).HasDefaultValue(true);

        // Видалення — Restrict (глобально в `EcrDbContext`): канал із правилами
        // не зникає мовчки, правила прибирає той, хто видаляє канал.
        builder.HasOne<NotificationChannel>()
               .WithMany()
               .HasForeignKey(x => x.ChannelId)
               .HasConstraintName("FK_NotificationRule_Channel");

        // Клітинка матриці «подія × канал» одна.
        builder.HasIndex(x => new { x.EventKind, x.ChannelId })
               .IsUnique().HasDatabaseName("UQ_NotificationRule_EventChannel");
        builder.HasIndex(x => x.ChannelId).HasDatabaseName("IX_NotificationRule_Channel");
    }
}

/// <summary>Конфігурація <see cref="NotificationDelivery"/> — журналу доставок (<c>BE-32</c>).</summary>
public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationDelivery", "itg", t => t.HasCheckConstraint(
            "CK_NotificationDelivery_Status", "Status IN (1, 2, 3)"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventKind).HasConversion<byte>();
        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.EventKey).HasMaxLength(NotificationDelivery.EventKeyMaxLength).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(NotificationDelivery.ErrorMaxLength);

        // ⚠ Зовнішнього ключа на канал немає навмисно — див. сутність.
        // Індекс — під дедуплікацію (`BE-34`): «чи йшов цей EventKey у цей
        // канал за останні 30 хв». Перелік останніх доставок іде за PK.
        builder.HasIndex(x => new { x.ChannelId, x.EventKey, x.At })
               .IsDescending(false, false, true)
               .HasDatabaseName("IX_NotificationDelivery_Dedup");
    }
}
