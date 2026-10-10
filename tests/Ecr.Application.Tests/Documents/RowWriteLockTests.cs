// tests/Ecr.Application.Tests/Documents/RowWriteLockTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Y7-03 (аудит R11): виняткове блокування аркуша для запису рядків перекладає відмову «зайнято» з ключа подання на
/// ключ «дані зайняті»; чужі відмови не чіпає.
/// </summary>
public sealed class RowWriteLockTests
{
    private static readonly PeriodKey Period = new(202601);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "Y7-03")]
    public async Task Відмова_ключем_подання_перекладається_на_ключ_зайнято_із_тим_самим_кодом_і_деталями()
    {
        var gate = Substitute.For<ISheetEditGate>();
        gate.EnterSubmitAsync(10, 7, Period, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(Busy("err.ECR-DOC-4091.sheetBeingEdited")));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => RowWriteLock.EnterAsync(gate, 10, 7, Period, CancellationToken.None));

        Assert.Equal(ErrorCodes.SheetBusy, error.ErrorCode);
        Assert.Equal("err.ECR-DOC-4091.lockTimeout", error.Details!["messageKey"]);
        Assert.Equal("7", error.Details["sheetDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "Y7-03")]
    public async Task Інша_відмова_і_успіх_проходять_без_змін()
    {
        var gate = Substitute.For<ISheetEditGate>();
        gate.EnterSubmitAsync(10, 7, Period, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(Busy("err.ECR-DOC-4091.sheetBeingSubmitted")));

        var other = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => RowWriteLock.EnterAsync(gate, 10, 7, Period, CancellationToken.None));
        Assert.Equal("err.ECR-DOC-4091.sheetBeingSubmitted", other.Details!["messageKey"]);

        var free = Substitute.For<ISheetEditGate>();
        await RowWriteLock.EnterAsync(free, 10, 7, Period, CancellationToken.None);
        await free.Received(1).EnterSubmitAsync(10, 7, Period, Arg.Any<CancellationToken>());
    }

    private static ConcurrencyConflictException Busy(string messageKey)
        => new(
            ErrorCodes.SheetBusy,
            "зайнято",
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["sheetDefId"] = "7" });
}
