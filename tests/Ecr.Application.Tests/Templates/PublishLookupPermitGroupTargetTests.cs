using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Permits PRMG: публікація версії, де колонка/поле шапки Lookup вказує на ГРУПУ дозволів (прихований реєстр <c>PRMG_*</c>
/// без записів). Перевірки публікації читають лише ІСНУВАННЯ й активність цілі, а не її записи, тож група проходить як
/// звичайний довідник, а неактивна група - відмова <c>lookupRegistryUnknown</c> / <c>headerFieldLookupRegistryUnknown</c>.
/// </summary>
/// <remarks>
/// Мутація: у <c>PublishChecks.LookupRegistryIds</c> відфільтрувати ціль-групу - перший тест червоний; у
/// <c>CheckLookupRegistries</c> пропускати цілі поза <c>usableRegistryIds</c> - тест неактивної групи червоний.
/// REGSUM/REGFIELD над колонкою-групою у v1 не підтримуються - це відмова публікації на стороні Permits (поза цим тестом).
/// </remarks>
public sealed class PublishLookupPermitGroupTargetTests
{
    private const int GroupRegistry = 900;
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ціль_група_колонки_і_шапки_збирається_й_проходить_як_активний_довідник()
    {
        var version = Version(out var table);
        var column = new ColumnDef(table.Id, EcrCode.Create("PermitNumber"), Name("PermitNumber"), 1, CellDataType.Lookup);
        column.SetLookup(GroupRegistry);
        table.AddColumn(column);
        var field = new HeaderFieldDef(version.Id, EcrCode.Create("Permit"), Name("Permit"), 1, CellDataType.Lookup);
        field.SetLookup(GroupRegistry);
        version.AddHeaderField(field);

        Assert.Equal([GroupRegistry], PublishChecks.LookupRegistryIds(version));
        Assert.Empty(PublishChecks.CheckLookupRegistries(version, new HashSet<int> { GroupRegistry }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Неактивна_група_відмовляє_для_колонки_і_поля_шапки()
    {
        var version = Version(out var table);
        var column = new ColumnDef(table.Id, EcrCode.Create("PermitNumber"), Name("PermitNumber"), 1, CellDataType.Lookup);
        column.SetLookup(GroupRegistry);
        table.AddColumn(column);
        var field = new HeaderFieldDef(version.Id, EcrCode.Create("Permit"), Name("Permit"), 1, CellDataType.Lookup);
        field.SetLookup(GroupRegistry);
        version.AddHeaderField(field);

        var diagnostics = PublishChecks.CheckLookupRegistries(version, new HashSet<int>());

        Assert.Equal(
            ["err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown", "err.ECR-TMPL-0422.lookupRegistryUnknown"],
            diagnostics.Select(d => d.MessageKey).Order(StringComparer.Ordinal));
    }

    private static TemplateVersion Version(out TableDef table)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, Now);
        var sheet = new SheetDef(version.Id, EcrCode.Create("S1"), Name("S1"), 1);
        table = new TableDef(sheet.Id, EcrCode.Create("T1"), Name("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        sheet.AddTable(table);
        version.AddSheet(sheet);

        return version;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
