using Ecr.Adapters.PiAf;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// `DefaultCatalogQuery` читає точні назви колонок RTQP (`Q-197`).
/// </summary>
/// <remarks>
/// ⚠ Раніше запит читав <c>a.UOM</c> і <c>a.Type</c> з
/// <c>[Master].[Element].[Attribute]</c> — обох колонок під цими іменами
/// не існує ні за офіційною AVEVA PI SQL DAS (RTQP Engine) Reference, ні за
/// продуктивним експортом NCOC. Правильні назви — <c>UnitOfMeasure</c> і
/// <c>ValueType</c>.
/// <para>
/// ⛔ Перевірки навмисно кваліфіковані префіксом <c>a.</c> і крапкою:
/// голий <see cref="string.Contains(string)"/> на <c>"Type"</c> дав би
/// хибнопозитивний зелений тест, бо <c>"ValueType"</c> сам містить підрядок
/// <c>"Type"</c> — тест мовчав би навіть на зламаному запиті.
/// </para>
/// </remarks>
public sealed class PiSqlClientDataSourceQueryTests
{
    [Fact]
    public void DefaultCatalogQuery_ReadsUnitOfMeasureColumn()
    {
        Assert.Contains("a.UnitOfMeasure", PiSqlClientDataSource.DefaultCatalogQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCatalogQuery_ReadsValueTypeColumn()
    {
        Assert.Contains("a.ValueType", PiSqlClientDataSource.DefaultCatalogQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCatalogQuery_DoesNotReadNonExistentUomColumn()
    {
        Assert.DoesNotContain("a.UOM", PiSqlClientDataSource.DefaultCatalogQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCatalogQuery_DoesNotReadNonExistentTypeColumn()
    {
        Assert.DoesNotContain("a.Type", PiSqlClientDataSource.DefaultCatalogQuery, StringComparison.Ordinal);
    }
}
