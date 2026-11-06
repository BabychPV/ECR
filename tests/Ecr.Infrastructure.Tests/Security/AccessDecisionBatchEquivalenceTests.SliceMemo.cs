using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// ⚡ Перф зрізу: <c>CanEditSliceAsync</c>/<c>CanStyleSliceAsync</c> рахують рішення один раз на «клас рядка × колонку»
/// (мемо в <c>AccessDecisionService.SliceDecisionsAsync</c>). Тут — що мемо дає РІВНО те саме, що поштучний шлях без
/// мемо (<c>CanEditCellsAsync</c> / <c>CanClearCellsAsync</c> — той самий <c>Decide</c> на кожну адресу), на кожній
/// комірці, кожному профілі й кожному екземплярі строкатого набору.
/// </summary>
public sealed partial class AccessDecisionBatchEquivalenceTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PERF-SLICE")]
    public async Task Мемо_зрізу_тотожне_поштучним_рішенням_для_запису_і_фарбування()
    {
        var world = await ArrangeAsync();

        // Класи рядків, які мемо мусить розрізняти: рядок «лише читання» серед редагованих (базова таблиця A), і
        // колонка «лише читання» (для фарбування ознаки колонки не діють — гілка ignoreColumnFlags). Рядок із вікном
        // чинності дозволу (таблиця SB) — у наборі вже є (`ArrangePermitAsync`).
        await ExecuteAsync("UPDATE cfg.RowDef SET IsReadOnly = 1 WHERE Id = @id", ("@id", world.Base.RowDefIds[1]));
        await ExecuteAsync("UPDATE cfg.ColumnDef SET IsReadOnly = 1 WHERE Id = @id", ("@id", world.Base.ColumnDefIds[2]));

        var reasons = new HashSet<EditDenyReason>();
        var allowed = 0;
        var compared = 0;

        foreach (var (name, profile) in world.Profiles)
        {
            foreach (var instanceId in world.InstanceIds)
            {
                await using var db = world.Builder.CreateContext();
                var service = Service(db);

                var edit = await service.CanEditSliceAsync(profile, instanceId, CancellationToken.None);
                var style = await service.CanStyleSliceAsync(profile, instanceId, CancellationToken.None);

                Assert.NotEmpty(edit);
                Assert.Equal(edit.Keys.OrderBy(Key), style.Keys.OrderBy(Key));

                var period = edit.Keys.First().PeriodKey;
                var editCells = await service.CanEditCellsAsync(profile, instanceId, period, [.. edit.Keys], CancellationToken.None);
                var clearCells = await service.CanClearCellsAsync(profile, instanceId, period, [.. style.Keys], CancellationToken.None);

                AssertRecordsEqual($"запис, профіль «{name}», екземпляр {instanceId}", editCells, edit);
                AssertRecordsEqual($"фарбування, профіль «{name}», екземпляр {instanceId}", clearCells, style);

                foreach (var decision in edit.Values.Concat(style.Values))
                {
                    reasons.Add(decision.Reason);
                    allowed += decision.IsAllowed ? 1 : 0;
                    compared++;
                }
            }
        }

        // ⛔ Проти хибнозеленого: набір мусить справді містити класи, які мемо розрізняє.
        Assert.True(allowed > 0 && compared > allowed, "Набір не перевіряє і дозволу, і відмови.");
        Assert.Contains(EditDenyReason.RowReadOnly, reasons);
        Assert.Contains(EditDenyReason.ColumnReadOnly, reasons);
        Assert.Contains(EditDenyReason.OutsidePermitWindow, reasons);
        Assert.Contains(EditDenyReason.NoGrant, reasons);
        Assert.Contains(EditDenyReason.DocumentSubmitted, reasons);
    }

    /// <summary>Повна рівність записів рішень (усі поля <see cref="EditDecision"/>), набір адрес — той самий.</summary>
    private static void AssertRecordsEqual(
        string what,
        IReadOnlyDictionary<CellAddress, EditDecision> expected,
        IReadOnlyDictionary<CellAddress, EditDecision> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(Key), actual.Keys.OrderBy(Key));

        foreach (var (address, e) in expected)
        {
            Assert.True(e == actual[address], $"{what}, {address}: очікувано {e}, отримано {actual[address]}");
        }
    }
}
