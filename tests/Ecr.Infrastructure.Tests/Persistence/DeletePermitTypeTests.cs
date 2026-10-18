// tests/Ecr.Infrastructure.Tests/Persistence/DeletePermitTypeTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Видалення типу дозволу: лише чернетка без дозволів (Permits, PM-03).</summary>
/// <remarks>
/// Мутаційні докази: прибрати перевірку <c>Status != Draft</c> - червоний «Опублікований…»; прибрати підрахунок
/// дозволів - червоний «Тип_із_дозволами…»; прибрати <c>RequireAsync</c> - червоний «Читач…».
/// </remarks>
[Collection("SqlServer")]
public sealed class DeletePermitTypeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Code(string name) => $"{name}{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_без_дозволів_видаляється_разом_з_реєстром_шапки()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(new Ecr.Application.Permits.Dto.CreatePermitTypeRequest(Code("DEL"), Name(), "Air"), default);

        await using var del = await h.NextRequestAsync();
        await del.Delete.HandleAsync(Code("DEL"), default);

        await using var check = await h.NextRequestAsync();
        Assert.Null(await check.Permits.FindByCodeAsync(Code("DEL"), default));
        Assert.False(await check.Db.RegistryDefs.AnyAsync(d => d.Code == $"PRM_{Code("DEL")}"));
        await Assert.ThrowsAsync<NotFoundException>(() => check.Delete.HandleAsync(Code("DEL"), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікований_тип_не_видаляється_ECR_PRM_4094()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(new Ecr.Application.Permits.Dto.CreatePermitTypeRequest(Code("PUB"), Name(), "Air"), default);

        await using (var publish = await h.NextRequestAsync())
        {
            var type = (await publish.Permits.FindByCodeAsync(Code("PUB"), default))!;
            type.Publish(publish.UserId, DateTime.UtcNow);
            await publish.Uow.SaveChangesAsync(default);
        }

        await using var del = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => del.Delete.HandleAsync(Code("PUB"), default));

        Assert.Equal("ECR-PRM-4094", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-4094.published", error.Details!["messageKey"]);

        await using var check = await h.NextRequestAsync();
        Assert.NotNull(await check.Permits.FindByCodeAsync(Code("PUB"), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_із_дозволами_не_видаляється_а_видалений_логічно_дозвіл_не_рахується()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(new Ecr.Application.Permits.Dto.CreatePermitTypeRequest(Code("USE"), Name(), "Air"), default);

        int headerId;
        await using (var seed = await h.NextRequestAsync())
        {
            headerId = (await seed.Permits.FindByCodeAsync(Code("USE"), default))!.HeaderRegistryDefId;
            var live = new RegistryEntry(headerId, EcrCode.Create($"P1_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "P1" }), 1, DateTime.UnixEpoch);
            var gone = new RegistryEntry(headerId, EcrCode.Create($"P2_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "P2" }), 1, DateTime.UnixEpoch);
            gone.SoftDelete();
            seed.Db.RegistryEntries.AddRange(live, gone);
            await seed.Db.SaveChangesAsync();
        }

        await using var del = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => del.Delete.HandleAsync(Code("USE"), default));

        Assert.Equal("err.ECR-PRM-4094.hasPermits", error.Details!["messageKey"]);
        Assert.Equal("1", error.Details["count"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_видаляє_тип()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(new Ecr.Application.Permits.Dto.CreatePermitTypeRequest(Code("RO"), Name(), "Air"), default);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.Delete.HandleAsync(Code("RO"), default));

        await using var check = await h.NextRequestAsync();
        Assert.NotNull(await check.Permits.FindByCodeAsync(Code("RO"), default));
    }

    private static Dictionary<string, string> Name() => new() { ["en"] = "Type" };
}
