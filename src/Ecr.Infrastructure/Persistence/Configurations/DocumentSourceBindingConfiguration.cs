using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="DocumentSourceBinding"/> (B80 N1, міграція <c>B80DocumentSourceBinding</c>).</summary>
/// <remarks>
/// ⛔ Стан-ворота і політика закриті CHECK-обмеженнями: невідоме значення завантажувач читав би як «будь-який стан»
/// мовчки. ⚠ Імена обмежень — явні (безіменне неможливо прибрати скриптом).
/// </remarks>
public sealed class DocumentSourceBindingConfiguration : IEntityTypeConfiguration<DocumentSourceBinding>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentSourceBinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentSourceBinding", "cfg", t =>
        {
            t.HasCheckConstraint("CK_DocSrcBinding_State", "[SourceState] IN (0, 1)");
            t.HasCheckConstraint("CK_DocSrcBinding_Policy", "[Policy] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_DocSrcBinding_Mapping", "[MappingKind] = 1");
        });
        builder.HasKey(x => x.Id).HasName("PK_DocumentSourceBinding");
        builder.Property(x => x.Code).HasMaxLength(DocumentSourceBinding.CodeMaxLength).IsRequired();
        builder.Property(x => x.SourceTableCodes);
        builder.Property(x => x.Policy).HasConversion<byte>();

        // ⛔ Умовчання схеми — Any: рядок, вставлений будь-ким без стану (сід, ручний INSERT), не вмикає ворота.
        builder.Property(x => x.SourceState).HasConversion<byte>()
               .HasDefaultValue(DocumentSourceState.Any, "DF_DocSrcBinding_State").ValueGeneratedNever();
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_DocumentSourceBinding_Code");
        builder.HasIndex(x => new { x.SourceTemplateId, x.RegistryDefId }).IsUnique()
               .HasDatabaseName("UQ_DocumentSourceBinding_Source");
        builder.HasIndex(x => x.RegistryDefId).HasDatabaseName("IX_DocumentSourceBinding_Registry");

        builder.HasOne<Template>().WithMany().HasForeignKey(x => x.SourceTemplateId)
               .HasConstraintName("FK_DocSrcBinding_Template");
        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_DocSrcBinding_Registry");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId)
               .HasConstraintName("FK_DocSrcBinding_UpdatedBy");

        builder.HasMany(x => x.Consumers).WithOne().HasForeignKey(c => c.BindingId)
               .HasConstraintName("FK_DocSrcBindingConsumer_Binding");
        builder.Navigation(x => x.Consumers).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="DocumentSourceBindingConsumer"/>.</summary>
public sealed class DocumentSourceBindingConsumerConfiguration : IEntityTypeConfiguration<DocumentSourceBindingConsumer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentSourceBindingConsumer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentSourceBindingConsumer", "cfg");
        builder.HasKey(x => new { x.BindingId, x.TemplateId }).HasName("PK_DocumentSourceBindingConsumer");
        builder.HasIndex(x => x.TemplateId).HasDatabaseName("IX_DocSrcBindingConsumer_Template");
        builder.HasOne<Template>().WithMany().HasForeignKey(x => x.TemplateId)
               .HasConstraintName("FK_DocSrcBindingConsumer_Template");
    }
}
