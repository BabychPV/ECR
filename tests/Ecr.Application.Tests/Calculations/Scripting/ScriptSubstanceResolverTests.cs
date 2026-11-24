using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// c11-10: код речовини скрипта → запис довідника без мовчазного «меншого Id»: речовини версії, єдиний кандидат, довідник,
/// який уже вживає методологія; інакше — неоднозначно.
/// </summary>
/// <remarks>
/// Мутації: брати перший кандидат замість неоднозначності — червоніє <c>Два_довідники_без_переваги_методології_неоднозначні</c>;
/// не дивитись на речовини версії — червоніє <c>Речовина_версії_важливіша_за_вподобання_методології</c>.
/// </remarks>
public sealed class ScriptSubstanceResolverTests
{
    private static readonly Dictionary<long, string> NoOwn = [];

    private static SubstanceCandidate Hse(string code, long id, bool used = false) => new(code, id, "C" + code, "LAND_HSE400_SUBSTANCE", used);

    private static SubstanceCandidate Air(string code, long id, bool used = false) => new(code, id, "E" + code, "LAND_AIR_SUBSTANCE", used);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Два_довідники_без_переваги_методології_неоднозначні()
    {
        var r = ScriptSubstanceResolver.Resolve(["333"], NoOwn, [Hse("333", 642), Air("333", 6044)]);

        Assert.Empty(r.Found);
        Assert.Equal([642L, 6044L], r.Ambiguous["333"].Select(c => c.EntryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Довідник_який_уже_вживає_методологія_знімає_неоднозначність_а_єдиний_кандидат_береться_як_є()
    {
        var r = ScriptSubstanceResolver.Resolve(
            ["333", "416", "999"], NoOwn, [Hse("333", 642), Air("333", 6044, used: true), Hse("416", 652)]);

        Assert.Equal(6044, r.Found["333"]);
        Assert.Equal(652, r.Found["416"]);
        Assert.False(r.Found.ContainsKey("999"));
        Assert.Empty(r.Ambiguous);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Речовина_версії_важливіша_за_вподобання_методології()
    {
        var r = ScriptSubstanceResolver.Resolve(
            ["333"], new Dictionary<long, string> { [6044] = "333" }, [Hse("333", 642, used: true), Air("333", 6044, used: true)]);

        Assert.Equal(6044, r.Found["333"]);
        Assert.Empty(r.Ambiguous);
    }
}
