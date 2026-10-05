// tests/Ecr.Infrastructure.Tests/Persistence/PatchCellsWorkbookTests.LockOrder.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-36b (рев'ю AN-36, P3-2): книжковий шлях без утриманих викликачем блокувань бере
/// блокування структури документа спільно, один раз і ПЕРШИМ — до аркушів (L6-02).
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>EnterStructureAsync</c> у <c>PersistWorkbookAsync</c> — першим
/// стоїть аркуш, тест червоний. З утриманими блокуваннями (<c>heldSheetStatuses</c>,
/// як в імпорті) структуру вже взяв викликач — другого звернення немає.
/// </remarks>
public sealed partial class PatchCellsWorkbookTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Книга_бере_структуру_документа_першою_і_лише_коли_її_не_взяв_викликач(bool held)
    {
        var world = await ArrangeAsync([2, 2]);
        var requests = Requests(world, await VersionsAsync(world));
        RecordingGate? gate = null;

        _ = await RunWorkbookAsync(
            world, Writer(world), requests,
            gate: inner => gate = new RecordingGate(inner),
            passHeldStatuses: held);

        Assert.NotNull(gate);
        if (held)
        {
            Assert.DoesNotContain(gate.Calls, c => c.StartsWith("structure", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal("structure:S", gate.Calls[0]);
            Assert.Single(gate.Calls, c => c.StartsWith("structure", StringComparison.Ordinal));
        }
    }

    /// <summary>Справжні ворота, що записують порядок своїх викликів.</summary>
    private sealed class RecordingGate(ISheetEditGate inner) : ISheetEditGate
    {
        public List<string> Calls { get; } = [];

        public Task<DocumentStatus> EnterEditAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            Calls.Add("sheet:S");
            return inner.EnterEditAsync(documentId, sheetDefId, periodKey, ct);
        }

        public Task EnterSubmitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            Calls.Add("sheet:X");
            return inner.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct);
        }

        public Task<int?> EnterStructureAsync(long documentId, bool exclusive, CancellationToken ct)
        {
            Calls.Add(exclusive ? "structure:X" : "structure:S");
            return inner.EnterStructureAsync(documentId, exclusive, ct);
        }

        public Task EnterHeaderAsync(long documentId, bool exclusive, CancellationToken ct)
        {
            Calls.Add(exclusive ? "header:X" : "header:S");
            return inner.EnterHeaderAsync(documentId, exclusive, ct);
        }
    }
}
