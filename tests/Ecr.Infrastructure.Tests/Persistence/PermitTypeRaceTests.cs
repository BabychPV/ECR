// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Гонка двох запитів на тип дозволу (Permits, PM-03, дозавдання «Аудиту»): другий не лишається голим 500, а дає 409
/// <c>ECR-PRM-4091</c> (заведення) чи <c>ECR-PRM-4093</c> (публікація).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати обидва арми <c>UQ_PermitTypeDef</c>/<c>UQ_RegistryDef</c> у <c>UnitOfWork.TryMapDuplicateKey</c> -
/// червоніє «Два_одночасні_POST…» (<c>DbUpdateException</c> замість 4091).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_одночасні_POST_з_тим_самим_кодом_дають_один_тип_і_409_4091()
    {
        await using var seed = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

        for (var round = 0; round < Rounds; round++)
        {
            var code = $"RC{tag}{round}";
            await using var a = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
            await using var b = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
            var start = new TaskCompletionSource();

            async Task<Exception?> TryAsync(PermitHarness h)
            {
                await start.Task;
                try
                {
                    await h.Create.HandleAsync(new CreatePermitTypeRequest(code, PermitFixtures.Name(code), "Air"), default);
                    return null;
                }
                catch (BusinessRuleException ex)
                {
                    return ex;
                }
            }

            var first = TryAsync(a);
            var second = TryAsync(b);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, r => r is null);
            var failure = Assert.IsType<BusinessRuleException>(Assert.Single(results, r => r is not null));
            Assert.Equal("ECR-PRM-4091", failure.ErrorCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_одночасні_публікації_одного_токена_дають_одну_публікацію_і_409_4093()
    {
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

        for (var round = 0; round < Rounds; round++)
        {
            await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
            var code = $"RP{tag}{round}";
            var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(code, PermitFixtures.Name(code), "Air"), default);
            var table = new PermitTypeTableDto(
                "T", PermitFixtures.Name("T"), 1,
                [new PermitTypeColumnDto("K", PermitFixtures.Name("K"), "String", null, true, true, null, null, null, null, null, null, null, 1)], []);
            await using var put = await h.NextRequestAsync();
            var draft = await put.Replace.HandleAsync(code, new ReplacePermitTypeRequest(created.RowVersion, PermitFixtures.Name(code), "Air", [table], []), default);

            await using var a = await h.NextRequestAsync();
            await using var b = await h.NextRequestAsync();
            var start = new TaskCompletionSource();

            async Task<Exception?> TryAsync(PermitHarness x)
            {
                await start.Task;
                try
                {
                    await x.Publish.HandleAsync(code, new PublishPermitTypeRequest("одночасна публікація", draft.RowVersion), default);
                    return null;
                }
                catch (ConcurrencyConflictException ex)
                {
                    return ex;
                }
            }

            var first = TryAsync(a);
            var second = TryAsync(b);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, r => r is null);
            var failure = Assert.IsType<ConcurrencyConflictException>(Assert.Single(results, r => r is not null));
            Assert.Equal("ECR-PRM-4093", failure.ErrorCode);
        }
    }
}
