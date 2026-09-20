// tests/Ecr.Domain.Tests/External/CollectionScheduleStateTests.cs
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>Стан постановки розкладу збору (<c>LastError</c>) — без бази.</summary>
public sealed class CollectionScheduleStateTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Новий_розклад_помилки_не_має_а_позначений_тримає_причину_й_момент()
    {
        var schedule = new CollectionSchedule(7, "0 5 * * * ?");
        Assert.Null(schedule.LastError);
        Assert.Null(schedule.LastErrorAt);

        schedule.MarkInvalid("  Unexpected end of expression.  ", Now);

        Assert.Equal("Unexpected end of expression.", schedule.LastError);
        Assert.Equal(Now, schedule.LastErrorAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довга_причина_обрізається_до_ширини_стовпця()
    {
        var schedule = new CollectionSchedule(7, "0 5 * * * ?");

        schedule.MarkInvalid(new string('x', 401), Now);

        // ⛔ Числом: ширина — контракт зі стовпцем `nvarchar(400)`, а не з константою.
        Assert.Equal(400, schedule.LastError!.Length);

        schedule.MarkInvalid(new string('y', 400), Now);
        Assert.Equal(400, schedule.LastError!.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Очищення_знімає_і_причину_і_момент_а_порожня_причина_відхиляється()
    {
        var schedule = new CollectionSchedule(7, "0 5 * * * ?");
        schedule.MarkInvalid("bad", Now);

        schedule.ClearError();

        Assert.Null(schedule.LastError);
        Assert.Null(schedule.LastErrorAt);

        // «Помилка без тексту» в інтерфейсі виглядала б як справний розклад із датою збою.
        Assert.Throws<ArgumentException>(() => schedule.MarkInvalid(" ", Now));
    }
}
