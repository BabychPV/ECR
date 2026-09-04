using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="FormulaDef"/>.</summary>
/// <remarks>
/// ⚠ Таблиця має тригер незмінності — <c>HasTrigger</c> обов'язковий.
/// <c>EvaluationOrder</c> заповнюється при <c>Publish</c>, а не в рантаймі
/// (ФВ-9.4): це результат топологічного сортування, а не поле, яке хтось
/// виставляє руками.
/// </remarks>
public sealed class FormulaDefConfiguration : IEntityTypeConfiguration<FormulaDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FormulaDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FormulaDef", "cfg", t =>
        {
            t.HasTrigger("TR_FormulaDef_Immutable");
            t.HasCheckConstraint(
                "CK_Formula_Scope",
                "(Scope = 0 AND ColumnDefId IS NOT NULL) OR " +
                "(Scope = 1 AND RowDefId IS NOT NULL) OR " +
                "(Scope = 2 AND ColumnDefId IS NOT NULL AND RowDefId IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Scope).HasConversion<byte>();
        builder.Property(x => x.Dialect).HasConversion<byte>();
        builder.Property(x => x.Expression).HasMaxLength(2000).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.Dialect).HasDefaultValueSql("0", "DF_Formula_Dialect");
        builder.Property(x => x.EvaluationOrder).HasDefaultValue(0, "DF_Formula_Order");
        builder.Property(x => x.IsCrossSheet).HasDefaultValue(false, "DF_Formula_Cross");
        builder.Property(x => x.IsSnapshot).HasDefaultValue(false, "DF_Formula_Snap");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_Formula_Del");

        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_Formula_Table");
    }
}

/// <summary>Конфігурація <see cref="FormulaDependency"/>.</summary>
/// <remarks>
/// Зворотний індекс по цій таблиці — основа інкрементного перерахунку.
/// <c>RowKey</c> тут завжди <b>конкретний</b>: діапазони розкриваються при
/// <c>Publish</c>, у рантаймі їх не існує (B03 §4).
/// </remarks>
public sealed class FormulaDependencyConfiguration : IEntityTypeConfiguration<FormulaDependency>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FormulaDependency> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FormulaDependency", "cfg", t => t.HasCheckConstraint(
            "CK_FDep_Source",
            "(SourceKind = 0 AND FormulaDefId IS NOT NULL) OR (SourceKind = 1 AND BindingId IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RowKey).HasMaxLength(100);
        builder.Property(x => x.FilterJson).HasColumnType("nvarchar(max)");

        builder.HasOne<FormulaDef>().WithMany().HasForeignKey(x => x.FormulaDefId)
               .HasConstraintName("FK_FDep_Formula");

        // Індекс під зворотний пошук «які формули залежать від цієї комірки».
        builder.HasIndex(x => new { x.TableDefId, x.RowKey, x.ColumnDefId })
               .HasDatabaseName("IX_FormulaDependency_Reverse");
    }
}

/// <summary>Конфігурація <see cref="ValidationRule"/>.</summary>
public sealed class ValidationRuleConfiguration : IEntityTypeConfiguration<ValidationRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ValidationRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ValidationRule", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Severity).HasConversion<byte>();
        builder.Property(x => x.Expression).HasMaxLength(2000).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_VRule_Active");
        builder.HasIndex(x => new { x.TableDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_ValidationRule");
    }
}

/// <summary>Конфігурація <see cref="TableRelationDef"/>.</summary>
public sealed class TableRelationDefConfiguration : IEntityTypeConfiguration<TableRelationDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TableRelationDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TableRelationDef", "cfg", t => t.HasCheckConstraint(
            "CK_Rel_NotSelf", "SourceTableDefId <> TargetTableDefId"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RelationKind).HasConversion<byte>();
        builder.Property(x => x.MatchJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.MapJson).HasColumnType("nvarchar(max)");

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.OnSourceChange).HasDefaultValueSql("0", "DF_Rel_OnChange");
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_Rel_Active");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_TableRelationDef");
    }
}

/// <summary>Конфігурація <see cref="PeriodAccessRuleDef"/>.</summary>
public sealed class PeriodAccessRuleDefConfiguration : IEntityTypeConfiguration<PeriodAccessRuleDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PeriodAccessRuleDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PeriodAccessRuleDef", "cfg", t =>
        {
            t.HasCheckConstraint("CK_PAR_Target", "SheetDefId IS NOT NULL OR TableDefId IS NOT NULL");
            t.HasCheckConstraint(
                "CK_PAR_Range",
                "FromSequence IS NULL OR ToSequence IS NULL OR FromSequence <= ToSequence");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OnOutOfWindow).HasConversion<byte>();
    }
}

/// <summary>Конфігурація <see cref="SheetGroupRule"/>.</summary>
public sealed class SheetGroupRuleConfiguration : IEntityTypeConfiguration<SheetGroupRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SheetGroupRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SheetGroupRule", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SheetGroup).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TargetGroup).HasMaxLength(64);
    }
}

