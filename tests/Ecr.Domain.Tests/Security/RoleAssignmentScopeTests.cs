// tests/Ecr.Domain.Tests/Security/RoleAssignmentScopeTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>Область дії призначення ролі (ФВ-6.14): формат і безпечний бік розбору.</summary>
public sealed class RoleAssignmentScopeTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Область_пишеться_як_projects_і_читається_назад()
    {
        var assignment = new RoleAssignment(1, 2, principalSid: null);

        assignment.SetScope(RoleAssignmentScope.Create([7, 3]));

        Assert.Equal("{\"projects\":[3,7]}", assignment.ScopeJson);
        Assert.Equal([3, 7], assignment.ScopedProjectIds());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Без_області_призначення_діє_скрізь()
    {
        var assignment = new RoleAssignment(1, 2, principalSid: null);

        Assert.Null(assignment.ScopeJson);
        Assert.Null(assignment.ScopedProjectIds());
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 1 })]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { -5 })]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Порожня_повторна_чи_недодатна_область_відхиляється(int[] projects)
    {
        var error = Assert.Throws<DomainException>(() => RoleAssignmentScope.Create(projects));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
    }

    /// <summary>
    /// ⛔ Зіпсований або невідомий JSON — роль не діє НІДЕ (порожній перелік),
    /// а не скрізь (<c>null</c>). Невідомий ключ (<c>sheets</c>) не ігнорується:
    /// записане й не застосоване поле розширило б права мовчки.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"projects\":[1],\"sheets\":[5]}")]
    [InlineData("{\"RegionIds\":[1,2]}")]
    [InlineData("{\"projects\":[]}")]
    [InlineData("{\"projects\":[\"1\"]}")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Зіпсована_область_не_дає_нічого(string json)
    {
        Assert.Null(RoleAssignmentScope.TryParse(json));

        var assignment = new RoleAssignment(1, 2, principalSid: null);
        assignment.SetScope(RoleAssignmentScope.Create([1]));
        typeof(RoleAssignment).GetProperty(nameof(RoleAssignment.ScopeJson))!.SetValue(assignment, json);

        Assert.Empty(assignment.ScopedProjectIds()!);
    }

    /// <summary>D-214: аркуші й періоди пишуться лише коли задані й читаються назад.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Аркуші_й_періоди_пишуться_і_читаються_назад()
    {
        var scope = RoleAssignmentScope.Create([2], ["F2", "F1"], new PeriodKey(202601), null);

        Assert.Equal("{\"projects\":[2],\"sheets\":[\"F1\",\"F2\"],\"periods\":{\"from\":202601,\"to\":null}}", scope.ToJson());

        var back = RoleAssignmentScope.TryParse(scope.ToJson())!;
        Assert.Equal([2], back.ProjectIds);
        Assert.Equal(["F1", "F2"], back.SheetCodes);
        Assert.Equal(new PeriodKey(202601), back.PeriodFrom);
        Assert.Null(back.PeriodTo);
        Assert.True(back.IsNarrowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Порожні_аркуші_й_періоди_це_без_обмеження()
    {
        var scope = RoleAssignmentScope.TryParse("{\"projects\":[1],\"sheets\":[],\"periods\":{\"from\":null,\"to\":null}}")!;

        Assert.False(scope.IsNarrowed);
        Assert.Equal("{\"projects\":[1]}", scope.ToJson());
        Assert.True(scope.IncludesSheet("ANY"));
        Assert.True(scope.IncludesPeriod(null));
    }

    [Theory]
    [InlineData(202601, 202606, 202601, true)]
    [InlineData(202601, 202606, 202606, true)]
    [InlineData(202601, 202606, 202512, false)]
    [InlineData(202601, 202606, 202607, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Межі_періодів_включні(int from, int to, int period, bool expected)
    {
        var scope = RoleAssignmentScope.Create([1], null, new PeriodKey(from), new PeriodKey(to));

        Assert.Equal(expected, scope.IncludesPeriod(new PeriodKey(period)));
        Assert.False(scope.IncludesPeriod(null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Аркуш_поза_переліком_і_невідомий_аркуш_поза_областю()
    {
        var scope = RoleAssignmentScope.Create([1], ["F1"]);

        Assert.True(scope.IncludesSheet("F1"));
        Assert.False(scope.IncludesSheet("F2"));
        Assert.False(scope.IncludesSheet(null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Проміжок_перетинає_періоди_проєкту()
    {
        var scope = RoleAssignmentScope.Create([1], null, new PeriodKey(202601), new PeriodKey(202606));

        Assert.True(scope.OverlapsPeriods(new PeriodKey(202512), new PeriodKey(202601)));
        Assert.True(scope.OverlapsPeriods(new PeriodKey(202606), new PeriodKey(202612)));
        Assert.False(scope.OverlapsPeriods(new PeriodKey(202501), new PeriodKey(202512)));
        Assert.False(scope.OverlapsPeriods(new PeriodKey(202607), new PeriodKey(202612)));
    }

    public static TheoryData<string[]?, int?, int?> InvalidNarrowing => new()
    {
        { ["F1", "F1"], null, null },
        { ["F1", "f1"], null, null },
        { ["1bad"], null, null },
        { [""], null, null },
        { null, 189912, null },
        { null, 202600, null },
        { null, null, 202700 },
        { null, 202606, 202601 },
    };

    [Theory]
    [MemberData(nameof(InvalidNarrowing))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Некоректні_аркуші_чи_періоди_відхиляються(string[]? sheets, int? from, int? to)
    {
        var error = Assert.Throws<DomainException>(() => RoleAssignmentScope.Create(
            [1], sheets, from is { } f ? new PeriodKey(f) : null, to is { } t ? new PeriodKey(t) : null));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
    }

    /// <summary>⛔ Зіпсоване звуження в збереженому JSON — роль не діє НІДЕ, а не «без звуження».</summary>
    [Theory]
    [InlineData("{\"projects\":[1],\"sheets\":\"F1\"}")]
    [InlineData("{\"projects\":[1],\"sheets\":[\"F1\",\"F1\"]}")]
    [InlineData("{\"projects\":[1],\"periods\":[202601]}")]
    [InlineData("{\"projects\":[1],\"periods\":{\"from\":\"202601\"}}")]
    [InlineData("{\"projects\":[1],\"periods\":{\"since\":202601}}")]
    [InlineData("{\"projects\":[1],\"periods\":{\"from\":202600}}")]
    [InlineData("{\"projects\":[1],\"periods\":{\"from\":202606,\"to\":202601}}")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Зіпсоване_звуження_не_дає_нічого(string json)
    {
        Assert.Null(RoleAssignmentScope.TryParse(json));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Перенесення_області_дослівне()
    {
        var previous = new RoleAssignment(1, 2, principalSid: null);
        typeof(RoleAssignment).GetProperty(nameof(RoleAssignment.ScopeJson))!.SetValue(previous, "broken");

        var next = new RoleAssignment(1, 2, principalSid: null);
        next.CarryScopeFrom(previous);

        Assert.Equal("broken", next.ScopeJson);
        Assert.Empty(next.ScopedProjectIds()!);
    }
}
