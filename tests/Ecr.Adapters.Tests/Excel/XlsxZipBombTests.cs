using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// `S10` (аудит безпеки): книга імпорту перевіряється на zip-bomb ДО розбору
/// ClosedXML — межа розпакованого розміру, коефіцієнта стиснення й кількості
/// записів.
/// </summary>
/// <remarks>
/// ⛔ Доти <c>ExcelImporter.Open</c> віддавав файл просто в
/// <c>new XLWorkbook(file)</c>: межею була лише стеля тіла запиту Kestrel
/// (~30 МБ СТИСНУТОГО), а 30 МБ deflate розгортаються в десятки гігабайтів.
/// Перевірки карти й документа йдуть уже ПІСЛЯ розбору, тож бомбу
/// розгортав будь-хто з правом імпорту в будь-який видимий документ.
/// </remarks>
public sealed class XlsxZipBombTests
{
    private const int MiB = 1024 * 1024;

    private static readonly System.Text.Json.JsonSerializerOptions MapOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Справжня книга, у якій аркуш роздуто пробілами до ~48 МБ (стиснуто —
    /// десятки КБ): коефіцієнт ~1000:1, тобто класична бомба в мініатюрі.
    /// </summary>
    /// <remarks>
    /// ⚠ 48 МБ — навмисно: без запобіжника ClosedXML цей аркуш СПРАВДІ
    /// розбирає (пробіли між елементами — валідний XML), і тест має лишатися
    /// безпечним для машини навіть до фіксу. До фіксу відмова приходила вже
    /// з-за розбору — «немає карти» (<c>noMapSheet</c>), після мегабайтів
    /// виділень; тепер — «не книга» до відкриття, без розгортання.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public async Task Роздутий_аркуш_відхиляється_до_розбору_без_розгортання()
    {
        var bomb = InflatedWorkbook(48L * MiB);
        Assert.True(bomb.Length < MiB, $"бомба мала бути малою стиснутою, а вийшла {bomb.Length} байтів.");

        using var file = new MemoryStream(bomb);
        var importer = Importer();

        // ⚠ Лічильник ПОТОКУ, а не процесу: паралельні тести інших класів
        // інакше додали б свої виділення. `PreviewAsync` до першого `await`
        // (тобто й `Open`) іде синхронно на цьому потоці.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var task = importer.PreviewAsync(1, file, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => task);

        Assert.True(
            allocated < 8L * MiB,
            $"на відмову виділено {allocated / MiB} МБ ({error.Details!["messageKey"]}) — "
            + "книгу розгорнуто, а не перевірено до розбору.");
        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", error.Details!["messageKey"]);
    }

    /// <summary>
    /// Звичайна книга системи з картою проходить запобіжник і доходить до
    /// перевірок змісту (тут — «книга з чужого документа»).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public async Task Звичайна_книга_проходить_запобіжник()
    {
        using var file = new MemoryStream(NormalWorkbook(documentId: 42));

        // ⛔ Відмова саме «інший документ»: до неї доходить лише книга, яку
        // запобіжник пропустив і ClosedXML розібрав разом із картою.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, file, CancellationToken.None));

        Assert.Equal("err.ECR-IMP-0422.workbookOtherDocument", error.Details!["messageKey"]);
    }

    /// <summary>Пошкоджений zip — та сама відмова, а не 500.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public async Task Пошкоджений_zip_відхиляється_тією_самою_відмовою()
    {
        var book = NormalWorkbook(documentId: 1);

        // Обрізаний хвіст — пропадає центральний каталог.
        using var file = new MemoryStream(book[..(book.Length / 2)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, file, CancellationToken.None));

        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", error.Details!["messageKey"]);
    }

    /// <summary>
    /// Класична бомба: один запис на 512 МіБ нулів (стиснуто — ~500 КБ).
    /// Огляд відмовляє за заголовком, нічого не розпаковуючи.
    /// </summary>
    [Fact(Timeout = 120_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public async Task Запис_на_сотні_мегабайтів_нулів_відхиляється_за_заголовком()
    {
        var bomb = await Task.Run(() => ZerosZip(("xl/worksheets/sheet1.xml", 512L * MiB)));
        Assert.True(bomb.Length < 2 * MiB, $"бомба {bomb.Length} байтів — генератор не стиснув нулі.");

        using var file = new MemoryStream(bomb);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var verdict = XlsxSafetyGate.Inspect(file);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(XlsxVerdict.TooLarge, verdict);
        Assert.True(allocated < MiB, $"огляд виділив {allocated} байтів — він розпаковує, а не читає каталог.");
        Assert.Equal(0, file.Position);
    }

    /// <summary>
    /// Запис у межах сумарного розміру, але зі стисненням ~1000:1 — теж
    /// відмова: саме так виглядає бомба, що ховається під стелю.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Запис_із_коефіцієнтом_понад_100_до_1_відхиляється()
    {
        using var file = new MemoryStream(ZerosZip(("xl/worksheets/sheet1.xml", 32L * MiB)));

        Assert.Equal(XlsxVerdict.SuspiciousCompression, XlsxSafetyGate.Inspect(file));
    }

    /// <summary>
    /// Дрібний запис із тим самим коефіцієнтом НЕ відхиляється: поріг
    /// перевірки — 1 МіБ, інакше падали б чесні крихітні частини книги.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Дрібний_сильно_стиснутий_запис_проходить()
    {
        using var file = new MemoryStream(ZerosZip(("xl/styles.xml", 512L * 1024)));

        Assert.Equal(XlsxVerdict.Safe, XlsxSafetyGate.Inspect(file));
    }

    /// <summary>
    /// Сума записів, кожен з яких окремо чесний, понад межу — відмова.
    /// </summary>
    /// <remarks>
    /// ⚠ Межа тут зменшена через <see cref="XlsxLimits"/>: ~256 МіБ чесно
    /// стиснутих даних тест генерував би секундами, а предмет перевірки —
    /// СУМУВАННЯ, а не число.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Сума_записів_понад_межу_відхиляється()
    {
        using var file = new MemoryStream(ZerosZip(("a.xml", 600), ("b.xml", 600), ("c.xml", 600)));
        var limits = XlsxSafetyGate.DefaultLimits with { MaxUncompressedBytes = 1_500 };

        Assert.Equal(XlsxVerdict.TooLarge, XlsxSafetyGate.Inspect(file, limits));

        // Контроль: та сама книга з межею, що вміщає суму, — проходить.
        Assert.Equal(XlsxVerdict.Safe, XlsxSafetyGate.Inspect(file, limits with { MaxUncompressedBytes = 1_800 }));
    }

    /// <summary>Записів понад <see cref="XlsxSafetyGate.MaxEntries"/> — відмова.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Записів_понад_межу_відхиляється()
    {
        var entries = Enumerable.Range(0, XlsxSafetyGate.MaxEntries + 1)
            .Select(i => ($"p/{i}.xml", 1L))
            .ToArray();

        using var file = new MemoryStream(ZerosZip(entries));

        Assert.Equal(XlsxVerdict.TooManyEntries, XlsxSafetyGate.Inspect(file));
    }

    /// <summary>Не zip і обрізаний zip — <see cref="XlsxVerdict.Corrupt"/>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Пошкоджений_пакет_дає_вердикт_Corrupt()
    {
        var book = NormalWorkbook(documentId: 1);

        using var cut = new MemoryStream(book[..(book.Length / 2)]);
        using var text = new MemoryStream(Encoding.UTF8.GetBytes("plain text, not a workbook"));

        Assert.Equal(XlsxVerdict.Corrupt, XlsxSafetyGate.Inspect(cut));
        Assert.Equal(XlsxVerdict.Corrupt, XlsxSafetyGate.Inspect(text));
    }

    /// <summary>
    /// Файл, коротший за кінцевий запис zip-каталогу, у потоці, що на від'ємну
    /// позицію кидає <see cref="ArgumentOutOfRangeException"/> (так робить потік
    /// завантаження ASP.NET), — <see cref="XlsxVerdict.Corrupt"/>, а не виняток.
    /// </summary>
    /// <remarks>
    /// ⛔ До фіксу <c>POST /documents/{id}/import/preview</c> із порожнім, текстовим
    /// чи обрізаним до кількох байтів файлом давав 500 (прохід по відмовах API):
    /// <c>MemoryStream</c> у сусідньому тесті кидає <c>IOException</c>, яку
    /// <c>ZipArchive</c> сам перетворює на <c>InvalidDataException</c>, тож там
    /// дефект не видно.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(13)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "negative-path-sweep")]
    public void Файл_коротший_за_кінець_каталогу_дає_вердикт_Corrupt(int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes("PK\u0003\u0004hello garbage").AsSpan(0, Math.Min(length, 17)).CopyTo(bytes);

        using var file = new StrictPositionStream(bytes);

        Assert.Equal(XlsxVerdict.Corrupt, XlsxSafetyGate.Inspect(file));
        Assert.Equal(0, file.Position);
    }

    /// <summary>Потік, що, як потік завантаження ASP.NET, не терпить позиції поза межами.</summary>
    private sealed class StrictPositionStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, Length);
                base.Position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            Position = loc switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => Length + offset,
            };

            return Position;
        }
    }

    /// <summary>Звичайна книга системи — <see cref="XlsxVerdict.Safe"/>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Звичайна_книга_дає_вердикт_Safe()
    {
        using var file = new MemoryStream(NormalWorkbook(documentId: 1));

        Assert.Equal(XlsxVerdict.Safe, XlsxSafetyGate.Inspect(file));
    }

    /// <summary>
    /// Потік без позиціювання буферизується й доходить до розбору; понад
    /// стелю пакета — відмова без буфера на весь потік.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public async Task Потік_без_позиціювання_буферизується_зі_стелею()
    {
        using var normal = new ForwardOnlyStream(new MemoryStream(NormalWorkbook(documentId: 42)));

        var other = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, normal, CancellationToken.None));
        Assert.Equal("err.ECR-IMP-0422.workbookOtherDocument", other.Details!["messageKey"]);

        // Нескінченний потік: без стелі буфер ріс би, доки є пам'ять.
        using var endless = new ForwardOnlyStream(null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, endless, CancellationToken.None));
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", error.Details!["messageKey"]);
        Assert.InRange(endless.Consumed,XlsxSafetyGate.MaxPackageBytes, XlsxSafetyGate.MaxPackageBytes + MiB);
    }

    /// <summary>
    /// Zip із записами нулів заданого розміру; пишеться шматками, розгорнутий
    /// вміст у пам'яті не тримається.
    /// </summary>
    private static byte[] ZerosZip(params (string Name, long Length)[] entries)
    {
        var chunk = new byte[MiB];

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, length) in entries)
            {
                using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                for (long written = 0; written < length; written += chunk.Length)
                {
                    target.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Потік лише вперед (як тіло запиту без буферизації); <c>null</c> —
    /// нескінченні нулі.
    /// </summary>
    private sealed class ForwardOnlyStream(Stream? inner) : Stream
    {
        public long Consumed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n;
            if (inner is null)
            {
                Array.Clear(buffer, offset, count);
                n = count;
            }
            else
            {
                n = inner.Read(buffer, offset, count);
            }

            Consumed += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Мала справжня книга з картою документа.</summary>
    internal static byte[] NormalWorkbook(long documentId)
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Data").Cell(1, 1).Value = 1;

        var map = new ExcelWorkbookMap(documentId, 202601, 7, []);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value =
            System.Text.Json.JsonSerializer.Serialize(map, MapOptions);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Справжня книга, у якій XML першого аркуша роздуто пробілами перед
    /// закривальним тегом кореня. Пишеться ПОТОКОВО шматками: розгорнутий
    /// вміст у пам'яті тесту не тримається ніколи.
    /// </summary>
    internal static byte[] InflatedWorkbook(long padding)
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Data").Cell(1, 1).Value = 1;

        using var source = new MemoryStream();
        workbook.SaveAs(source);
        source.Position = 0;

        using var output = new MemoryStream();
        using (var original = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var inflated = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                var copy = inflated.CreateEntry(entry.FullName, CompressionLevel.SmallestSize);
                using var target = copy.Open();
                using var from = entry.Open();

                if (!entry.FullName.Equals("xl/worksheets/sheet1.xml", StringComparison.Ordinal))
                {
                    from.CopyTo(target);
                    continue;
                }

                using var reader = new StreamReader(from, Encoding.UTF8);
                var xml = reader.ReadToEnd();
                var close = xml.LastIndexOf("</", StringComparison.Ordinal);

                var head = Encoding.UTF8.GetBytes(xml[..close]);
                target.Write(head);

                var chunk = new byte[MiB];
                Array.Fill(chunk, (byte)' ');
                for (long written = 0; written < padding; written += chunk.Length)
                {
                    target.Write(chunk, 0, (int)Math.Min(chunk.Length, padding - written));
                }

                target.Write(Encoding.UTF8.GetBytes(xml[close..]));
            }
        }

        return output.ToArray();
    }

    private static ExcelImporter Importer()
        => new(
            Substitute.For<IMetadataCache>(),
            Substitute.For<IRegistryStore>(),
            Substitute.For<IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            Substitute.For<IImportPreviewStore>(),
            patch: null!,
            new ImportDiffBuilder(),
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<ISheetEditGate>());
}