/// <summary>Конфігурація <see cref="RegistryDef"/>.</summary>
/// <remarks>
/// <c>DataRevision</c> і <c>DefinitionVersion</c> — різні осі (ФВ-13.2):
/// перша росте від зміни <b>записів</b>, друга — від зміни <b>складу полів</b>.
/// </remarks>
public sealed class RegistryDefConfiguration : IEntityTypeConfiguration<RegistryDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryDef", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceKind).HasConversion<byte>();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsTemporal).HasDefaultValue(false, "DF_RegDef_Temp");
        builder.Property(x => x.SourceKind).HasDefaultValueSql("2", "DF_RegDef_Src");
        builder.Property(x => x.DataRevision).HasDefaultValue(0, "DF_RegDef_Rev");
        builder.Property(x => x.DefinitionVersion).HasDefaultValue(1, "DF_RegDef_Ver");
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_RegDef_Act");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_RegistryDef");
        builder.Navigation(x => x.Fields).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Конфігурація <see cref="RegistryFieldDef"/>.</summary>
public sealed class RegistryFieldDefConfiguration : IEntityTypeConfiguration<RegistryFieldDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryFieldDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryFieldDef", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DataType).HasConversion<byte>();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsRequired).HasDefaultValue(false, "DF_RegField_Req");
        builder.Property(x => x.IsKey).HasDefaultValue(false, "DF_RegField_Key");
        builder.HasIndex(x => new { x.RegistryDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_RegistryFieldDef");
        // .WithMany(r => r.Fields) обов'язково: інакше RegistryDef.Fields стає
        // другим зв'язком і тягне за собою тіньову колонку RegistryDefId1.
        builder.HasOne<RegistryDef>().WithMany(r => r.Fields).HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegField_Registry");
    }
}

/// <summary>Конфігурація <see cref="CalculationBinding"/>.</summary>
public sealed class CalculationBindingConfiguration : IEntityTypeConfiguration<CalculationBinding>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CalculationBinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalculationBinding", "cfg");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OutputCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.MatchJson).HasColumnType("nvarchar(max)").IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_CalcBind_Act");
        builder.HasIndex(x => new { x.ColumnDefId, x.MethodologyId, x.OutputCode })
               .IsUnique().HasDatabaseName("UQ_CalculationBinding");
    }
}

/// <summary>Конфігурація <see cref="Dimension"/>.</summary>
public sealed class DimensionConfiguration : IEntityTypeConfiguration<Dimension>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Dimension> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Dimension", "uom", t => t.HasCheckConstraint(
            "CK_Dim_Derived",
            "IsDerived = 0 OR (NumeratorDimensionId IS NOT NULL AND DenominatorDimensionId IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsDerived).HasDefaultValue(false, "DF_Dim_Derived");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Dimension_Code");
    }
}

/// <summary>Конфігурація <see cref="Unit"/>.</summary>
/// <remarks>
/// <c>FactorToBase</c> і <c>OffsetToBase</c> — <see cref="decimal"/>, не
/// <c>float</c>: похибка коефіцієнта множиться на кожне значення у звіті
/// (<c>D-30</c>).
/// </remarks>
public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Unit", "uom", t =>
        {
            t.HasCheckConstraint("CK_Unit_Factor", "FactorToBase <> 0");
            t.HasCheckConstraint("CK_Unit_Base", "IsBase = 0 OR (FactorToBase = 1 AND OffsetToBase = 0)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();

        // decimal(38,18), а не (28,12): коефіцієнт множиться на кожне значення
        // у звіті, і зрізана 13-та цифра стає розбіжністю в тоннах (D-30).
        builder.Property(x => x.FactorToBase).HasPrecision(38, 18);
        builder.Property(x => x.OffsetToBase).HasPrecision(38, 18);
        builder.Property(x => x.DisplayFormat).HasMaxLength(50);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsBase).HasDefaultValue(false, "DF_Unit_Base");
        builder.Property(x => x.FactorToBase).HasDefaultValue(1m, "DF_Unit_Factor");
        builder.Property(x => x.OffsetToBase).HasDefaultValue(0m, "DF_Unit_Offset");
        builder.Property(x => x.IsActive).HasDefaultValue(true, "DF_Unit_Active");
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Unit_Code");

        // Базова одиниця в розмірності може бути лише одна: дві означали б два
        // різні «нулі» для перетворень (ФВ-16.2).
        builder.HasIndex(x => x.DimensionId)
               .IsUnique()
               .HasFilter("[IsBase] = 1")
               .HasDatabaseName("UX_Unit_BasePerDimension");

        builder.HasOne<Dimension>().WithMany().HasForeignKey(x => x.DimensionId)
               .HasConstraintName("FK_Unit_Dimension");
    }
}

/// <summary>Конфігурація <see cref="UnitConversion"/>.</summary>
/// <remarks>
/// ⚠ Конверсія існує <b>лише в межах однієї розмірності</b>. Перехід
/// «маса ↔ об'єм» сюди не потрапляє ніколи: це контекстний коефіцієнт
/// (щільність), і він живе в константах методології (ФВ-16.5, <c>D-75</c>).
/// </remarks>
public sealed class UnitConversionConfiguration : IEntityTypeConfiguration<UnitConversion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitConversion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Conversion", "uom", t =>
        {
            t.HasCheckConstraint("CK_Conv_NotSelf", "FromUnitId <> ToUnitId");
            t.HasCheckConstraint("CK_Conv_Note", "Kind <> 1 OR Note IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Factor).HasPrecision(38, 18);
        builder.Property(x => x.Offset).HasPrecision(38, 18);
        builder.Property(x => x.Note).HasMaxLength(400);

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.Offset).HasDefaultValue(0m, "DF_Conv_Offset");
        builder.HasIndex(x => new { x.FromUnitId, x.ToUnitId })
               .IsUnique().HasDatabaseName("UQ_Conversion");
    }
}
