using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// R16-1 (RC17): висяча <c>Lookup</c>-колонка/поле шапки БЕЗ довідника (<c>LookupRegistryDefId = null</c>, наприклад з бази,
/// створеної до CK) дає на публікації діагностику <c>lookupRegistryRequired</c>, а не мовчки проходить.
/// </summary>
/// <remarks>
/// Мутація: прибрати гілку <c>LookupRegistryDefId is null</c> у <see cref="PublishChecks.CheckLookupRegistries"/> - обидва тести червоні.
/// </remarks>
public sealed class PublishLookupRegistryRequiredTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Колонка_Lookup_без_довідника_дає_діагностику_lookupRegistryRequired()
    {
        var version = Version(out var table);
        table.AddColumn(new ColumnDef(table.Id, EcrCode.Create("LK_NULL"), Name("LK_NULL"), 1, CellDataType.Lookup));

        var diagnostics = PublishChecks.CheckLookupRegistries(version, new HashSet<int>());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("err.ECR-TMPL-0422.lookupRegistryRequired", diagnostic.MessageKey);
        Assert.Contains("LK_NULL", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Поле_шапки_Lookup_без_довідника_дає_діагностику_headerFieldLookupRegistryRequired()
    {
        var version = Version(out _);
        version.AddHeaderField(new HeaderFieldDef(version.Id, EcrCode.Create("HF_NULL"), Name("HF_NULL"), 1, CellDataType.Lookup));

        var diagnostics = PublishChecks.CheckLookupRegistries(version, new HashSet<int>());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRegistryRequired", diagnostic.MessageKey);
        Assert.Contains("HF_NULL", diagnostic.Message, StringComparison.Ordinal);
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
