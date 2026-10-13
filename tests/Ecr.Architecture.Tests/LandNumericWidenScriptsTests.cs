using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-29: колонки Decimal клону лишались 28/12, а модель вела 34/16 — migrate-version давав guardedWithData.
/// Build-LandTemplate.ps1 тепер підтягує Precision/Scale НАЯВНИХ колонок чернетки-клону до моделі, але лише
/// розширенням (те саме правило, що <c>VersionMigrationPlanner.IsWidening</c>); Verify-LandTemplate.ps1 ловить
/// розбіжність Precision/Scale з моделлю.
/// </summary>
public sealed class LandNumericWidenScriptsTests
{
    private static string Read(string name)
        => File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", name));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_розширює_Precision_Scale_наявних_колонок_клону_лише_розширенням()
    {
        var text = Read("Build-LandTemplate.ps1");

        Assert.Contains("SELECT c.Id, c.TableDefId, c.Code, c.DataType, c.Precision, c.Scale FROM cfg.ColumnDef c", text);
        Assert.Contains("UPDATE cfg.ColumnDef SET Precision=@p, Scale=@s WHERE Id=@i", text);

        // Четверо запобіжників розширення: було без межі; Precision менша; Scale менша; цілих розрядів меншає.
        Assert.Contains("if ($null -eq $hi.precision) { continue }", text);
        Assert.Contains("if ($p1 -lt $hi.precision) { continue }", text);
        Assert.Contains("if ($null -eq $hi.scale -or $s1 -lt $hi.scale) { continue }", text);
        Assert.Contains("if (($p1 - $s1) -lt ($hi.precision - $hi.scale)) { continue }", text);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_ловить_розбіжність_Precision_Scale_з_моделлю()
    {
        var text = Read("Verify-LandTemplate.ps1");

        Assert.Contains("PrecisionChecksPerTable", text);
        Assert.Contains("/columns/$([string]$mcol.code)", text);
        Assert.Contains("[int]$pc.precision -ne [int]$mcol.precision -or $gotScale -ne $wantScale", text);
        Assert.Contains("Precision/Scale: розбіжностей з моделлю", text);
    }
}
