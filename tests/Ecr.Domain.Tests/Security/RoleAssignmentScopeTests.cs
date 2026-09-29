// tests/Ecr.Domain.Tests/Security/RoleAssignmentScopeTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
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
