// tests/Ecr.Infrastructure.Tests/Persistence/PermitNumberRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Гонка створення й перейменування дозволів (аудит, прохід A, PA-A-07): двадцять паралельних <c>POST /permits</c> з тим самим
/// типом, номером і початком створювали двадцять чернеток; тепер один виграє, решта дістає 409 <c>ECR-PRM-4095</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>stats.LockNumberAsync</c> у <c>CreatePermitHandler</c> - червоніє «Паралельні_створення…»
/// (кілька 201); прибрати блок замка в <c>UpdatePermitHandler</c> - червоніє «Паралельне_перейменування…».
/// ⚠ Обидва тести - гонки справжніх обробників над живою базою: вікно між перевіркою й записом недетерміноване, тож тест
/// ганяє <see cref="Rounds"/> раундів; зелений без замка бути не може на жодному раунді, де обидва встигли прочитати.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitNumberRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;
    private const int Parallel = 10;
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2027, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"NR{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельні_створення_з_тим_самим_типом_номером_і_початком_дають_одну_чернетку_і_409_4095()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        for (var round = 0; round < Rounds; round++)
        {
            var number = $"NR-{round}";
            var requests = new List<PermitHarness>();
            for (var i = 0; i < Parallel; i++)
            {
                requests.Add(await h.NextRequestAsync());
            }

            try
            {
                var start = new TaskCompletionSource();

                async Task<Exception?> TryAsync(PermitHarness x)
                {
                    await start.Task;
                    try
                    {
                        await x.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, null, null, From, To, null), default);
                        return null;
                    }
                    catch (ConcurrencyConflictException ex)
                    {
                        return ex;
                    }
                }

                var attempts = requests.Select(TryAsync).ToList();
                start.SetResult();
                var results = await Task.WhenAll(attempts);

                Assert.Single(results, r => r is null);
                Assert.All(results.OfType<ConcurrencyConflictException>(), e => Assert.Equal("ECR-PRM-4095", e.ErrorCode));
                Assert.Equal(Parallel - 1, results.OfType<ConcurrencyConflictException>().Count());

                await using var read = await h.NextRequestAsync();
                var listed = await read.ListPermits.HandleAsync(new Ecr.Application.Common.CursorRequest(500), TypeCode, "Draft", null, null, default);
                Assert.Single(listed.Items, p => p.Number == number);
            }
            finally
            {
                foreach (var request in requests)
                {
                    await request.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельне_перейменування_двох_дозволів_на_один_номер_дає_один_успіх_і_409_4095()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        for (var round = 0; round < Rounds; round++)
        {
            var first = await CreateAsync(h, $"A-{round}");
            var second = await CreateAsync(h, $"B-{round}");
            var target = $"T-{round}";

            await using var a = await h.NextRequestAsync();
            await using var b = await h.NextRequestAsync();
            var start = new TaskCompletionSource();

            async Task<Exception?> TryAsync(PermitHarness x, PermitDto permit)
            {
                await start.Task;
                try
                {
                    await x.UpdatePermit.HandleAsync(
                        permit.Id, new UpdatePermitRequest(permit.RowVersion, target, null, null, From, To, null, null), default);
                    return null;
                }
                catch (ConcurrencyConflictException ex)
                {
                    return ex;
                }
            }

            var one = TryAsync(a, first);
            var two = TryAsync(b, second);
            start.SetResult();
            var results = await Task.WhenAll(one, two);

            Assert.Single(results, r => r is null);
            var failure = Assert.IsType<ConcurrencyConflictException>(Assert.Single(results, r => r is not null));
            Assert.Equal("ECR-PRM-4095", failure.ErrorCode);
        }
    }

    private async Task<PermitDto> CreateAsync(PermitHarness h, string number)
    {
        await using var request = await h.NextRequestAsync();
        return await request.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, null, null, From, To, null), default);
    }
}
