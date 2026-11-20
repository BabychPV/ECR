using Ecr.Infrastructure.Jobs;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// PSS-16: книга експорту читається в один масив, а не потік → MemoryStream → ToArray().
/// </summary>
public sealed class ExcelExportJobBufferTests
{
    private const int Size = 8 * 1024 * 1024;

    [Fact]
    public async Task ReadAllBytes_Seekable_ReturnsSameBytesFromCurrentPosition()
    {
        var src = new byte[Size];
        new Random(7).NextBytes(src);
        await using var stream = new MemoryStream(src);
        stream.Position = 5;

        var result = await ExcelExportJob.ReadAllBytesAsync(stream, CancellationToken.None);

        Assert.Equal(src.AsSpan(5).ToArray(), result);
    }

    [Fact]
    public async Task ReadAllBytes_Seekable_AllocatesOneCopy_NotThree()
    {
        await using var stream = new MemoryStream(new byte[Size]);
        // Прогрів JIT, щоб не рахувати його алокації.
        await ExcelExportJob.ReadAllBytesAsync(new MemoryStream(new byte[16]), CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await ExcelExportJob.ReadAllBytesAsync(stream, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Size, result.Length);
        // Одна копія + дрібниця. Старий шлях: буфер MemoryStream (зростання подвоєнням) + ToArray() ≥ 3×.
        Assert.True(allocated < Size * 1.25, $"allocated {allocated} for {Size}");
    }

    [Fact]
    public async Task ReadAllBytes_NonSeekable_StillReadsEverything()
    {
        var src = new byte[100_000];
        new Random(3).NextBytes(src);
        await using var stream = new NonSeekable(src);

        var result = await ExcelExportJob.ReadAllBytesAsync(stream, CancellationToken.None);

        Assert.Equal(src, result);
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
