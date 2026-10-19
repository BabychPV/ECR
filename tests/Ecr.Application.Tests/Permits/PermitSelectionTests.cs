// tests/Ecr.Application.Tests/Permits/PermitSelectionTests.cs
using Ecr.Application.Permits.Bindings;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Вибір дозволу за об'єктом і кінцем періоду - чиста функція (Permits, PM-09).</summary>
public sealed class PermitSelectionTests
{
    private static readonly DateOnly End = new(2026, 6, 30);

    [Fact]
    public void Назва_об_єкта_порівнюється_без_регістру_і_пробілів_за_будь_якою_з_назв()
    {
        var site = Permit(1, "Kashagan Field");

        Assert.Equal(1, Choose([site], ["  kashagan field "])?.EntryId);
        Assert.Equal(1, Choose([site], ["KGN", "KASHAGAN FIELD"])?.EntryId);
        Assert.Null(Choose([site], ["Kashagan"]));
        Assert.Null(Choose([site], []));
        Assert.Null(Choose([site], ["  "]));
    }

    [Fact]
    public void Півінтервал_ValidTo_виключно_ValidFrom_включно()
    {
        Assert.Null(Choose([Permit(1, "A", to: End)], ["A"]));
        Assert.Equal(1, Choose([Permit(1, "A", to: End.AddDays(1))], ["A"])?.EntryId);
        Assert.Equal(2, Choose([Permit(2, "A", from: End)], ["A"])?.EntryId);
        Assert.Null(Choose([Permit(2, "A", from: End.AddDays(1))], ["A"]));
    }

    [Fact]
    public void Неактивний_і_видалений_не_беруться()
    {
        Assert.Null(Choose([Permit(1, "A", active: false)], ["A"]));
        Assert.Null(Choose([Permit(1, "A", deleted: true)], ["A"]));
    }

    [Fact]
    public void З_кількох_береться_найпізніший_ValidFrom_а_за_рівності_найбільший_Id()
    {
        var older = Permit(1, "A", from: new DateOnly(2025, 1, 1));
        var newer = Permit(2, "A", from: new DateOnly(2026, 3, 1));
        var open = Permit(3, "A");
        var tie = Permit(4, "A", from: new DateOnly(2026, 3, 1));

        Assert.Equal(2, Choose([older, newer, open], ["A"])?.EntryId);
        Assert.Equal(4, Choose([older, newer, tie], ["A"])?.EntryId);
        Assert.Equal(1, Choose([open, older], ["A"])?.EntryId);
    }

    [Fact]
    public void Чужий_об_єкт_не_підставляється_замість_відсутнього()
        => Assert.Null(Choose([Permit(1, "B"), Permit(2, "C")], ["A"]));

    private static PermitCandidate? Choose(IEnumerable<PermitCandidate> candidates, string[] names)
        => PermitSelection.ChooseByFacility(candidates, names, End);

    private static PermitCandidate Permit(
        long id, string location, DateOnly? from = null, DateOnly? to = null, bool active = true, bool deleted = false)
        => new(id, $"P{id}", location, from, to, active, deleted);
}
