// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.LockOrder.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// AN-36b (рев'ю AN-36, P3-2): подання бере блокування документа в єдиному порядку
/// «структура спільно → шапка спільно → аркуш винятково».
/// </summary>
/// <remarks>
/// ⛔ Що ловить. Подання без блокування шапки (L6-06) чи структури (L6-02) не
/// червонить жоден інший тест: храповик звернень перевіряє лише стелю, а тести
/// на двох з'єднаннях моделюють подання сирими викликами воріт. Тут — справжній
/// <c>SubmitSheetHandler</c> над справжнім <c>SheetEditGate</c>, порядок записує декоратор.
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-06")]
    public async Task Подання_бере_структуру_шапку_й_аркуш_у_єдиному_порядку()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        var gate = new RecordingGate(new SheetEditGate(db));
        await Submit(world, db, Access(), gate)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(["structure:S", "header:S", "sheet:X"], gate.Calls);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
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
