# 08 — Експорт Excel: ім'я файлу й аркуш шапки документа

**Область:** вивантаження документа (`xlsx` / ZIP CSV / JSON), ім'я файлу на всіх шляхах експорту, аркуш шапки документа («Header») і його відповідність формі «2. Contract» шаблону Land, зворотний імпорт книги з аркушем шапки.
**`main`:** `39aea41d9` (worktree `F:/ecr-wt/audit-dec`). **Дата:** 2026-11-11.
**✎ 2026-11-11 (після рішень PO):** X-1, X-2, X-4, X-5 — «так»; X-8 — за правилом імені X-1. Знахідки переписано до впровадження; рядок рішень `D-X2` (X-2 крок 1) описує весь новий вигляд аркуша шапки (порядок, назва, розкладка, службові рядки).
**Охоплено повністю:** `DocumentsController.Export/DownloadExport`, `ExportDocumentHandler`, `DownloadExportHandler`, `ExcelExportJob`, `ExportStore`, `ExcelExporter` (порядок аркушів, імена аркушів, `WriteHeaderSheetAsync`, `WriteMap`, `Coordinates`, `CleanSheetName/UniqueName`), `DocumentHeaderExport`/`HeaderExportLabels`, клієнт `ExportButton.tsx`, `JobFacts.JobResultLink`, `features/audit/fileName.ts`; імена файлів `ReportsController.ExportSnapshot`, `RegistryRowsController.Export`, `AuditController.StreamCsvAsync`, `PiRawDataController`, `UiStringsController`.
**Поверхово:** `DocumentDataExporter` (лише шапка CSV/JSON та імена записів ZIP), `ExcelImporter` (лише пошук аркушів), `SnapshotWorkbookWriter`/`RegistryWorkbookWriter` (лише імена).
**Не охоплено:** вміст аркушів даних (формули, стилі, умовне форматування — це звіт 04), продуктивність побудови (звіт 03). Оригінал Excel-шаблону Land (`RDS_xxx_Template_Land_2026_v1.0.4.0*.xlsm`) у репозиторії **відсутній** (`docs/delivery/reference-data/land-template/README.md:9-10` посилається на `docs/source/...`, теки `docs/source` в репо немає) — розкладку «2. Contract» звірено лише з тим, що про неї записано в репо (маніфест полів, підписи, опис секції Contract у картці); решту — чеклістом для стенда (X-5).

---

## Відповіді на питання PO

**1. «Чи під час експорту документу назва файла нормальна?» — Ні.**
Сервер називає файл `document-{id документа}-{exportId}.xlsx`, де `exportId` — 32 шістнадцяткові символи GUID: наприклад `document-42-3f2a9c0b7d1e4f6a8b9c0d1e2f3a4b5c.xlsx` (`src/Ecr.Api/Controllers/DocumentsController.cs:734`, `exportId` = `Guid.NewGuid().ToString("N")` у `src/Ecr.Application/Documents/ExcelExchangeHandlers.cs:109`). У назві немає ні назви документа, ні його номера (File Number), ні періоду — два експорти різних місяців неможливо розрізнити в теці «Завантаження». Так само `.zip` (CSV) і `.json`. Браузер зберігає саме цю назву: посилання — звичайне `<a href … download>` без власного імені (`src/Ecr.Web/src/features/export/ExportButton.tsx:191-195`, `src/Ecr.Web/src/features/jobs/JobFacts.tsx:229`). Цікаво, що код задумував інше: коментар `src/Ecr.Infrastructure/Persistence/DocumentStore.cs:1057-1059` прямо каже «BusinessKey потрапляє … в назви експортів». Пропозиція — **X-1** (ім'я на кшталт `Звіт Тенгіз (LAND-0007) 2026-11.xlsx`, з безпечною обробкою кирилиці/казахської, лапок, слешів і довжини). **✎ Рішення PO 11.11: «так»** — «<Назва документа> (<File Number>) <Період>», період `2026-11` / `Q2 2026` / `2026`; те саме правило — для зрізу звіту (X-8). Зріз звіту теж називається безлико — `snapshot-7.xlsx` (**X-8**). Довідники (`registry-<код>-<дата>`) і журнали аудиту (`audit-…-<UTC>Z`) — прийнятні.

**2. «Чи загальна вкладка Document header іде першою та має вигляд як в Excel-шаблоні Land?» — Ні, і ні.**
- **Не перша.** Аркуш шапки додається **після всіх аркушів даних** і перед прихованим службовим `_ecr` (`src/Ecr.Adapters.Excel/ExcelExporter.cs:278-284`); книга відкривається на першому аркуші даних. Це було **свідоме рішення D-290** від 06.10 (`docs/tz/10-decisions.md:743`: «видимий аркуш «Header» після аркушів даних»). **✎ Рішення PO 11.11: «так»** — шапка перша й активна, D-290 у частині порядку аркушів переглядається новим рядком рішень — **X-2**. Технічно перенос безпечний: імпорт шукає аркуші за іменем, а не за позицією.
- **Вигляд не як у Land.** У Land вкладка «2. Contract» — форма «підпис | значення» з підписами «EN — RU» і 13 позиціями, серед яких File Number і Version (так її відтворює картка документа: `docs/delivery/QUICKSTART-FIRST-TEST.md:249-252`, `docs/delivery/reference-data/land-contract/header-fields.json`). Експорт пише таблицю «Field | Code | Value» з технічним кодом поля, підписами однією мовою, **кодами довідників замість назв** (`AREA_001`, `REG_001`, `ONOFF_001` замість «1 BCMA», «Atyrau», «Offshore» — **X-3**), **без File Number і Version** і без періоду (**X-4**), назва вкладки «Header», а не «Document header»/«Contract» (**X-5**). Повної звірки з оригінальним `.xlsm` зробити неможливо — його немає в репо; у X-5 — чекліст для стенда. **✎ Рішення PO 11.11: X-4 і X-5 — «так»** (вкладка «Document header», «підпис EN — RU | значення», File Number / Version / період).

---

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| X-1 | Висока | ui / correctness | Ім'я файлу експорту документа — `document-{id}-{GUID}`, без назви й періоду | M | середній | ні (✎ ухвалено PO 11.11: «Назва (File Number) Період») |
| X-2 | Висока | ui | Аркуш шапки — останній видимий, не перший і не активний (D-290) | S | низький | ні (✎ ухвалено PO 11.11: «так», перегляд D-290) |
| X-3 | Висока | ui / i18n | Поля-списки шапки в xlsx — код запису (`AREA_001`), а не назва | S | низький | ні |
| X-4 | Середня | ui | В аркуші шапки немає File Number, Version і періоду вивантаження | M | середній | ні (✎ ухвалено PO 11.11: так) |
| X-5 | Середня | ui / i18n | Розкладка аркуша шапки не як форма «2. Contract» Land | M | середній | ні (✎ ухвалено PO 11.11: «Document header», «EN — RU \| значення») |
| X-6 | Низька | correctness | Текст шапки пишеться в обхід `WriteText` — губиться початковий апостроф | S | низький | ні |
| X-7 | Середня | correctness | Апостроф у назві аркуша ламає посилання формул; назва з `'` на краю / «History» | S | низький | ні |
| X-8 | Низька | ui | Зріз звіту вивантажується як `snapshot-{id}.xlsx` | S | низький | ні (✎ ім'я за правилом X-1, PO 11.11) |

---

## Знахідки

### X-1 — Висока — Ім'я файлу експорту документа — `document-{id}-{GUID}`, без назви й періоду
**✎ рішення PO 11.11:** «так» — ім'я файлу «<Назва документа> (<File Number>) <Період>.xlsx», період читабельно: місяць `2026-11`, квартал `Q2 2026`, рік `2026` (інша періодичність — `2026-P05`). Та сама функція дає ім'я для `.zip` (CSV) і `.json` того самого експорту (розширення визначає вміст) і для вивантаження зрізу звіту (X-8). Заголовок — `filename*=UTF-8''…` (RFC 5987) плюс ASCII-запасне `filename`. Копія аркуша в імені не потрібна: експорт документа вивантажує документ за період цілком, з усіма копіями аркушів (`ExcelExportOptions` не має фільтра копії — `src/Ecr.Application/Ports/IExcelExporter.cs:44-46`).
**Категорія:** ui / correctness
**Де:**
- `src/Ecr.Api/Controllers/DocumentsController.cs:729-735` — `DownloadExport`, рядок `:734` будує ім'я.
- `src/Ecr.Application/Documents/ExcelExchangeHandlers.cs:109` — `exportId = Guid.NewGuid().ToString("N")`; `:117-121` — постановка задачі; `:160-162` — `ExcelExportTask`.
- `src/Ecr.Application/Documents/DownloadExportHandler.cs:62`, `:119` — повертає лише байти.
- `src/Ecr.Application/Ports/IExportStore.cs:36-37`, `:49` — сховище не знає імені.
- `src/Ecr.Infrastructure/Caching/ExportStore.cs:26`, `:38`, `:41-57`, `:60-75` — конверт `[documentId][owner][вміст]`.
- `src/Ecr.Infrastructure/Jobs/ExcelExportJob.cs:83-85` — збереження книги.
- Клієнт не перекриває ім'я: `src/Ecr.Web/src/features/export/ExportButton.tsx:191-195` (`<Anchor href=… download>` без значення), `src/Ecr.Web/src/features/jobs/JobFacts.tsx:229` — тож браузер бере ім'я з `Content-Disposition`.
- Намір, що не реалізований: `src/Ecr.Infrastructure/Persistence/DocumentStore.cs:1057-1059` («BusinessKey потрапляє в аудит і в назви експортів»).

**Проблема:**
```csharp
// DocumentsController.cs:731-734
var content = await downloadExport.HandleAsync(exportId, ct).ConfigureAwait(false);
var (contentType, extension) = Ecr.Application.Documents.DocumentExportFormat.OfContent(content);
return File(content, contentType, $"document-{id}-{exportId}.{extension}");
```
`exportId` — внутрішній 128-бітний ключ (32 hex). Назви документа (`Document.NameL10n`), File Number (`Document.BusinessKey`) і періоду в імені немає. Додатково: `{id}` береться з маршруту, а не з книги (`book.DocumentId`), тож `GET /documents/5/export/<exportId документа 7>` віддасть книгу документа 7 під іменем `document-5-…` (доступ при цьому перевіряється правильно — за `book.DocumentId`, `DownloadExportHandler.cs:99`).

**Сценарій відтворення:** документ «Звіт Тенгіз», File Number `LAND-0007`, період 2026-11 → «Ще» → «Export to Excel» → тост «Download the workbook» → збережений файл `document-42-3f2a9c0b7d1e4f6a8b9c0d1e2f3a4b5c.xlsx`. Очікувано: людське ім'я з назвою/номером документа і періодом (питання PO; намір у `DocumentStore.cs:1057-1059`).

**Вплив:** усі користувачі з `Document.Export`, кожен експорт (xlsx, zip, json). Файли різних документів/місяців не розрізнити без відкриття; при пересиланні поштою отримувач бачить GUID.

**Зміна — покроково:**

1. **Новий файл** `src/Ecr.Application/Common/ExportFileName.cs`:
```csharp
using System.Globalization;
using System.Text;

namespace Ecr.Application.Common;

/// <summary>Людські й безпечні для Windows/macOS/Linux імена файлів експорту (аудит 2026-11, X-1).</summary>
/// <remarks>
/// ⚠ Кирилиця й казахські літери лишаються як є: `File(...)` ASP.NET Core сам пише
/// `filename*=UTF-8''…` (RFC 5987) і ASCII-запасне `filename`. Не використовувати тут
/// `System.Net.Http.Headers.ContentDispositionHeaderValue.FileName` — для не-ASCII він дає RFC 2047 (`=?utf-8?B?…?=`).
/// </remarks>
public static class ExportFileName
{
    /// <summary>Найбільша довжина основи імені (без розширення), символів.</summary>
    /// <remarks>Excel не відкриває файл із повним шляхом понад 218 символів; 100 лишає запас на теку.</remarks>
    public const int MaxBaseLength = 100;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Безпечна основа імені; порожнє після очищення — <paramref name="fallback"/>.</summary>
    public static string Sanitize(string? raw, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var text = Clean(raw);
        return Truncate(text.Length == 0 ? Clean(fallback) : text, MaxBaseLength);
    }

    /// <summary>Період людською формою (рішення PO 11.11): <c>2026-11</c>, <c>Q2 2026</c>, <c>2026</c>, інакше <c>2026-P05</c>.</summary>
    /// <param name="periodKey"><c>Рік*100 + Номер</c>.</param>
    /// <param name="periodKind"><c>PeriodKind.ToString()</c> проєкту: Monthly / Quarterly / Yearly / Custom; <c>null</c> — невідомо.</param>
    public static string Period(int periodKey, string? periodKind)
    {
        var year = periodKey / 100;
        var sequence = periodKey % 100;
        return periodKind switch
        {
            "Monthly" => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{sequence:D2}"),
            "Quarterly" => string.Create(CultureInfo.InvariantCulture, $"Q{sequence} {year:D4}"),
            "Yearly" => year.ToString("D4", CultureInfo.InvariantCulture),
            _ => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-P{sequence:D2}"),
        };
    }

    /// <summary>
    /// Основа імені експорту: «{назва} ({код}) {період}{суфікс}»; без назви (або назва = код) — «{код} {період}{суфікс}»;
    /// без коду — «{назва} {період}{суфікс}»; без обох — <paramref name="fallback"/>. ОДНА функція для документа (X-1) і
    /// зрізу звіту (X-8), рішення PO 11.11.
    /// </summary>
    /// <remarks>Різеться назва, а не код, період чи суфікс: саме вони розрізняють файли.</remarks>
    public static string ForExport(
        string? code, string? name, int? periodKey, string? periodKind, string? suffix = null, string fallback = "document")
    {
        var tail = (periodKey is { } key ? " " + Period(key, periodKind) : string.Empty) + (suffix ?? string.Empty);
        var cleanCode = Clean(code);
        var cleanName = Clean(name);

        string head;
        if (cleanName.Length == 0 || string.Equals(cleanName, cleanCode, StringComparison.OrdinalIgnoreCase))
        {
            head = cleanCode.Length == 0 ? fallback : cleanCode;
        }
        else if (cleanCode.Length == 0)
        {
            head = cleanName;
        }
        else
        {
            var room = MaxBaseLength - tail.Length - cleanCode.Length - 3; // " (" + ")"
            head = room >= 10 ? $"{Truncate(cleanName, room)} ({cleanCode})" : cleanCode;
        }

        return Truncate(head, MaxBaseLength - tail.Length) + tail;
    }

    /// <summary>X-1: «{назва документа} ({File Number}) {період}».</summary>
    public static string ForDocument(string? businessKey, string? documentName, int periodKey, string? periodKind)
        => ForExport(businessKey, documentName, periodKey, periodKind);

    /// <summary>X-8: «{назва звіту} ({код звіту}) {період} #{id}»; id — бо зрізів одного звіту й періоду буває кілька.</summary>
    public static string ForSnapshot(string? reportCode, string? reportName, int? periodKey, string? periodKind, long snapshotId)
        => ForExport(
            reportCode, reportName, periodKey, periodKind,
            suffix: string.Create(CultureInfo.InvariantCulture, $" #{snapshotId}"), fallback: "snapshot");

    /// <summary>
    /// ASCII-запасне ім'я для параметра <c>filename</c> (клієнти без RFC 5987): лишаються друковані ASCII-символи; якщо
    /// від назви нічого не лишилось — дужки навколо коду знімаються («Звіт (LAND-0007) 2026-11» → «LAND-0007 2026-11»);
    /// менше трьох літер/цифр — <paramref name="asciiDefault"/>.
    /// </summary>
    public static string AsciiFallback(string baseName, string asciiDefault)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(asciiDefault);

        var builder = new StringBuilder(baseName.Length);
        foreach (var symbol in baseName)
        {
            if (symbol is >= ' ' and <= '~')
            {
                builder.Append(symbol);
            }
        }

        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (text.StartsWith('(') && text.IndexOf(')', StringComparison.Ordinal) is var close and > 0)
        {
            text = (text[1..close] + text[(close + 1)..]).Trim();
        }

        return text.Count(char.IsLetterOrDigit) >= 3 ? text : asciiDefault;
    }

    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var symbol in raw)
        {
            builder.Append(symbol switch
            {
                '"' => '\'',
                '/' or '\\' or '|' => '-',
                ':' or '*' or '?' or '<' or '>' => '_',
                _ when char.IsControl(symbol) => ' ',
                _ => symbol,
            });
        }

        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var trimmed = collapsed.Trim('.', ' ');
        return trimmed.Length > 0 && Reserved.Contains(trimmed.Split('.')[0]) ? "_" + trimmed : trimmed;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // ⚠ За текстовими елементами: різ посеред сурогатної пари дав би зіпсований UTF-8 у заголовку.
        var info = new StringInfo(text);
        var builder = new StringBuilder(max);
        for (var i = 0; i < info.LengthInTextElements; i++)
        {
            var element = info.SubstringByTextElements(i, 1);
            if (builder.Length + element.Length > max)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString().TrimEnd('.', ' ');
    }
}
```

2. Файл `src/Ecr.Application/Documents/ExcelExchangeHandlers.cs` — залежність, обчислення імені, поле завдання.
```diff
@@ :26-35
 public sealed class ExportDocumentHandler(
     IBackgroundJobScheduler jobs,
     IAccessDecisionService access,
     ICurrentUser currentUser,
     IAuditWriter audit,
     Ecr.Domain.Abstractions.IClock clock,
     // ✎ Q-17: колонки з іншим власником, період якого поза областю ролі замовника, не вивантажуються (як приховані).
     // Необов'язкові лише заради тестів, що конструюють обробник вручну.
     IMetadataCache? metadata = null,
-    IMonthColumnRouter? monthRouter = null)
+    IMonthColumnRouter? monthRouter = null,
+    // X-1 (аудит 2026-11): назва, File Number і періодичність — для людського імені файлу. `null` - ім'я за id.
+    IDocumentStore? documents = null)
 {
@@ :109
         var exportId = Guid.NewGuid().ToString("N");
+
+        // X-1: ім'я файлу — ТУТ, бо тут відомі документ, період і мова замовника; задача і завантаження їх не мають.
+        string? fileBaseName = null;
+        if (documents is not null
+            && await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false) is { } summary)
+        {
+            fileBaseName = ExportFileName.ForDocument(
+                summary.BusinessKey, summary.NameL10n?.Get(options.Language), options.PeriodKey, summary.PeriodKind);
+        }
@@ :119-121
         var jobId = await jobs
             .EnqueueAsync<IExcelExportJob>(
-                new ExcelExportTask(documentId, options, exportId, normalized, requestedBy), ct, currentUser.UserId)
+                new ExcelExportTask(documentId, options, exportId, normalized, requestedBy, fileBaseName), ct, currentUser.UserId)
             .ConfigureAwait(false);
@@ :147-162 (документація + запис)
+/// <param name="FileBaseName">X-1: основа імені файлу без розширення (<see cref="ExportFileName.ForDocument"/>); <c>null</c> — завдання до X-1.</param>
 public sealed record ExcelExportTask(
     long DocumentId, ExcelExportOptions Options, string ExportId, string? Format = DocumentExportFormat.Xlsx,
-    int? RequestedByUserId = null);
+    int? RequestedByUserId = null, string? FileBaseName = null);
```
(`using Ecr.Application.Common;` і `using Ecr.Application.Ports;` уже є, `ExcelExchangeHandlers.cs:2`, `:5`; `PeriodKeyFilter` — `Ecr.Application.Ports`, `IDocumentStore.cs:635`.) `ExportDocumentHandler` у DI — `AddScoped` (`src/Ecr.Application/DependencyInjection.cs:494`), `IDocumentStore` розв'язується контейнером.

3. Файл `src/Ecr.Application/Ports/IExportStore.cs`:
```diff
     public Task SaveAsync(
-        string exportId, long documentId, int ownerUserId, byte[] content, TimeSpan lifetime, CancellationToken ct);
+        string exportId, long documentId, int ownerUserId, byte[] content, TimeSpan lifetime, CancellationToken ct,
+        string? fileBaseName = null);
@@
-/// <param name="Content">Вміст книги.</param>
-public sealed record ExportedBook(long DocumentId, int OwnerUserId, byte[] Content);
+/// <param name="Content">Вміст книги.</param>
+/// <param name="FileBaseName">X-1: основа імені файлу; <c>null</c> — завдання до X-1.</param>
+public sealed record ExportedBook(long DocumentId, int OwnerUserId, byte[] Content, string? FileBaseName = null);
```
(і `/// <param name="fileBaseName">…</param>` до `SaveAsync`).

4. Файл `src/Ecr.Infrastructure/Caching/ExportStore.cs` — конверт v3 із довжиною імені:
```diff
-    public const string KeyPrefix = "ecr:export:v2:";
+    // X-1: v3 — у заголовку ще й ім'я файлу (int довжини UTF-8 + байти). Записи v2 живуть годину й зникають самі.
+    public const string KeyPrefix = "ecr:export:v3:";
@@
-    private const int HeaderLength = sizeof(long) + sizeof(int);
+    private const int HeaderLength = sizeof(long) + sizeof(int) + sizeof(int);
@@ SaveAsync
-    public Task SaveAsync(
-        string exportId, long documentId, int ownerUserId, byte[] content, TimeSpan lifetime, CancellationToken ct)
+    public Task SaveAsync(
+        string exportId, long documentId, int ownerUserId, byte[] content, TimeSpan lifetime, CancellationToken ct,
+        string? fileBaseName = null)
     {
         ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
         ArgumentNullException.ThrowIfNull(content);
 
-        var envelope = new byte[HeaderLength + content.Length];
+        var name = fileBaseName is null ? [] : System.Text.Encoding.UTF8.GetBytes(fileBaseName);
+        var envelope = new byte[HeaderLength + name.Length + content.Length];
         BitConverter.TryWriteBytes(envelope, documentId);
         BitConverter.TryWriteBytes(envelope.AsSpan(sizeof(long)), ownerUserId);
-        content.CopyTo(envelope, HeaderLength);
+        BitConverter.TryWriteBytes(envelope.AsSpan(sizeof(long) + sizeof(int)), fileBaseName is null ? -1 : name.Length);
+        name.CopyTo(envelope, HeaderLength);
+        content.CopyTo(envelope, HeaderLength + name.Length);
@@ FindAsync
         var documentId = BitConverter.ToInt64(envelope, 0);
         var ownerUserId = BitConverter.ToInt32(envelope, sizeof(long));
-        var content = envelope[HeaderLength..];
-
-        return new ExportedBook(documentId, ownerUserId, content);
+        var nameLength = BitConverter.ToInt32(envelope, sizeof(long) + sizeof(int));
+        var nameBytes = Math.Max(nameLength, 0);
+        if (envelope.Length < HeaderLength + nameBytes)
+        {
+            return null;
+        }
+
+        var fileBaseName = nameLength < 0 ? null : System.Text.Encoding.UTF8.GetString(envelope, HeaderLength, nameBytes);
+        var content = envelope[(HeaderLength + nameBytes)..];
+
+        return new ExportedBook(documentId, ownerUserId, content, fileBaseName);
```
Оновити коментар `:28-37` (заголовок тепер містить ще й ім'я).

5. Файл `src/Ecr.Infrastructure/Jobs/ExcelExportJob.cs:83-85`:
```diff
         await exports
-            .SaveAsync(task.ExportId, task.DocumentId, ownerUserId, content, Lifetime, ct)
+            .SaveAsync(task.ExportId, task.DocumentId, ownerUserId, content, Lifetime, ct, task.FileBaseName)
             .ConfigureAwait(false);
```

6. Файл `src/Ecr.Application/Documents/DownloadExportHandler.cs`:
```diff
-    /// <summary>Читає готову книгу.</summary>
+    /// <summary>Читає готову книгу разом із документом і основою імені файлу (X-1).</summary>
@@ :62
-    public async Task<byte[]> HandleAsync(string exportId, CancellationToken ct)
+    public async Task<ExportedBook> HandleAsync(string exportId, CancellationToken ct)
@@ :119
-        return book.Content;
+        return book;
```

7. Файл `src/Ecr.Api/Controllers/DocumentsController.cs:729-735`:
```diff
     public async Task<IActionResult> DownloadExport(long id, string exportId, CancellationToken ct)
     {
-        var content = await downloadExport.HandleAsync(exportId, ct).ConfigureAwait(false);
-        var (contentType, extension) = Ecr.Application.Documents.DocumentExportFormat.OfContent(content);
-
-        return File(content, contentType, $"document-{id}-{exportId}.{extension}");
+        var book = await downloadExport.HandleAsync(exportId, ct).ConfigureAwait(false);
+        var (contentType, extension) = Ecr.Application.Documents.DocumentExportFormat.OfContent(book.Content);
+
+        // ⛔ X-1: людське ім'я з книги; id — із книги, а не з маршруту (маршрут може назвати інший документ).
+        // Заголовок — ЯВНО: `filename*=UTF-8''…` (RFC 5987, повне ім'я) + ASCII `filename` (запасне для старих клієнтів).
+        // `File(content, type, name)` дав би запасне з `_` замість кожної кириличної літери; System.Net.Http.Headers —
+        // RFC 2047 для не-ASCII. Тому Microsoft.Net.Http.Headers і `File` без імені (тоді він заголовка не пише).
+        var fallbackName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"document-{book.DocumentId}");
+        var baseName = book.FileBaseName ?? fallbackName;
+        Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
+        {
+            FileName = $"{ExportFileName.AsciiFallback(baseName, fallbackName)}.{extension}",
+            FileNameStar = $"{baseName}.{extension}",
+        }.ToString();
+        return File(book.Content, contentType);
     }
```
`id` лишається параметром маршруту (контракт не змінюється). Якщо аналізатор скаржиться на невикористаний параметр — `_ = id;` з коментарем. `using Ecr.Application.Common;` у контролері вже є (`DocumentsController.cs:2`); `Microsoft.Net.Http.Headers` в `Ecr.Api` уже вживається (`CollectionSchedulesController.cs:4`). Ім'я для `.zip`/`.json` — те саме (розширення з `DocumentExportFormat.OfContent`, `:732`).

8. Контракт `docs/build/02-contracts.md:4675` — дописати в рядок: «(✎ X-1, рішення PO 11.11: ім'я файлу — `{назва документа} ({File Number}) {період}.{xlsx|zip|json}`, період `2026-11` / `Q2 2026` / `2026`; `Content-Disposition: attachment; filename="<ASCII>"; filename*=UTF-8''<повне>`)». `openapi.snapshot.json` / `schema.d.ts` не змінюються (тіло — файл).

**Тест-доказ:**
- `tests/Ecr.Application.Tests/Common/ExportFileNameTests.cs` (новий), клас `ExportFileNameTests`, `[Theory] ForDocument_дає_людське_безпечне_ім_я` з `[InlineData]`:
  - `("Звіт Тенгіз", "LAND-0007", 202611, "Monthly")` → `"Звіт Тенгіз (LAND-0007) 2026-11"`;
  - `(null, "P3-V12-0007", 202602, "Quarterly")` → `"P3-V12-0007 Q2 2026"`;
  - `("Цех №1 \"Північ\"/Південь", "K1", 202601, "Yearly")` → `"Цех №1 'Північ'-Південь (K1) 2026"`;
  - `("a:b*c?d<e>f|g\\h", "K", 202611, "Monthly")` → `"a_b_c_d_e_f-g-h (K) 2026-11"`;
  - `("Қазақстан Ұлттық", "KZ-1", 202611, "Monthly")` → `"Қазақстан Ұлттық (KZ-1) 2026-11"`;
  - `(" .Назва. ", "K", 202611, "Monthly")` → `"Назва (K) 2026-11"`;
  - `("Line1\r\nLine2\tX", "K", 202611, "Monthly")` → `"Line1 Line2 X (K) 2026-11"`;
  - `("K", "K", 202611, "Monthly")` → `"K 2026-11"`;
  - `("CON", "K", 202611, "Monthly")` → `"_CON (K) 2026-11"`;
  - `(null, "", 202605, "Custom")` → `"document 2026-P05"`.
  - `[Fact] Довга_назва_ріжеться_а_ключ_і_період_лишаються`: `ForDocument(new string('Д', 300), "LAND-0007", 202611, "Monthly")` → довжина ≤ 100, `EndsWith(" (LAND-0007) 2026-11")`.
  - `[Fact] Різ_не_розриває_сурогатну_пару`: `ForDocument(string.Concat(Enumerable.Repeat("😀", 60)), "K", 202611, "Monthly")` → `!char.IsHighSurrogate(result[result.IndexOf(" (K)") - 1])`, і `Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result)) == result`.
  - `[Theory] Sanitize`: `("CON", "x")` → `"_CON"`; `("lpt1.txt", "x")` → `"_lpt1.txt"`; `("   ", "snapshot-7")` → `"snapshot-7"`; `("a<>b", "x")` → `"a__b"`.
  - `[Theory] Period`: `(202611,"Monthly")`→`"2026-11"`, `(202602,"Quarterly")`→`"Q2 2026"`, `(202601,"Yearly")`→`"2026"`, `(202605,null)`→`"2026-P05"`.
  - `[Theory] AsciiFallback`: `("Звіт Тенгіз (LAND-0007) 2026-11", "document-42")` → `"LAND-0007 2026-11"`; `("Report (K1) Q2 2026", "document-42")` → `"Report (K1) Q2 2026"`; `("Қазақ", "document-42")` → `"document-42"`.
  - `[Theory] ForSnapshot`: `("IEC", "Water report", 202602, "Quarterly", 7)` → `"Water report (IEC) Q2 2026 #7"`; `(null, null, null, null, 7)` → `"snapshot #7"`; `("IEC", null, null, null, 7)` → `"IEC #7"`.
  Падають без фіксу: класу немає (не компілюється); прибрати заміну `'"' => '\''` — червоний кейс із лапками; прибрати `Reserved` — червоний `CON`.
- `tests/Ecr.Application.Tests/Documents/ExportAccessTests.cs` — новий `[Fact] Завдання_несе_людське_ім_я_файлу`: як `З_грантом_експорт_ставиться_в_чергу` (`:72-95`), але `Handler()` з `documents:` — `Substitute.For<IDocumentStore>()`, `FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), …)` повертає `new DocumentSummary(DocumentId, 1, "LAND-0007", DateTime.UtcNow, 0, new Dictionary<string,string>(), new LocalizedText(new Dictionary<string,string>{["en"]="Tengiz"}), PeriodKind: "Monthly")`; перевірити `Received(1).EnqueueAsync<IExcelExportJob>(Arg.Is<object?>(p => p is ExcelExportTask { FileBaseName: "Tengiz (LAND-0007) 2026-01" }), …)`. Без кроку 2 — `FileBaseName` = `null`, тест червоний.
- `tests/Ecr.Application.Tests/Documents/DownloadExportAccessTests.cs:81` — `Assert.Equal(content, result);` → `Assert.Equal(content, result.Content);`.
- `tests/Ecr.Infrastructure.Tests/Caching/ExportStoreTests.cs` — новий `[Fact]` (інтеграційний, як сусідні) `Ім_я_файлу_переживає_кеш_разом_із_кирилицею`: `SaveAsync(…, fileBaseName: "Звіт \"Тенгіз\" (K) 2026-11")` → `FindAsync` повертає те саме `FileBaseName` і той самий `Content`; другий виклик без імені → `FileBaseName == null`. Без кроку 4 — ім'я губиться (`null`), тест червоний.
- `tests/Ecr.Api.Tests/Security/DenyReadTests.ExportOwner.cs` — новий `[Fact]` у тому ж partial-класі: `OrderExportAsync(app, reader, s)` → `reader.GetAsync(Url(exportId))` → `response.Content.Headers.ContentDisposition!.FileNameStar` не містить `exportId`, закінчується на `.xlsx` і відповідає `^.+ (\d{4}-\d{2}|Q\d \d{4}|\d{4}|\d{4}-P\d{2})\.xlsx$`; `FileName` (ASCII) складається лише із символів `0x20-0x7E`. До фіксу — ім'я `document-{id}-{exportId}.xlsx`, червоний. Другий кейс — документ із кириличною/казахською назвою: `FileNameStar` містить назву дослівно, `FileName` — без кирилиці (за `AsciiFallback`).

**Перевірка після впровадження:**
`dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~ExportFileNameTests|FullyQualifiedName~ExportAccessTests|FullyQualifiedName~DownloadExportAccessTests"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter FullyQualifiedName~ExportStoreTests` (тест-БД за м'ютексом); `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DenyReadTests"`; `dotnet test tests/Ecr.Architecture.Tests`. Стенд (чекліст): (1) експорт xlsx, CSV (`.zip`), JSON документа з кириличною й казахською назвою → у Chrome, Edge і Firefox збережене ім'я `Назва (Ключ) 2026-11.xlsx/.zip/.json`; (2) квартальний проєкт → `… Q2 2026.xlsx`, річний → `… 2026.xlsx`; (3) документ без назви → `P3-V12-0007 2026-11.xlsx`; (4) назва з `"`, `/`, `:` і назва `CON` → Windows зберігає файл без помилки; (5) назва 300 символів → основа ≤ 100 символів, File Number і період на місці; (6) зріз звіту (X-8) → `<Назва звіту> (<код>) Q2 2026 #N.xlsx`.

**Ризики і побічні ефекти:** +1 читання `IDocumentStore.FindAsync` на ЗАМОВЛЕННЯ експорту (не на завантаження); стелі фіксованої кількості запитів для `POST /documents/{id}/export` — перевірити (`git grep -n "export" tests/Ecr.Api.Tests | grep -i "QueryCount\|DbCommandCounter"`), не піднімати — якщо стеля є і падає, замінити `FindAsync` легким запитом (`BusinessKey`, `NameL10n`, `PeriodKind`) у `IDocumentStore`. Зміна префікса кешу на v3: книги, побудовані за годину до розгортання, не завантажаться («побудуйте заново») — прийнятно, як при переході на v2 (`ExportStore.cs:21-24`). Завдання з черги, поставлені до розгортання, не мають `FileBaseName` → ім'я `document-{id}.xlsx`. Клієнт не змінюється (бюджет маршруту 250 KB не зачіпається).
**Обґрунтування:** ім'я рахується при замовленні, бо лише там є мова й період (у сховищі їх немає); зберігається в тому самому конверті кешу, тож не потрібні міграція чи таблиця. Альтернатива — рахувати при завантаженні — вимагала б зберігати період і мову окремо й читати документ на кожне завантаження. `exportId` в імені не лишаємо: він нічого не каже людині, а захисту не додає (доступ перевіряється за власником і грантом).
**Рішення PO:** ухвалено 11.11 — формат (а) «{Назва документа} ({File Number}) {період}», період `2026-11` / `Q2 2026` / `2026`; та сама функція для `.zip`/`.json` і для зрізу звіту (X-8). Відкритих питань немає.
**Впевненість:** підтверджено читанням (поведінку браузера щодо `download` без значення і `filename*` — перевірити на стенді).
**Залежності:** X-8 використовує `ExportFileName.ForSnapshot`/`AsciiFallback` (після X-1); X-4 — той самий `FindAsync` і `ExportFileName.Period` (після X-1). Сід, міграції, OpenAPI не змінюються.

---

### X-2 — Висока — Аркуш шапки — останній видимий, не перший і не активний (D-290)
**✎ рішення PO 11.11:** «так» — аркуш шапки документа йде ПЕРШИМ, і книга відкривається на ньому (активна вкладка). Це переглядає xlsx-частину D-290 від 06.10; решта D-290 (CSV `000-header.csv`, JSON `header[]`, імпорт ігнорує шапку, видалені поля не експортуються, права) чинна. Того ж дня PO ухвалив X-4 і X-5 (назва «Document header», розкладка «2. Contract», службові рядки), тож новий рядок рішень `D-X2` (крок 1) описує й нову назву, й розкладку; сам код X-2 змінює лише порядок і активність вкладки, назву й розкладку робить X-5 (після X-3), рядки — X-4.

**Категорія:** ui
**Де:**
- `src/Ecr.Adapters.Excel/ExcelExporter.cs:278-284` — коментар і виклик `WriteHeaderSheetAsync` після аркушів даних;
- `:337` — `workbook.Worksheets.Add(...)` ставить аркуш шапки в кінець; `Position`, `SetTabActive`, `TabActive`, `TabSelected` у файлі не викликаються ніде (`git grep -n "SetTabActive\|TabActive\|TabSelected\|\.Position = " -- src/Ecr.Adapters.Excel` — порожньо, крім `output.Position = 0` потоку на `:310`);
- `:1352` — `WriteMap` додає прихований `_ecr` останнім (після шапки), `:1362` — `sheet.Hide()`;
- рішення: `docs/tz/10-decisions.md:743` (рядок D-290: «xlsx — видимий аркуш «Header» після аркушів даних і перед прихованим `_ecr`»), журнал змін `:1055`;
- тести, що закріплюють старий порядок: `tests/Ecr.Adapters.Tests/Excel/ExcelExporterHeaderSheetTests.cs:20-21` (summary), `:114` (назва тесту), `:118-119`, `:189`;
- документація тестувальника: `docs/build/TESTER-GUIDE.md:1108` (З-2а: «після аркушів даних (…; перший видимий аркуш — дані)»).

**Проблема:**
```csharp
// ExcelExporter.cs:278-284
// A2-12: поля шапки — окремим ВИДИМИМ аркушем ПІСЛЯ аркушів даних і ПЕРЕД
// прихованою мапою. …
await WriteHeaderSheetAsync(workbook, snapshot, documentId, options, usedNames, ct).ConfigureAwait(false);
```
Аркуш шапки додається в кінець (`:337`), активна вкладка не задається, тож Excel відкриває книгу на першому аркуші даних. У Land «2. Contract» — перша вкладка з тих, що заповнює людина (`land-template.model.json` → `excelSheets`: `Configuration`(1, довідники), `1. Sheet Selection`(2), **`2. Contract`(3)**, далі звітні `3. …`).

**Сценарій відтворення:** документ Land із полями шапки → експорт xlsx → відкрити в Excel. Фактично: активна вкладка — перший аркуш даних (напр. «Stationary Equipment»), «Header» — остання перед прихованою `_ecr`. Очікувано (рішення PO 11.11): «Header» — перша вкладка й відкрита при відкритті книги.

**Вплив:** кожен експорт документа з полями шапки; людина, що звіряє з Land, шукає Contract наприкінці книги з ~20 аркушів.

**Імпорт не зачеплено (доведено кодом):** імпорт знаходить аркуші лише за ІМЕНЕМ із карти `_ecr`, позиція ніде не читається:
- `src/Ecr.Adapters.Excel/ExcelImporter.cs:233` — `workbook.Worksheets.TryGetWorksheet(block.SheetName, out _)`;
- `:299` — `workbook.Worksheet(block.SheetName)`;
- `:337-339` — `StrayValueDetector.Find(workbook.Worksheet(sheet.Key), …)` лише для аркушів блоків карти;
- `:1233` — `workbook.Worksheets.TryGetWorksheet(ExcelWorkbookMap.SheetName, out var sheet)`.
Аркуш шапки в карті не значиться, тож ні читається, ні сканується. Формули аркушів даних посилаються на аркуші за іменем (`WriteFormulas`), визначених імен і областей друку експортер не створює (`git grep -n "DefinedName\|PrintArea\|NamedRange" -- src/Ecr.Adapters.Excel/ExcelExporter.cs` — порожньо).

**Зміна — покроково:**
1. `docs/tz/10-decisions.md`.
   - Новий рядок у таблиці рішень одразу після рядка `D-474` (`:911`); номер — наступний вільний на момент коміту (на `39aea41d9` останній — `D-474`; `D-475` уже згадано у звітах 01/04, тож перевір `grep -n "D-47[5-9]" docs/tz/10-decisions.md`). Нижче — `D-X2`:
     ```
     | **D-X2** | **✎ 2026-11-11. Експорт xlsx: аркуш шапки документа «Document header» — ПЕРШИЙ і активний, у вигляді форми «2. Contract» Land (переглядає xlsx-частину `D-290`).** (1) Порядок: видимий аркуш шапки — перша, єдина вибрана й активна вкладка (Excel відкриває книгу на ньому); далі аркуші даних у колишньому порядку; прихований `_ecr` — останнім (`X-2`). (2) Назва вкладки — мовою вивантаження, як заголовок панелі в UI: «Document header» / «Шапка документа» / «Құжаттың тақырып бөлігі» (`X-5`). (3) Розкладка — два видимі стовпці «підпис | значення»; підписи полів секції Contract — «EN — RU», порядок — як у формі Land (те саме правило, що в картці документа); технічний код поля — лише в прихованому стовпці C (`X-5`); значення полів-довідників — назвою запису мовою вивантаження (`X-3`); довгі значення переносяться, а не обрізаються на 50 символах (`X-5`). (4) Службові рядки: File Number, версія шаблону документа за період, останнім — «Reporting period» (`2026-11` / `Q2 2026` / `2026`) (`X-4`). Аркуш шапки, як і раніше, лише для шаблону з полями шапки. Решта `D-290` (CSV `000-header.csv`, JSON `header[]`, імпорт аркуш шапки ігнорує, видалені поля не експортуються, права — за `Document.View`) чинна. Імпорт знаходить аркуші за іменем із карти `_ecr`, тож ні позиція, ні нова назва на нього не впливають; старі книги з аркушем «Header» імпортуються так само. | рішення PO 11.11 (аудит 2026-11, `X-2`, `X-4`, `X-5`, через координатора) | `X-2` · `X-3` · `X-4` · `X-5` · `D-290` · `A2-12` |
     ```
   - У рядку `D-290` (`:743`) після «xlsx — видимий аркуш «Header» після аркушів даних і перед прихованим `_ecr`;» дописати: « ✎ 2026-11-11 (`D-X2`): xlsx-частину переглянуто — аркуш шапки «Document header» ПЕРШИЙ і активний, у вигляді форми «2. Contract» (підпис EN — RU | значення), зі службовими рядками File Number / Version / Reporting period; `_ecr` — останній;». Також у тому ж рядку фраза «Довгі значення в xlsx обрізаються на 50 символах ширини колонки» → дописати «(✎ `D-X2`: переносяться, ширина до 80)».
   - ⚠ Рядок `D-X2` вище свідомо описує ВЕСЬ новий вигляд аркуша (X-2 + X-3 + X-4 + X-5, усі ухвалені PO 11.11). Якщо лінію A ведуть кількома PR, рядок додає перший PR (X-2), а пункти (2)-(4) мають бути правдою на момент злиття останнього (X-5) — тож або X-2…X-5 в одному PR, або перший PR пише лише пункт (1), а X-4/X-5 дописують свої пункти (кожен раз перерахувати `CHECKSUMS.txt:11`).
   - У журнал змін унизу (після останнього рядка, `:1139`) рядок за зразком `:1055`:
     ```
     | 2026-11-11 | `D-X2`: експорт xlsx — аркуш шапки «Document header» першою й активною вкладкою, вигляд форми «2. Contract» (підпис EN — RU \| значення), File Number / Version / Reporting period (переглядає xlsx-частину `D-290`); решта `D-290` чинна; `docs/CHECKSUMS.txt` оновлено в тому ж коміті | рішення PO 11.11 (аудит 2026-11, X-2, X-4, X-5) |
     ```
   - `docs/CHECKSUMS.txt:11` (рядок `tz/10-decisions.md`): нова сума рахується з LF-вмісту, як у сторожі `SpecificationBaselineTests` (`tests/Ecr.Architecture.Tests/SpecificationBaselineTests.cs:8-24`): `git add docs/tz/10-decisions.md && git show :docs/tz/10-decisions.md | sha256sum` → замінити 64 hex на початку рядка 11.
   - Якщо X-5 іде в тому самому PR — обидві правки D-290 і одна сума (див. «Порядок впровадження»).
2. `src/Ecr.Adapters.Excel/ExcelExporter.cs`, коментар `:278-283`:
   ```diff
   -        // A2-12: поля шапки — окремим ВИДИМИМ аркушем ПІСЛЯ аркушів даних і ПЕРЕД
   -        // прихованою мапою. Не блоком зверху аркуша даних: той зсунув би рядки
   -        // таблиць, а з ними карту книги, заморожений рядок і стовпці зворотного
   -        // імпорту. Аркуш поза картою імпорт не читає й не сканує
   -        // (`StrayValueDetector` ходить лише по аркушах блоків карти).
   +        // A2-12 / ✎ D-X2 (X-2, рішення PO 11.11): поля шапки — окремим ВИДИМИМ аркушем, ПЕРШОЮ й активною вкладкою
   +        // (як «2. Contract» у Land). Пишеться ПІСЛЯ аркушів даних (їхні імена мають пріоритет у `usedNames`: аркуш даних
   +        // «Header» лишається «Header»), а на позицію 1 переноситься в `WriteHeaderSheetAsync`. Не блоком зверху аркуша
   +        // даних: той зсунув би рядки таблиць, а з ними карту книги, заморожений рядок і стовпці зворотного імпорту.
   +        // Аркуш поза картою імпорт не читає й не сканує (`StrayValueDetector` ходить лише по аркушах блоків карти), а
   +        // аркуші карти шукає за ІМЕНЕМ (`ExcelImporter`), тож позиція на імпорт не впливає.
            await WriteHeaderSheetAsync(workbook, snapshot, documentId, options, usedNames, ct).ConfigureAwait(false);
   ```
3. Той самий файл, `WriteHeaderSheetAsync`, одразу після `:337`:
   ```diff
            var sheet = workbook.Worksheets.Add(UniqueName(CleanSheetName(labels.Sheet, "Header"), usedNames));
   +
   +        // ✎ D-X2 (X-2): перша вкладка, єдина вибрана й активна — Excel відкриває книгу на шапці. Інші вкладки явно
   +        // знімаються з вибору: дві вибрані вкладки Excel відкриває групою («[Group]»), і правка однієї лягла б в обидві.
   +        sheet.Position = 1;
   +        foreach (var other in workbook.Worksheets)
   +        {
   +            other.TabSelected = false;
   +        }
   +
   +        sheet.TabSelected = true;
   +        sheet.SetTabActive();
   ```
   API ClosedXML 0.104.2 (`Directory.Packages.props:65`): `IXLWorksheet.Position { get; set; }`, `IXLWorksheet.TabSelected { get; set; }`, `IXLWorksheet.SetTabActive()`. `_ecr` додається пізніше (`:1352`) у кінець і лишається останнім. Документ без полів шапки — без змін: метод виходить раніше (`:329-332`), і книга, як і досі, відкривається на першому аркуші даних.
4. `docs/build/TESTER-GUIDE.md:1108` (рядок З-2а): «видимий аркуш «Header»/«Шапка»/«Тақырып» після аркушів даних (поле, код, значення; перший видимий аркуш — дані)» → «видимий аркуш «Header»/«Шапка»/«Тақырып» ПЕРШИМ і відкритим при відкритті книги (поле, код, значення; `D-X2`)».
5. `tests/Ecr.Adapters.Tests/Excel/ExcelExporterHeaderSheetTests.cs`:
   - summary `:19-21`: «окремим видимим аркушем «Header» ПІСЛЯ аркушів даних» → «окремим видимим аркушем «Header» ПЕРШОЮ й активною вкладкою (D-X2)»;
   - назву тесту `:114` `Аркуш_шапки_видимий_після_аркуша_даних_із_підписами_кодами_і_типізованими_значеннями` → `Аркуш_шапки_перший_активний_із_підписами_кодами_і_типізованими_значеннями`;
   - `:118-119`:
     ```diff
     -        // Порядок: дані → шапка → прихована карта; перший видимий аркуш — дані (сценарії тримаються за це).
     -        Assert.Equal(["Sheet", "Header", "_ecr"], [.. workbook.Worksheets.Select(w => w.Name)]);
     +        // ✎ D-X2: шапка → дані → прихована карта; шапка — єдина вибрана й активна вкладка.
     +        Assert.Equal(["Header", "Sheet", "_ecr"], [.. workbook.Worksheets.Select(w => w.Name)]);
     +        Assert.True(workbook.Worksheet("Header").TabActive);
     +        Assert.Equal(["Header"], [.. workbook.Worksheets.Where(w => w.TabSelected).Select(w => w.Name)]);
     ```
   - `:189`: `["Header", "Header~2", "_ecr"]` → `["Header~2", "Header", "_ecr"]` (аркуш даних «Header» зберігає ім'я, аркуш шапки — «Header~2» і стоїть першим).
   - Книга в тесті читається зі збереженого потоку (`ExportAsync`, `:234`: `new XLWorkbook(copy)`), тож `TabActive`/`TabSelected` перевіряються такими, як їх записав файл.
6. Сценарні тести беруть «перший видимий аркуш» як аркуш даних: `tests/Ecr.Scenarios.Tests/ImportRoundTripScenarios.cs:134`, `ExportFormulaScenarios.cs:45`, `ImportAtomicityScenarios.cs:362`, `BackgroundImportScenarios.cs:135`. Шаблони цих сценаріїв полів шапки не заводять (`git grep -ln "header-fields\|HeaderFieldDef\|headerFields" -- tests/Ecr.Scenarios.Tests` дає лише `DataEntryScenarios.cs` і `HeaderScenarios.cs`, які xlsx не експортують), тож аркуша шапки в їхніх книгах немає і селектор не міняється. Якщо прогін покаже інакше — замінити селектор на `workbook.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible && w.Name != <ім'я аркуша шапки>)` або на явне ім'я аркуша даних. `tests/Ecr.Adapters.Tests/Excel/ExcelSheetCopiesTests.cs:94, 122` чекають лише аркушів даних (`["Stacks 1", "Stacks 2"]`) — їхній знімок без полів шапки, без змін. e2e-спек, що читають порядок аркушів xlsx, немає (`git grep -n "SheetNames\|sheet_to_json\|exceljs" -- src/Ecr.Web/e2e` — порожньо).

**Тест-доказ:** `Ecr.Adapters.Tests` / `ExcelExporterHeaderSheetTests.Аркуш_шапки_перший_активний_із_підписами_кодами_і_типізованими_значеннями` (крок 5). Без кроку 3 порядок `["Sheet", "Header", "_ecr"]` — червоний перший `Assert.Equal`; прибрати лише `SetTabActive()` — червоний `TabActive`; прибрати цикл зняття вибору — якщо ClosedXML лишає вибраним і перший аркуш даних, червоний `TabSelected`-асерт (саме тому цикл явний). `Назва_аркуша_шапки_розводиться_…` (`:171-190`) червоніє без кроку 3. Наявні тести імпорту з шапкою (`:194-216`: незмінена книга → 0 змін; правка шапки ігнорується) мусять лишитися зеленими — це доказ, що позиція не впливає на зворотний імпорт.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~ExcelExporterHeaderSheetTests|FullyQualifiedName~ExcelImport|FullyQualifiedName~ExcelSheetCopiesTests"`; `dotnet test tests/Ecr.Scenarios.Tests --filter "FullyQualifiedName~ImportRoundTripScenarios|FullyQualifiedName~ExportFormulaScenarios|FullyQualifiedName~ImportAtomicityScenarios|FullyQualifiedName~BackgroundImportScenarios"`; `dotnet test tests/Ecr.Architecture.Tests --filter "FullyQualifiedName~SpecificationBaselineTests"` (сума `10-decisions.md`). Стенд: експорт документа Land → Excel відкривається на «Header», у рядку вкладок вона перша, групового режиму («[Group]» у заголовку вікна) немає; експорт → імпорт без правок → 0 змін.

**Ризики і побічні ефекти:** зовнішні макроси/інструкції, що беруть «перший аркуш» як дані, зламаються; у репо таких, крім сценаріїв кроку 6, немає. Контракт API, сід, міграції — без змін у X-2 (сід — у X-4/X-5). Для документа без шапки поведінка ідентична.

**Обґрунтування:** перенос позиції після запису — найменша зміна, що не міняє імен аркушів даних і карти книги. Альтернативу «писати шапку першою» відкинуто: змінився б пріоритет імен у `usedNames` (аркуш даних «Header» став би «Header~2»), а це ризик для карти імпорту.

**Рішення PO:** ухвалено 11.11 — «так» (аркуш шапки перший і активний). Відкритих питань немає; назву й розкладку (X-5) і службові рядки (X-4) PO ухвалив того ж дня — вони в рядку `D-X2`.
**Впевненість:** підтверджено читанням; запис `TabSelected` у файл ClosedXML — перевіряється тестом кроку 5.
**Залежності:** лінія A (той самий файл): після X-6, X-3; перед X-5, X-7. ⚠ X-5 крок 3 замінює `WriteHeaderSheetAsync` повністю — виконавець X-5 мусить перенести блок кроку 3 цієї знахідки (позиція, вибір, активність) у новий метод, інакше тест кроку 5 почервоніє.

---

### X-3 — Висока — Поля-списки шапки в xlsx — код запису (`AREA_001`), а не назва
**Категорія:** ui / i18n
**Де:** `src/Ecr.Application/Documents/DocumentHeaderExport.cs:107-117` (словник лише кодів), `:153-159` (значення = код); запис у книгу `src/Ecr.Adapters.Excel/ExcelExporter.cs:367-369`, ширина `:374`. Довідники Land: коди `AREA_001`, `REG_001`, `ONOFF_001`, `CTR_001` — `docs/delivery/reference-data/land-contract/*.csv`; назва — `DisplayL10n` (`tools/land/Import-ContractDictionaries.ps1:175-180`, `src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs:50`). Картка документа показує назву (`entry.display`, `src/Ecr.Web/src/features/documents/DocumentHeaderPanel.tsx:58-66`).
**Проблема:**
```csharp
// DocumentHeaderExport.cs:115-116
codes[registryId] = entries.GroupBy(e => e.Entry.Id)
    .ToDictionary(g => g.Key, g => Permits.Groups.LookupTargetEntries.Label(g.First().Entry.Code, g.First().TypeCode));
// :153-159
case CellDataType.Lookup when value.ValueRegistryEntryId is { } entryId:
    var code = … ? known : entryId.ToString(CultureInfo.InvariantCulture);
    return new HeaderExportRow(field.Code, label, code, code, false);
```
Для книги (`Value`) і для CSV/JSON (`Text`) — однаково код.
**Сценарій відтворення:** документ Land, Area = «1 BCMA», Region = «Atyrau», On/Offshore = «Offshore» → експорт xlsx → аркуш «Header», стовпець Value: `AREA_001`, `REG_001`, `ONOFF_001`. Очікувано: як у картці документа й у Land — «1 BCMA», «Atyrau», «Offshore».
**Вплив:** 7 із 11 полів Contract Land — списки (`header-fields.json`); аркуш шапки для людини фактично нечитабельний.
**Зміна — покроково:**
1. `src/Ecr.Application/Documents/DocumentHeaderExport.cs`:
```diff
@@ :12-16 (документація HeaderExportRow)
-/// <param name="Text">Значення текстом в інваріантній культурі; <c>null</c> — поле порожнє.</param>
+/// <param name="Text">Значення текстом в інваріантній культурі (для CSV/JSON; у списку — КОД запису); <c>null</c> — поле порожнє.</param>
 /// <param name="Value">
 /// Типізоване значення для книги (<see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/>
-/// або <see cref="string"/>); <c>null</c> — поле порожнє.
+/// або <see cref="string"/>; у списку — НАЗВА запису мовою вивантаження, X-3); <c>null</c> — поле порожнє.
 /// </param>
@@ :107-117
         var codes = new Dictionary<int, IReadOnlyDictionary<long, string>>();
+        var names = new Dictionary<int, IReadOnlyDictionary<long, string>>();
         foreach (var registryId in fields
…
             var entries = await Permits.Groups.LookupTargetEntries.ListAsync(registries, groups, registryId, ct).ConfigureAwait(false);
             codes[registryId] = entries.GroupBy(e => e.Entry.Id)
                 .ToDictionary(g => g.Key, g => Permits.Groups.LookupTargetEntries.Label(g.First().Entry.Code, g.First().TypeCode));
+
+            // X-3: людині в книзі — НАЗВА запису (як у картці документа); група дозволів — «номер — тип», як і була.
+            names[registryId] = entries.GroupBy(e => e.Entry.Id)
+                .ToDictionary(g => g.Key, g => DisplayOf(g.First(), lang));
         }
@@ :126-128
             rows.Add(value is null || value.IsEmpty
                 ? new HeaderExportRow(field.Code, shown, null, null, IsNumeric: false)
-                : Row(field, shown, value, codes));
+                : Row(field, shown, value, codes, names));
@@ :134-136
     private static HeaderExportRow Row(
         HeaderFieldDef field, string label, DocumentHeaderValueData value,
-        Dictionary<int, IReadOnlyDictionary<long, string>> codes)
+        Dictionary<int, IReadOnlyDictionary<long, string>> codes,
+        Dictionary<int, IReadOnlyDictionary<long, string>> names)
@@ :153-159
             case CellDataType.Lookup when value.ValueRegistryEntryId is { } entryId:
                 var code = field.LookupRegistryDefId is { } registry
                            && codes.TryGetValue(registry, out var entries)
                            && entries.TryGetValue(entryId, out var known)
                     ? known
                     : entryId.ToString(CultureInfo.InvariantCulture);
-                return new HeaderExportRow(field.Code, label, code, code, false);
+                var name = field.LookupRegistryDefId is { } named
+                           && names.TryGetValue(named, out var byId)
+                           && byId.TryGetValue(entryId, out var display)
+                    ? display
+                    : code;
+                return new HeaderExportRow(field.Code, label, code, name, false);
```
і новий приватний метод у тому ж класі:
```csharp
    /// <summary>Назва запису мовою вивантаження; порожня — код. Член групи дозволів — «номер — тип».</summary>
    private static string DisplayOf(Permits.Groups.LookupTargetEntry entry, string language)
    {
        if (entry.TypeCode is not null)
        {
            return Permits.Groups.LookupTargetEntries.Label(entry.Entry.Code, entry.TypeCode);
        }

        var display = entry.Entry.DisplayL10n?.Get(language);
        return string.IsNullOrWhiteSpace(display) ? entry.Entry.Code : display;
    }
```
2. `src/Ecr.Adapters.Excel/ExcelExporter.cs:374`:
```diff
-            lengths[2] = Math.Max(lengths[2], row.Text?.Length ?? 0);
+            lengths[2] = Math.Max(lengths[2], (row.Value as string)?.Length ?? row.Text?.Length ?? 0);
```
CSV (`DocumentDataExporter.cs:343-344`) і JSON (`:447-448`) читають `Text` — машинні формати лишаються з кодами, як і таблиці.
**Тест-доказ:** `tests/Ecr.Adapters.Tests/Excel/ExcelExporterHeaderSheetTests.cs`: (а) поле класу `private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();` і в `ExportAsync` (`:220-224`) передати `_registries` замість `Substitute.For<IRegistryStore>()`; (б) новий `[Fact] Поле_списку_шапки_пишеться_назвою_запису_а_не_кодом`: перебудувати `_snapshot` з одним полем `Field(26, "AREA", 1, CellDataType.Lookup, "Area", "Район работ", "Аудан")` + `field.SetLookup(40)`; `var entry = new RegistryEntry(40, EcrCode.Create("AREA_001"), new LocalizedText(new Dictionary<string,string>{["en"]="1 BCMA", ["ru"]="Строительство"}));` `typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, 501L);` `_registries.ListEntriesAsync(40, Arg.Any<CancellationToken>()).Returns([entry]);` значення `ValueRegistryEntryId = 501`; експорт `en` → комірка значення = `"1 BCMA"`, `ru` → `"Строительство"`. Без кроку 1 — `"AREA_001"`, червоний. Наявний `tests/Ecr.Application.Tests/Documents/DocumentDataExportPermitGroupTests.cs` (CSV/JSON групи — «номер — тип» через `Text`) має лишитися зеленим.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter FullyQualifiedName~ExcelExporterHeaderSheetTests`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~DocumentDataExport"`. Стенд: документ Land → експорт xlsx у en і ru → Area/Region/Contractor — назви.
**Ризики і побічні ефекти:** без додаткових запитів (ті самі записи довідника, `ListEntriesAsync` уже викликається). Поле типу `Unit` у шапці й далі пише числовий id одиниці (`DocumentHeaderExport.cs:161-163`) — у Land полів `Unit` немає, виправлення потребує порту одиниць; винесено в «Відкинуто/відкладено».
**Обґрунтування:** назва — для людини в xlsx; код лишається в CSV/JSON (машинні формати, звірка) — шапку імпорт не читає (D-290), тож назва в книзі не ламає зворотного шляху. Альтернатива «код — назва» в одній комірці — довша і не як у Land.
**Рішення PO:** не потрібне (якщо PO хоче саме «NAME» Land — «1 BCMA - Строительство…» двома мовами — це X-5, варіант підпису значень).
**Впевненість:** підтверджено читанням.
**Залежності:** робити до X-5 (той самий метод `WriteHeaderSheetAsync`).

---

### X-4 — Середня — В аркуші шапки немає File Number, Version і періоду вивантаження
**✎ рішення PO 11.11:** «так» — аркуш шапки містить File Number (BusinessKey документа), версію шаблону документа за період і звітний період вивантаження. Прийнято рекомендовані варіанти: (1) рядок «Reporting period» — останнім рядком; (2) аркуш шапки, як і досі, створюється лише для шаблону з полями шапки (`ExcelExporter.cs:329-332`). Уточнення до чернетки: для шаблону БЕЗ секції Contract рядки File Number і Version не відкидаються (як у картці), а йдуть перед рядком періоду — PO просив, щоб вони були в аркуші завжди (крок 4а в X-5). Час вивантаження окремим рядком НЕ додається: звіт його не рекомендує — книгу ідентифікують File Number, версія й період (і ім'я файлу X-1), а мітка часу робила б кожне вивантаження тих самих даних різним; якщо PO захоче — це один рядок `export.header.exportedAt` у поясі сайту (окрема правка).

**Рядки, що додає X-4 (ключі й тексти):**

| Рядок | Ключ підпису | en | ru | kz | Значення |
|---|---|---|---|---|---|
| File Number | `document.header.contract.fileNumber` (є, `RC15A-contract.sql:7-8`, `:20`) | `File Number — Номер файла` | `File Number — Номер файла` | `File Number — Файл нөмірі` | `Document.BusinessKey` (`DocumentSummary.BusinessKey`) |
| Version | `document.header.contract.version` (є, `RC15A-contract.sql:9-10`, `:21`) | `Version — Версия` | `Version — Версия` | `Version — Нұсқа` | версія шаблону документа за період (`TemplateVersionByPeriod[період]`, інакше `TemplateVersion`) |
| Reporting period | `export.header.period` (новий, сід кроку 5) | `Reporting period` | `Отчётный период` | `Есепті кезең` | `ExportFileName.Period(періодKey, PeriodKind)` (X-1): `2026-11` / `Q2 2026` / `2026` |

Підписи File Number/Version уже двомовні в каталозі — саме так їх показує картка документа; тексти каталогу не змінюються.

**Категорія:** ui
**Де:** `src/Ecr.Application/Documents/DocumentHeaderExport.cs:98` (лише `HeaderFieldDef`); маніфест Land — 13 позицій, з них `FileNumber` (11) і `Version` (13) службові, не `HeaderFieldDef` (`docs/delivery/reference-data/land-contract/header-fields.json`, `tools/land/Apply-ContractHeader.ps1:6-8`); картка документа їх показує (`src/Ecr.Web/src/features/documents/DocumentHeaderPanel.tsx:794-808`, `contractSection.ts:26-28`, D-330 `docs/tz/10-decisions.md:779`); підписи вже є в каталозі: `document.header.contract.fileNumber` / `.version` (`src/Ecr.Infrastructure/Persistence/Sql/seed/features/RC15A-contract.sql:7-10`, `:20-21`). Період: у видимих аркушах книги його немає взагалі (лише в прихованій `_ecr`, `ExcelExporter.cs:289-290`).
**Проблема:** аркуш шапки будується лише з `snapshot.HeaderFields` (`DocumentHeaderExport.cs:98`), тож 2 з 13 рядків форми Contract Land відсутні; періоду, за який вивантажено книгу, людина в книзі не бачить (а з X-1 до фіксу — і в імені файлу).
**Сценарій відтворення:** документ Land, File Number `LAND-0007`, версія шаблону `1.0.14.0`, період 2026-11 → експорт → аркуш шапки: 11 рядків, немає «File Number», «Version»; ніде не написано «2026-11». Очікувано: 13 позицій у порядку Land + період.
**Вплив:** кожен експорт документа з секцією Contract.
**Зміна — покроково** (після X-1 і X-5):
1. `src/Ecr.Application/Ports/IExcelExporter.cs` — `ExcelExportOptions`:
```diff
+/// <param name="DocumentBusinessKey">X-4: File Number (BusinessKey) документа для аркуша шапки; <c>null</c> — рядка немає.</param>
+/// <param name="TemplateVersionLabel">X-4: версія шаблону документа за період; <c>null</c> — рядка немає.</param>
+/// <param name="PeriodKind">X-4: періодичність проєкту (<c>PeriodKind.ToString()</c>) — для підпису періоду.</param>
 public sealed record ExcelExportOptions(
     bool IncludeFormulas, bool IncludeStyles, string Language, int PeriodKey,
-    IReadOnlyList<int>? HiddenTableDefIds = null, IReadOnlyList<int>? HiddenColumnDefIds = null);
+    IReadOnlyList<int>? HiddenTableDefIds = null, IReadOnlyList<int>? HiddenColumnDefIds = null,
+    string? DocumentBusinessKey = null, string? TemplateVersionLabel = null, string? PeriodKind = null);
```
2. `src/Ecr.Application/Documents/ExcelExchangeHandlers.cs` — у блоці X-1 (після `fileBaseName = …`) додати:
```csharp
            options = options with
            {
                DocumentBusinessKey = summary.BusinessKey,
                TemplateVersionLabel = summary.TemplateVersionByPeriod?.GetValueOrDefault(
                        options.PeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ?? summary.TemplateVersion,
                PeriodKind = summary.PeriodKind,
            };
```
(⚠ поставити ПІСЛЯ `options = options with { HiddenTableDefIds = … }` (`:99-105`), інакше перезапишеться.)
3. `src/Ecr.Application/Documents/DocumentHeaderExport.cs` — `HeaderExportLabels`:
```diff
     public const string CodeKey = "export.header.code";
+
+    /// <summary>X-4: ключі службових рядків (ті самі, що в картці документа) і рядка періоду.</summary>
+    public const string FileNumberKey = "document.header.contract.fileNumber";
+    public const string VersionKey = "document.header.contract.version";
+    public const string PeriodLabelKey = "export.header.period";
+
+    public string FileNumber { get; init; } = "File Number";
+    public string Version { get; init; } = "Version";
+    public string Period { get; init; } = "Reporting period";
@@ :57-59
         return new HeaderExportLabels(
             Pick(SheetKey, Fallback.Sheet), Pick(FieldKey, Fallback.Field),
-            Pick(ValueKey, Fallback.Value), Pick(CodeKey, Fallback.Code));
+            Pick(ValueKey, Fallback.Value), Pick(CodeKey, Fallback.Code))
+        {
+            FileNumber = Pick(FileNumberKey, Fallback.FileNumber),
+            Version = Pick(VersionKey, Fallback.Version),
+            Period = Pick(PeriodLabelKey, Fallback.Period),
+        };
```
4. `src/Ecr.Adapters.Excel/ExcelExporter.cs` — новий метод (викликається з нового `WriteHeaderSheetAsync` X-5):
```csharp
    /// <summary>X-4: службові рядки форми Contract (File Number, Version) і період вивантаження.</summary>
    private static List<Ecr.Application.Documents.HeaderExportRow> ServiceRows(
        ExcelExportOptions options, Ecr.Application.Documents.HeaderExportLabels labels)
    {
        var rows = new List<Ecr.Application.Documents.HeaderExportRow>(3);
        if (!string.IsNullOrWhiteSpace(options.DocumentBusinessKey))
        {
            rows.Add(new("FileNumber", labels.FileNumber, options.DocumentBusinessKey, options.DocumentBusinessKey, false, "fileNumber"));
        }

        if (!string.IsNullOrWhiteSpace(options.TemplateVersionLabel))
        {
            rows.Add(new("Version", labels.Version, options.TemplateVersionLabel, options.TemplateVersionLabel, false, "version"));
        }

        var period = Ecr.Application.Common.ExportFileName.Period(options.PeriodKey, options.PeriodKind);
        rows.Add(new("Period", labels.Period, period, period, false));
        return rows;
    }
```
(конструктор `HeaderExportRow` із 6-м параметром `ContractKey` вводить X-5.)
5. Сід — новий файл `src/Ecr.Infrastructure/Persistence/Sql/seed/features/XLS-header-sheet.sql` (спільний з X-5), секція en:
```sql
-- XLS:header-sheet ── X-4/X-5 (аудит 2026-11): рядок періоду в аркуші шапки ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'export.header.period', N'en', N'Reporting period', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'export.header.period', N'ru', N'Отчётный период'),
    (N'export.header.period', N'kz', N'Есепті кезең')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- XLS:header-sheet ── кінець секції ──
```
**Тест-доказ:** `ExcelExporterHeaderSheetTests` — новий `[Fact] Службові_рядки_File_Number_Version_і_період`: поля `AREA` (ordinal 1, en «Area») і `PERMIT` (ordinal 12, en «Permit Number»), `ExcelExportOptions(... , DocumentBusinessKey: "LAND-0007", TemplateVersionLabel: "1.0.14.0", PeriodKind: "Monthly")`, період 202611 → підписи стовпця A по порядку: `Area`, `File Number`, `Permit Number`, `Version`, `Reporting period`; значення `LAND-0007`, `1.0.14.0`, `2026-11`. Без кроку 4 — рядків немає, червоний. Другий `[Fact]`: шаблон без полів Contract (поле `OPERATOR`) → `Operator`, `File Number`, `Version`, `Reporting period` (рішення PO: File Number/Version завжди). Третій: `PeriodKind: "Quarterly"`, період 202602 → значення періоду `Q2 2026`. `ExportAccessTests` — розширити тест X-1: завдання несе `Options.DocumentBusinessKey == "LAND-0007"`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter FullyQualifiedName~ExcelExporterHeaderSheetTests`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~ExportAccessTests|FullyQualifiedName~ExportTaskHiddenScopeSerializationTests"` (нові поля опцій серіалізуються в JSON завдання); `dotnet test tests/Ecr.Architecture.Tests` (каталог/сід). Стенд: Land, експорт ru → «File Number — Номер файла», «Version — Версия» на своїх місцях форми Contract, останній рядок «Отчётный период | 2026-11»; шаблон не-Land із полями шапки → File Number, Version, період наприкінці.
**Ризики і побічні ефекти:** `ExcelExportOptions` серіалізується в черзі — нові необов'язкові поля сумісні з завданнями до зміни (`null` → рядків File Number/Version немає, період є). Для шаблонів без полів шапки аркуш шапки, як і раніше, не створюється (рішення PO, варіант (2а)). Сід X-4 і X-5 — один файл `XLS-header-sheet.sql`.
**Обґрунтування:** службові рядки беруться з тих самих джерел і з тими самими підписами, що в картці документа (`DocumentHeaderPanel.tsx:794-808`), — книга й екран не розходяться. Період — не з Land (річний шаблон), а вимога ECR: книга — за один період.
**Рішення PO:** ухвалено 11.11 — «так»: (1) рядок періоду — останнім; (2) аркуш шапки — лише для шаблону з полями шапки; File Number і Version — завжди (у секції Contract на своєму місці, без секції — перед періодом). Відкритих питань немає.
**Впевненість:** підтверджено читанням.
**Залежності:** X-1 (той самий `FindAsync` і місце в `ExportDocumentHandler`), X-5 (`HeaderExportRow.ContractKey`, `HeaderSheetLayout.Order`, новий `WriteHeaderSheetAsync`).

---

### X-5 — Середня — Розкладка аркуша шапки не як форма «2. Contract» Land
**✎ рішення PO 11.11:** «так» — вкладка зветься «Document header» (мовою вивантаження, як заголовок панелі в UI: «Document header» / «Шапка документа» / «Құжаттың тақырып бөлігі» — варіант (1а)); розкладка як форма Land «2. Contract» — «підпис EN — RU | значення», технічний код поля не видно (він лише в прихованому стовпці C — для діагностики; видимих стовпців два) — варіант (2а); поля-довідники показують назву запису (X-3). Вкладка — перша й активна (X-2). Імпорт не зачеплено (див. «Імпорт і старі книги» нижче). Питання (3) — номери в назвах аркушів даних — PO не ставилося в цьому раунді: це зміна даних шаблону Land, лишається пунктом 10 чекліста стенда, у X-5 не робиться.

**Імпорт і старі книги (перевірено кодом):** жоден код не шукає аркуш шапки за назвою. `git grep -n '"Header"' -- src` (поза сідом) дає лише запасну назву при створенні (`ExcelExporter.cs:337`) і `HeaderExportLabels.Fallback` (`DocumentHeaderExport.cs:40`); імпорт шукає лише аркуші блоків карти `_ecr` за їхніми іменами (`ExcelImporter.cs:233`, `:299`, `:337-339`) і саму карту (`:1233`). Тож книги, вивантажені ДО зміни (аркуш «Header»/«Шапка»/«Тақырып»), і книги після неї («Document header»…) імпортуються однаково: аркуш шапки ігнорується (доказ — `ExcelExporterHeaderSheetTests.cs:194-216`). Зворотної сумісності вмикати не треба. Тести, що звертаються до аркуша за старою назвою, оновлюються (крок 6).

**Категорія:** ui / i18n
**Де:** `src/Ecr.Adapters.Excel/ExcelExporter.cs:321-383` (`WriteHeaderSheetAsync`); `src/Ecr.Application/Documents/DocumentHeaderExport.cs:40` (`Fallback` «Header»), `:86-132`; сід `src/Ecr.Infrastructure/Persistence/Sql/seed/features/COLL-a212.sql:5`, `:9`, `:21` (назва аркуша «Header»/«Шапка»/«Тақырып»). Еталон у репо: картка документа відтворює форму Land — «два стовпці, підписи EN — RU, порядок як в Excel-аркуші «2. Contract»» (`docs/delivery/QUICKSTART-FIRST-TEST.md:249-252`; `src/Ecr.Web/src/features/documents/contractSection.ts:14-29` — порядок, `:54-59` — `bilingualLabel`); підписи Land — `tools/land/Apply-ContractHeader.ps1:116-130`; заголовок панелі в UI — «Document header»/«Шапка документа»/«Құжаттың тақырып бөлігі» (`seed/00-prelude.sql:2199`, `:5215`, `seed/features/CATALOG-en-base.sql:1128`).
**Проблема:**
```csharp
// ExcelExporter.cs:339-342, 351-352
sheet.Cell(1, 1).Value = labels.Field;   // «Field»
sheet.Cell(1, 2).Value = labels.Code;    // «Code» — технічний код поля (AREA, CONTRACTOR…)
sheet.Cell(1, 3).Value = labels.Value;
…
sheet.Cell(number, 1).Value = row.Label; // підпис ОДНІЄЮ мовою (DocumentHeaderExport.cs:122-123)
```
Відмінності від форми Contract (як її описує репо): (1) три стовпці з видимим технічним кодом замість «підпис | значення»; (2) підписи однією мовою замість «EN — RU»; (3) порядок — за `Ordinal` поля, без службових рядків (X-4); (4) назва вкладки «Header», а в UI — «Document header», у Land — «2. Contract»; (5) ширина значення ≤ 50 символів без переносу (`:377-380`, `MaxContentWidth = 50`, `:448`) — довгі назви Area (до ~100 символів у `area.csv`) обрізаються візуально (D-290 це визнає: «Довгі значення в xlsx обрізаються на 50 символах»); (6) без налаштувань друку.
**Сценарій відтворення:** експорт документа Land (en) → аркуш «Header»: `Field | Code | Value`, рядок `Area | Area | AREA_001` (X-3), «Filled in by, name, contact tel» без «— Кем заполнено…». У картці документа той самий розділ — «Area — Район работ: 1 BCMA».
**Вплив:** користувачі, що звіряють експорт із Land чи пересилають книгу; без доступу до системи книга — єдиний носій шапки.
**Зміна — покроково** (варіант А — «як картка документа»; деталі розкладки, яких немає в репо, — чеклістом нижче):
1. **Новий файл** `src/Ecr.Application/Documents/HeaderSheetLayout.cs` — серверне дзеркало `contractSection.ts`:
```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Порядок і підписи аркуша шапки як у формі «2. Contract» Land (X-5) — ТЕ САМЕ правило, що й у картці документа
/// (`src/Ecr.Web/src/features/documents/contractSection.ts`). ⚠ Змінюючи перелік тут, змініть і там.
/// </summary>
public static class HeaderSheetLayout
{
    private static readonly (string Key, string[] Names)[] ContractOrder =
    [
        ("area", ["area"]),
        ("contractor", ["contractor"]),
        ("region", ["region"]),
        ("location", ["location", "facility", "locationfacility"]),
        ("onshoreOffshore", ["onoffshore", "onshoreoffshore", "onshore", "offshore"]),
        ("filledInBy", ["filledinby", "filledby"]),
        ("contractHolder", ["contractholder"]),
        ("contractNumber", ["contractnumber", "contractno"]),
        ("activityType", ["typeofactivity", "activitytype"]),
        ("processedOn", ["processedon"]),
        ("fileNumber", ["filenumber", "businesskey"]),
        ("permitNumber", ["permitnumber", "permit"]),
        ("version", ["version"]),
    ];

    /// <summary>Ключ секції Contract для поля або <c>null</c> (за нормалізованим кодом чи англійським підписом).</summary>
    public static string? ContractKeyOf(string code, string? englishLabel)
    {
        var candidates = new[] { Normalize(code), Normalize(englishLabel) };
        foreach (var (key, names) in ContractOrder)
        {
            if (candidates.Any(c => c.Length > 0 && names.Contains(c, StringComparer.Ordinal)))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>«EN — RU», якщо обидві мови є й різні; інакше <paramref name="fallback"/> (як `bilingualLabel`).</summary>
    public static string BilingualLabel(LocalizedText label, string fallback)
    {
        ArgumentNullException.ThrowIfNull(label);
        var en = label.Values.GetValueOrDefault("en")?.Trim() ?? string.Empty;
        var ru = label.Values.GetValueOrDefault("ru")?.Trim() ?? string.Empty;
        return en.Length > 0 && ru.Length > 0 && !string.Equals(en, ru, StringComparison.Ordinal) ? $"{en} — {ru}" : fallback;
    }

    /// <summary>
    /// Рядки секції Contract за порядком Land (службові File Number/Version — на своєму місці, якщо секція є), далі решта
    /// полів у порядку шаблону, далі службові рядки, що не стали в секцію (File Number/Version без секції, період).
    /// </summary>
    public static IReadOnlyList<HeaderExportRow> Order(IReadOnlyList<HeaderExportRow> fields, IReadOnlyList<HeaderExportRow> service)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(service);

        var contract = new Dictionary<string, HeaderExportRow>(StringComparer.Ordinal);
        var other = new List<HeaderExportRow>();
        foreach (var row in fields)
        {
            if (row.ContractKey is { } key && contract.TryAdd(key, row))
            {
                continue;
            }

            other.Add(row);
        }

        var hasSection = contract.Count > 0;
        var tail = new List<HeaderExportRow>();
        foreach (var row in service)
        {
            // ✎ X-4 (рішення PO 11.11): File Number і Version — завжди. Із секцією Contract — на своєму місці за порядком Land
            // (полем шаблону з тим самим ключем не дублюються), без секції — у хвіст перед періодом (сервіс приходить у
            // порядку FileNumber, Version, Period).
            if (row.ContractKey is { } key && hasSection)
            {
                contract.TryAdd(key, row);
            }
            else
            {
                tail.Add(row);
            }
        }

        return [.. contract.Values.OrderBy(r => Rank(r.ContractKey!)), .. other, .. tail];
    }

    private static int Rank(string key) => Array.FindIndex(ContractOrder, e => e.Key == key);

    private static string Normalize(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : new string([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
```
2. `src/Ecr.Application/Documents/DocumentHeaderExport.cs`:
```diff
-public sealed record HeaderExportRow(string Code, string Label, string? Text, object? Value, bool IsNumeric);
+/// <param name="ContractKey">X-5: ключ секції «Contract» (<see cref="HeaderSheetLayout"/>); <c>null</c> — поле поза секцією.</param>
+public sealed record HeaderExportRow(
+    string Code, string Label, string? Text, object? Value, bool IsNumeric, string? ContractKey = null);
@@ :40
-    public static HeaderExportLabels Fallback { get; } = new("Header", "Field", "Value", "Code");
+    public static HeaderExportLabels Fallback { get; } = new("Document header", "Field", "Value", "Code");
@@ :86-93 (сигнатура ReadAsync)
         CancellationToken ct,
-        Permits.Groups.IPermitGroupResolver? groups = null)
+        Permits.Groups.IPermitGroupResolver? groups = null,
+        bool contractLayout = false)
@@ :120-129
         foreach (var field in fields)
         {
             var label = field.LabelL10n.Get(lang);
             var shown = string.IsNullOrWhiteSpace(label) ? field.Code : label;
             var value = values.GetValueOrDefault(field.Id);
+
+            // X-5: для книги — підпис «EN — RU» і ключ секції Contract, як у картці документа. CSV/JSON — без змін.
+            string? contractKey = null;
+            if (contractLayout)
+            {
+                contractKey = HeaderSheetLayout.ContractKeyOf(field.Code, field.LabelL10n.Values.GetValueOrDefault("en"));
+                if (contractKey is not null)
+                {
+                    shown = HeaderSheetLayout.BilingualLabel(field.LabelL10n, shown);
+                }
+            }
 
-            rows.Add(value is null || value.IsEmpty
+            rows.Add((value is null || value.IsEmpty
                 ? new HeaderExportRow(field.Code, shown, null, null, IsNumeric: false)
-                : Row(field, shown, value, codes, names));
+                : Row(field, shown, value, codes, names)) with { ContractKey = contractKey });
         }
```
3. `src/Ecr.Adapters.Excel/ExcelExporter.cs` — `WriteHeaderSheetAsync` (`:321-383`) замінити повністю (включає X-2, X-3, X-4, X-6):
```csharp
    /// <summary>
    /// Аркуш шапки як форма «2. Contract» Land (X-5): стовпці «підпис | значення», код поля — у прихованому
    /// стовпці C; порядок і підписи — як у картці документа (<see cref="Ecr.Application.Documents.HeaderSheetLayout"/>).
    /// Перша й активна вкладка книги (X-2). Без полів шапки в шаблоні аркуша нема.
    /// </summary>
    private async Task WriteHeaderSheetAsync(
        XLWorkbook workbook, TemplateVersionSnapshot snapshot, long documentId, ExcelExportOptions options,
        HashSet<string> usedNames, CancellationToken ct)
    {
        var fields = await Ecr.Application.Documents.DocumentHeaderExport
            .ReadAsync(headers, registries, snapshot, documentId, options.Language, ct, groups, contractLayout: true)
            .ConfigureAwait(false);

        if (fields.Count == 0)
        {
            return;
        }

        var labels = await Ecr.Application.Documents.HeaderExportLabels
            .ResolveAsync(catalog, options.Language, ct).ConfigureAwait(false);

        var rows = Ecr.Application.Documents.HeaderSheetLayout.Order(fields, ServiceRows(options, labels));

        var fallbackName = Ecr.Application.Documents.HeaderExportLabels.Fallback.Sheet;
        var sheet = workbook.Worksheets.Add(UniqueName(CleanSheetName(labels.Sheet, fallbackName), usedNames));

        // ✎ D-X2 (X-2): перша вкладка, єдина вибрана й активна (блок перенесено з X-2 дослівно).
        sheet.Position = 1;
        foreach (var other in workbook.Worksheets)
        {
            other.TabSelected = false;
        }

        sheet.TabSelected = true;
        sheet.SetTabActive();

        WriteText(sheet.Cell(1, 1), labels.Field);
        WriteText(sheet.Cell(1, 2), labels.Value);
        WriteText(sheet.Cell(1, 3), labels.Code);
        sheet.Range(1, 1, 1, 3).Style.Font.Bold = true;

        var labelWidth = labels.Field.Length;
        var valueWidth = labels.Value.Length;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var number = i + 2;

            WriteText(sheet.Cell(number, 1), row.Label);
            WriteText(sheet.Cell(number, 3), row.Code);

            var cell = sheet.Cell(number, 2);
            switch (row.Value)
            {
                case decimal numeric:
                    cell.Value = numeric;
                    break;
                case bool flag:
                    cell.Value = flag;
                    break;
                case DateTime date:
                    cell.Value = date;
                    cell.Style.DateFormat.Format = date.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                    break;
                case string text:
                    WriteText(cell, text);
                    break;
            }

            labelWidth = Math.Max(labelWidth, row.Label.Length);
            valueWidth = Math.Max(valueWidth, (row.Value as string)?.Length ?? row.Text?.Length ?? 0);
        }

        sheet.Column(1).Width = Math.Min(labelWidth + ContentPadding, MaxHeaderLabelWidth);
        sheet.Column(2).Width = Math.Min(valueWidth + ContentPadding, MaxHeaderValueWidth);
        sheet.Column(1).Style.Alignment.WrapText = true;
        sheet.Column(2).Style.Alignment.WrapText = true;
        sheet.Column(3).Hide();
        sheet.SheetView.FreezeRows(1);
        sheet.PageSetup.FitToPages(1, 0);
    }

    /// <summary>X-5: найбільша ширина стовпця підписів аркуша шапки, символів (далі — перенос).</summary>
    private const double MaxHeaderLabelWidth = 60;

    /// <summary>X-5: найбільша ширина стовпця значень аркуша шапки, символів (далі — перенос, а не обріз).</summary>
    private const double MaxHeaderValueWidth = 80;
```
(`ServiceRows` — X-4, ухвалено PO 11.11. Стовпець C прихований — видимих стовпців два, «підпис | значення», як вирішив PO; заголовок стовпця C лишається для діагностики й не видний.)
4. Сід: `src/Ecr.Infrastructure/Persistence/Sql/seed/features/COLL-a212.sql` — для нових баз змінити значення `export.header.sheet`: `:5` `N'Шапка'` → `N'Шапка документа'`, `:9` `N'Тақырып'` → `N'Құжаттың тақырып бөлігі'`, `:21` `N'Header'` → `N'Document header'`. У файлі `seed/features/XLS-header-sheet.sql` (X-4) — для наявних баз:
```sql
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'export.header.sheet', N'en', N'Header', N'Document header'),
    (N'export.header.sheet', N'ru', N'Шапка', N'Шапка документа'),
    (N'export.header.sheet', N'kz', N'Тақырып', N'Құжаттың тақырып бөлігі')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
```
(оновлення лише рядків, що ще мають старе значення — правка адміністратора зберігається, `seed/99-epilogue.sql:42-52`.)
5. `docs/tz/10-decisions.md` — рядок `D-X2` (X-2) уже описує назву вкладки й розкладку (див. X-2 крок 1 — текст рядка включає X-3/X-4/X-5); якщо X-5 іде ОКРЕМИМ PR після X-2 — дописати в `D-X2` ✎ лише те, чого там ще немає, і перерахувати `docs/CHECKSUMS.txt:11`. `docs/build/TESTER-GUIDE.md:1108` (З-2а): «видимий аркуш «Document header»/«Шапка документа»/«Құжаттың тақырып бөлігі» ПЕРШИМ і відкритим; два стовпці «підпис EN — RU | значення» в порядку форми Contract Land; списки — назвою запису; рядки File Number, Version, «Reporting period» (D-X2)».
6. Тести, що звертаються до аркуша шапки за старою назвою або старими стовпцями (`tests/Ecr.Adapters.Tests/Excel/ExcelExporterHeaderSheetTests.cs:119-120`, `:146` (`InlineData("en", "Header", …)` і сусідні мови), `:189`, `:209`), — оновити за «Тест-доказом» нижче; інших звернень за назвою «Header» у `tests/` і `src/Ecr.Web/e2e` немає (`git grep -n 'Worksheet("Header")'` — лише цей файл).

**Чекліст звірки з оригіналом Land на стенді** (оригінального `.xlsm` у репо немає — відкрити `RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm` поруч з експортом):
1. Назва вкладки: «2. Contract» чи «Document header»? Позиція: перша серед заповнюваних (після `Configuration`, `1. Sheet Selection`).
2. Чи є в Land рядок заголовків стовпців («Field | Value») — чи форма без нього (тоді прибрати рядок 1 і `FreezeRows`).
3. Стовпці: у яких стовпцях стоять підпис і значення (A/B чи з відступом, напр. B/D); чи є об'єднані комірки для значення; заголовок форми зверху (назва компанії, логотип).
4. Підписи: «EN — RU» в одній комірці чи EN і RU в різних стовпцях/рядках; точні тексти — порівняти з `Apply-ContractHeader.ps1:116-130`.
5. Порядок 13 рядків: Area, Contractor, Region, Location, On/Offshore, Filled by, Contract holder, Contract number, Type of activity, Processed on, File Number, Permit Number, Version (`header-fields.json`).
6. Вигляд значень списків: «1 BCMA - Строительство…» (поле NAME, двомовне) чи лише «1 BCMA» (display) — X-3 пише display мовою вивантаження.
7. Формати: «Processed on» — дата чи текст (у маніфесті `String`); ширини стовпців; перенос тексту; шрифт/заливка підписів (ролі `LAND_HEADER` CCFFCC — `land-template/README.md`, розділ «Стилі»).
8. Захист аркуша / заблоковані комірки (у Land форма, ймовірно, захищена; експорт ECR захисту не ставить ніде — `git grep -n "Protect" src/Ecr.Adapters.Excel` порожньо).
9. Друк: орієнтація, «вмістити на 1 сторінку», область друку.
10. Назви аркушів даних: у Land «3. Stationary Equipment», в експорті — «Stationary Equipment» (номер відкидає генератор моделі: `tools/land/model-gen/gen_ecr_model.py:639` `'nameEn': sh['name'].split('. ', 1)[-1]`). Чи потрібні номери — окреме рішення (зміна даних шаблону Land, нова версія шаблону).

**Тест-доказ:** `tests/Ecr.Application.Tests/Documents/HeaderSheetLayoutTests.cs` (новий): `ContractKeyOf("Area", null)` → `"area"`; `ContractKeyOf("X1", "Filled in by, name, contact tel")` → `null` (нормалізоване `filledinbynamecontacttel` не в переліку — як у клієнті), `ContractKeyOf("FilledBy", null)` → `"filledInBy"`; `BilingualLabel({en:"Area", ru:"Район работ"}, "Area")` → `"Area — Район работ"`; `BilingualLabel({en:"Processed on"}, "Processed on")` → `"Processed on"`; `Order`: поля `[Permit(permitNumber), Area(area), Note(null)]` + сервіс `[FileNumber(fileNumber), Version(version), Period(null)]` → `Area, FileNumber, Permit, Version, Note, Period`; без полів секції → сервісні File Number/Version ідуть у хвіст перед періодом: `Note, FileNumber, Version, Period` (рішення PO, X-4). `ExcelExporterHeaderSheetTests` — оновити очікування: стовпці `["Field","Value","Code"]`, код у прихованому стовпці C (`header.Column(3).IsHidden`), назва аркуша `Document header` (без каталогу — `Fallback`), тест мов `:144-158` — нові `sheetName`; `:189` — після X-2 і X-5 зіткнення з аркушем даних «Header» не відбувається; переписати тест на аркуш даних «Document header» → `["Document header~2", "Document header", "_ecr"]` (шапка перша — X-2); `:209` — `workbook.Worksheet("Document header").Cell(3, 2)`. Новий `[Fact] Підпис_поля_Contract_двомовний`: поле `AREA` з `en`/`ru` → `Cell(2,1)` = `"Area — Район работ"`. Падає без кроку 2 (підпис одномовний).
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter FullyQualifiedName~HeaderSheetLayoutTests`; `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~ExcelExporterHeaderSheetTests|FullyQualifiedName~ExcelImport"` (імпорт книги з новою назвою аркуша — 0 змін); `dotnet test tests/Ecr.Architecture.Tests` (сід: `#UiChanged`, переклади); стенд — чекліст вище плюс: стару книгу (вивантажену до X-5, аркуш «Header») імпортувати без правок → 0 змін.
**Ризики і побічні ефекти:** перелік `ContractOrder` дублюється в C# і TS — розійдуться при першій правці; зафіксувати коментарем в обох файлах (крок 1) і, за бажання, архітектурним тестом, що порівнює ключі. Для шаблонів не-Land порядок і підписи не змінюються (ключів Contract немає), змінюються лише стовпці й назва вкладки. CSV/JSON не змінюються (`contractLayout` = `false` за замовчуванням, `DocumentDataExporter` викликає `ReadAsync` без нього).
**Обґрунтування:** еталон, доступний у репо, — картка документа, яку вже звірили з Land (RC15-A, `docs/release-notes/rc-2026-11-03.md:15`); робити книгу «як картка» дає одну правду на екрані й у файлі. Точну Excel-розкладку (об'єднання, стилі, захист) без оригіналу не вгадати — тому чекліст, а не вигадані координати.
**Рішення PO:** ухвалено 11.11 — (1а) «Document header» мовою вивантаження; (2а) «підпис EN — RU | значення», код не видно. Не вирішено й поза X-5: номери в назвах аркушів даних (п. 10 чекліста — зміна шаблону Land). Деталі, яких немає в репо (об'єднання, стилі, захист, друк), — за чеклістом стенда; розбіжність з оригіналом Land — окрема правка після стенда.
**Впевненість:** відмінності підтверджено читанням; повна відповідність Land — потребує перевірки на стенді.
**Залежності:** після X-2, X-3, X-6 (той самий метод); X-4 — після X-5.

---

### X-6 — Низька — Текст шапки пишеться в обхід `WriteText` — губиться початковий апостроф
**Категорія:** correctness
**Де:** `src/Ecr.Adapters.Excel/ExcelExporter.cs:339-341`, `:351-352`, `:367-369`; правильний запис — `ExcelExporter.WriteText`, `:1035-1042`; доказ поведінки ClosedXML — `tests/Ecr.Adapters.Tests/Excel/ExcelExportLeadingApostropheTests.cs:14`, `:18-40`.
**Проблема:** `WriteText` існує саме тому, що «ClosedXML читає початковий апостроф присвоєного рядка як «префікс тексту» Excel і ВІДКИДАЄ його» (`:1039-1041`). Аркуш шапки пише `cell.Value = text` напряму — значення `'OOO Ромашка'` у книзі стає `OOO Ромашка'`.
**Сценарій відтворення:** поле шапки «Contractor» = `'Nur' LLP` → експорт → комірка значення `Nur' LLP`. Очікувано: дослівно `'Nur' LLP` (як у таблицях даних).
**Вплив:** рідкісні значення/підписи з апострофом на початку; спотворення тексту без попередження.
**Зміна — покроково:** `src/Ecr.Adapters.Excel/ExcelExporter.cs`:
```diff
@@ :351-352
-            sheet.Cell(number, 1).Value = row.Label;
-            sheet.Cell(number, 2).Value = row.Code;
+            WriteText(sheet.Cell(number, 1), row.Label);
+            WriteText(sheet.Cell(number, 2), row.Code);
@@ :367-369
                 case string text:
-                    cell.Value = text;
+                    WriteText(cell, text);
                     break;
```
(Якщо X-5 упроваджується, він уже містить цю зміну.)
**Тест-доказ:** `ExcelExporterHeaderSheetTests` — `[Fact] Початковий_апостроф_у_значенні_шапки_зберігається`: `values[22] = new DocumentHeaderValueData { ValueString = "'Nur' LLP" }` → після збереження/читання `header.Cell(3, 3).GetString() == "'Nur' LLP"` (до X-5; після X-5 — `Cell(3, 2)`). Без фіксу — `"Nur' LLP"`, червоний (той самий механізм, що доводить `ExcelExportLeadingApostropheTests`).
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~ExcelExporterHeaderSheetTests|FullyQualifiedName~ExcelExportLeadingApostropheTests"`.
**Ризики і побічні ефекти:** немає (шапку імпорт не читає).
**Обґрунтування:** одне правило запису тексту для всієї книги.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням + наявним тестом поведінки ClosedXML.
**Залежності:** перед X-5 (або в його складі).

---

### X-7 — Середня — Апостроф у назві аркуша ламає посилання формул; назва з `'` на краю / «History»
**Категорія:** correctness
**Де:** `src/Ecr.Adapters.Excel/ExcelExporter.cs:1291-1295` (`Coordinates`), `:1439-1456` (`CleanSheetName`), `:60` (`ForbiddenInSheetName`); джерело назв — `SheetCopyNaming.Effective(sheet.NameL10n, group?.NameOverride)` (`:1413`), назву групи копій аркуша задає користувач до 200 символів без обмеження набору (`src/Ecr.Application/Documents/DocumentSheetCopyHandler.cs:66`, `:371-379`).
**Проблема:**
```csharp
// ExcelExporter.cs:1294-1295
map[(block.TableCode, row.RowKey, column.Code)] =
    $"'{block.SheetName}'!{letter}{row.Number.ToString(CultureInfo.InvariantCulture)}";
```
В Excel апостроф усередині цитованої назви аркуша має бути подвоєний (`'Operator''s site'!B3`); тут — ні, тож формула з посиланням на аркуш «Operator's site» синтаксично зламана. Зворотний розбір (`FormulaTranslator.cs:139-146`) перемикає «в лапках» на кожному `'`, тож подвоєний апостроф він читає правильно. `CleanSheetName` не прибирає `'` на початку/в кінці (Excel таке ім'я забороняє) і не обробляє зарезервоване «History».
**Сценарій відтворення:** документ із копіями аркуша; перейменувати групу на `Operator's site` (UI копій аркуша) → експорт із формулами (`includeFormulas: true` — так замовляє `ExportButton.tsx:124`) → Excel: «We found a problem with some content…»/формули видалено (потребує перевірки на стенді). Назва `'Test'` → `workbook.Worksheets.Add` може відмовити (ClosedXML валідує назви — потребує перевірки).
**Вплив:** документи з користувацькими назвами груп аркушів, що містять апостроф (англійські присвійні форми); експорт падає або книга «ремонтується» з втратою формул.
**Зміна — покроково:** `src/Ecr.Adapters.Excel/ExcelExporter.cs`:
```diff
@@ :1291-1295
-                    // Ім'я аркуша в посиланні — в апострофах: коди аркушів
-                    // бувають із пробілами, і без них Excel читає формулу до
-                    // першого пробілу.
+                    // Ім'я аркуша в посиланні — в апострофах: коди аркушів
+                    // бувають із пробілами, і без них Excel читає формулу до
+                    // першого пробілу. ⛔ X-7: апостроф усередині назви — подвоєний (синтаксис Excel).
                     map[(block.TableCode, row.RowKey, column.Code)] =
-                        $"'{block.SheetName}'!{letter}{row.Number.ToString(CultureInfo.InvariantCulture)}";
+                        $"'{block.SheetName.Replace("'", "''", StringComparison.Ordinal)}'!{letter}{row.Number.ToString(CultureInfo.InvariantCulture)}";
@@ :1446-1451
-        name = name.Trim();
+        // ⛔ X-7: Excel забороняє апостроф на початку й у кінці назви і зарезервував «History».
+        name = name.Trim().Trim('\'').Trim();
+        if (string.Equals(name, "History", StringComparison.OrdinalIgnoreCase))
+        {
+            name += " 1";
+        }
 
         if (name.Length == 0)
```
**Тест-доказ:** `tests/Ecr.Adapters.Tests/Excel/ExcelExporterSheetNameTests.cs` (новий; конструювати експортер як у `ExcelExporterHeaderSheetTests.ExportAsync`, `IncludeFormulas: true`, таблиця з колонкою `Formula`, що посилається на іншу колонку того ж рядка): (1) аркуш `Text("Operator's site")` → формула комірки містить `'Operator''s site'!`; книга перечитується `new XLWorkbook(stream)` без винятку. Без фіксу — `'Operator's site'!`, червоний. (2) аркуш `Text("'Quoted'")` → ім'я аркуша `Quoted`. (3) `Text("History")` → `History 1`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~ExcelExporterSheetNameTests|FullyQualifiedName~ExportFormula|FullyQualifiedName~FormulaTranslator"`; стенд: перейменувати групу копій на `Operator's site`, експорт, відкрити в Excel без повідомлення про ремонт.
**Ризики і побічні ефекти:** змінюється ім'я аркуша для назв з `'` на краю / «History» — карта книги пишеться з тим самим іменем, імпорт працює за картою; старі книги з такими назвами не існують (не відкривалися б).
**Обґрунтування:** мінімальна правка в одному місці формування посилань.
**Рішення PO:** не потрібне.
**Впевненість:** подвоєння — підтверджено читанням (синтаксис Excel); поведінку ClosedXML на `'Name'` — потребує перевірки тестом.
**Залежності:** той самий файл, що X-2/X-5 — у тій самій лінії, після них.

---

### X-8 — Низька — Зріз звіту вивантажується як `snapshot-{id}.xlsx`
**✎ рішення PO 11.11 (у складі X-1):** зріз звіту називається за тим самим правилом, що й документ: «<Назва звіту> (<код звіту>) <Період> #<id>.xlsx» — та сама функція `ExportFileName.ForExport` (X-1), той самий формат періоду (`2026-11` / `Q2 2026` / `2026`) і той самий заголовок (`filename*` RFC 5987 + ASCII `filename`). `#<id>` лишається, бо зрізів одного звіту й періоду буває кілька.
**Категорія:** ui
**Де:** `src/Ecr.Application/Reporting/ExportSnapshotHandler.cs:128-129`; `src/Ecr.Application/Ports/IReportSnapshotBuilder.cs:93`; `src/Ecr.Infrastructure/Reporting/ReportSnapshotBuilder.cs:588-594`; `src/Ecr.Api/Controllers/ReportsController.cs:203`.
**Проблема:**
```csharp
return new SnapshotExport(
    string.Create(CultureInfo.InvariantCulture, $"snapshot-{snapshotId}.xlsx"), content);
```
Ні коду звіту, ні періоду. Решта експортів так чи інакше людські (`registry-<код>-<дата>`, `audit-<вкладка>-<UTC>`), зріз — ні.
**Сценарій відтворення:** «Звіти» → зріз IEC за II квартал 2026 → «Excel» → `snapshot-7.xlsx`. Очікувано (рішення PO 11.11): `<Назва звіту> (IEC) Q2 2026 #7.xlsx`.
**Вплив:** користувачі з `Report.Export`; низький.
**Зміна — покроково:**
1. `src/Ecr.Application/Ports/IReportSnapshotBuilder.cs` — після `FindProjectIdAsync` (`:93`):
```csharp
    /// <summary>X-8: код і назва звіту, період і періодичність проєкту зрізу — для імені файлу; <c>null</c> — зрізу немає.</summary>
    public Task<SnapshotNameFacts?> FindNameFactsAsync(long snapshotId, CancellationToken ct);
```
і запис у тому ж файлі:
```csharp
/// <summary>Факти для імені файлу зрізу (X-8).</summary>
public sealed record SnapshotNameFacts(
    string ReportCode, Ecr.Domain.ValueObjects.LocalizedText ReportName, int? PeriodKey, Ecr.Domain.Enums.PeriodKind? PeriodKind);
```
2. `src/Ecr.Infrastructure/Reporting/ReportSnapshotBuilder.cs` — після `:594`:
```csharp
    /// <inheritdoc />
    public async Task<SnapshotNameFacts?> FindNameFactsAsync(long snapshotId, CancellationToken ct)
        => await (from s in db.ReportSnapshots.AsNoTracking()
                  join v in db.ReportVersions.AsNoTracking() on s.ReportVersionId equals v.Id
                  join d in db.ReportDefs.AsNoTracking() on v.ReportDefId equals d.Id
                  join p in db.Projects.AsNoTracking() on s.ProjectId equals p.Id
                  where s.Id == snapshotId
                  select new SnapshotNameFacts(d.Code, d.NameL10n, s.PeriodKey, (Ecr.Domain.Enums.PeriodKind?)p.PeriodKind))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
```
3. `src/Ecr.Application/Reporting/ExportSnapshotHandler.cs:128-129`:
```diff
+        // X-8 (рішення PO 11.11): ім'я за правилом X-1 — назва звіту мовою користувача, код, період; id лишається.
+        var facts = await snapshots.FindNameFactsAsync(snapshotId, ct).ConfigureAwait(false);
         return new SnapshotExport(
-            string.Create(CultureInfo.InvariantCulture, $"snapshot-{snapshotId}.xlsx"), content);
+            ExportFileName.ForSnapshot(
+                facts?.ReportCode, facts?.ReportName.Get(currentUser.Language), facts?.PeriodKey,
+                facts?.PeriodKind?.ToString(), snapshotId) + ".xlsx",
+            content);
```
(`using Ecr.Application.Common;` уже є, `:3`; `currentUser` — параметр конструктора, `:36`, `currentUser.Language` вже вживається на `:141`; `LocalizedText.Get(string language, string fallback = "en")` — `LocalizedText.cs:34`; `ReportDef.NameL10n` — `ReportDefinitions.cs:32`.) `ICurrentUser.Language` — `string` (`ICurrentUser.cs:20`); вміст зрізу — потік (`ReportsController.cs:200-203`), `File(Stream, string)` його так само закриває.
4. `src/Ecr.Api/Controllers/ReportsController.cs:203` — той самий заголовок, що в X-1 (повне ім'я в `filename*`, ASCII-запасне в `filename`):
```diff
-        return File(book.Content, SnapshotExport.ContentType, book.FileName);
+        // ✎ X-8/X-1: filename*=UTF-8'' (повне ім'я) + ASCII filename; File без імені заголовка не перезаписує.
+        var baseName = Path.GetFileNameWithoutExtension(book.FileName);
+        var fallbackName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"snapshot-{id}");
+        Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
+        {
+            FileName = $"{Ecr.Application.Common.ExportFileName.AsciiFallback(baseName, fallbackName)}.xlsx",
+            FileNameStar = book.FileName,
+        }.ToString();
+        return File(book.Content, SnapshotExport.ContentType);
```
**Тест-доказ:** `ExportFileNameTests` — `[Theory] ForSnapshot` (описано в X-1): `("IEC", "Water report", 202602, "Quarterly", 7)` → `"Water report (IEC) Q2 2026 #7"`; `(null, null, null, null, 7)` → `"snapshot #7"`; `("IEC", null, null, null, 7)` → `"IEC #7"`. Наявні тести `ExportSnapshotHandler` з підставним `IReportSnapshotBuilder` отримають `null` → `snapshot #7.xlsx` — якщо якийсь тест чекає `snapshot-7.xlsx` (`git grep -n "snapshot-" -- tests`), оновити очікування на нове ім'я. Інтеграційний тест `FindNameFactsAsync` — у `tests/Ecr.Infrastructure.Tests` поруч із тестами `ReportSnapshotBuilder` (`git grep -l "new ReportSnapshotBuilder" tests`): зріз відомого звіту → `ReportCode`, `PeriodKey`, `PeriodKind` збігаються.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~ExportFileNameTests|FullyQualifiedName~ExportSnapshot"`; стенд: «Звіти» → Excel → ім'я `<Назва звіту> (IEC) Q2 2026 #N.xlsx` (кирилична назва — у Chrome/Edge/Firefox дослівно).
**Ризики і побічні ефекти:** +1 легкий запит на вивантаження зрізу (не на рядки). Тести фіксованої кількості запитів для `export.xlsx` зрізу — перевірити `git grep -n "snapshots/.*export.xlsx" tests | grep -i count`; якщо є стеля — підняти не можна, тоді об'єднати з `FindProjectIdAsync` в один запит.
**Обґрунтування:** той самий генератор імен, що в X-1 (рішення PO 11.11); `id` лишається для унікальності.
**Рішення PO:** ухвалено 11.11 разом із X-1 (те саме правило імені). Відкритих питань немає.
**Впевненість:** підтверджено читанням.
**Залежності:** X-1 (`ExportFileName`).

---

## Порядок впровадження

- **Лінія A (аркуш шапки, послідовно; файли `ExcelExporter.cs`, `DocumentHeaderExport.cs`, `HeaderSheetLayout.cs`, `ExcelExporterHeaderSheetTests.cs`, сід, `10-decisions.md`):** X-6 → X-3 → X-2 → X-5 → X-7 (усі ухвалені/не потребують PO станом на 11.11). X-5 переносить блок позиції/активності X-2 у новий `WriteHeaderSheetAsync`.
- **Лінія B (імена файлів; файли `ExportFileName.cs`, `ExcelExchangeHandlers.cs`, `IExportStore.cs`, `ExportStore.cs`, `ExcelExportJob.cs`, `DownloadExportHandler.cs`, `DocumentsController.cs`, `02-contracts.md`):** X-1 → X-8. Паралельно з лінією A (спільних файлів немає).
- **X-4** (✎ ухвалено PO 11.11) — після X-1 (той самий блок у `ExportDocumentHandler`, `ExportFileName.Period`) і X-5 (той самий `WriteHeaderSheetAsync`, `HeaderExportRow.ContractKey`, `HeaderSheetLayout.Order`): останнім, коли обидві лінії злиті.
- **X-8** — у лінії B після X-1 (`ExportFileName.ForSnapshot`, `AsciiFallback`; ще `ReportsController.cs`, `ExportSnapshotHandler.cs`, `IReportSnapshotBuilder.cs`, `ReportSnapshotBuilder.cs`).
- Сід `XLS-header-sheet.sql` створює той, хто робить першим із X-4/X-5; другий — дописує секцію.
- Контракт API (`openapi.snapshot.json`, `schema.d.ts`) не змінюється (тіло — файл, змінюється лише заголовок `Content-Disposition`); `02-contracts.md` — лише текст рядка (X-1). Міграцій немає. Сід — один новий файл `XLS-header-sheet.sql` (X-4 + X-5) і правка `COLL-a212.sql` (X-5).
- `10-decisions.md` + `CHECKSUMS.txt` — рядок `D-X2` (X-2 крок 1) описує весь новий вигляд аркуша (X-2+X-3+X-4+X-5); найпростіше — лінія A одним PR. Інакше кожен PR дописує свій пункт і перераховує суму. Ці самі два файли правлять звіти 07 (R-1, R-3) і 09 (V-7) — послідовно, сума перераховується при кожному злитті.

## Відкинуто

- **Клієнт перекриває ім'я файлу через `a.download`** — ні: документ і задачі — `<Anchor download>` без значення (`ExportButton.tsx:191-195`, `JobFacts.tsx:229`); довідники/PI/рядки UI — `fileNameOf(Content-Disposition)` з `filename*` у пріоритеті і запасним ім'ям лише без заголовка (`features/audit/fileName.ts:7-31`, `RegistryExportMenu.tsx:64`, `piData/api.ts:121`, `localization/api.ts:49`).
- **Імена CSV журналів аудиту й PI** (`audit-{tab}-{from:yyyyMMdd'T'HHmmss'Z'}-…`, `AuditExportHandlers.cs:50`; `pi-{id}-…Z`, `RawDataHandler.cs:163`; `pi-events-…`, `PiEventDataHandler.cs:338`) — ASCII, пояс явно позначено `Z` (UTC), закріплено тестами (`AuditSortAndExportTests.cs:253`, `AuditStructureJournalTests.Export.cs:36`). Не дефект. ⚠ Якщо колись туди додадуть кирилицю: `AuditController.cs:316-319` / `PiRawDataController.cs:118-121` ставлять `System.Net.Http…ContentDispositionHeaderValue.FileName`, що для не-ASCII дає RFC 2047 — тоді переходити на `FileNameStar`.
- **Ім'я експорту довідника** `registry-{code}-{yyyyMMdd}` (`ExportRegistryHandler.cs:224`) — прийнятне; закріплено `RegistryEntryHistoryExportHttpTests.cs:173`.
- **Поле шапки типу `Unit` пишеться числовим id одиниці** (`DocumentHeaderExport.cs:161-163`) — реальна дрібниця, але у шаблоні Land полів `Unit` у шапці немає (`header-fields.json`), а виправлення потребує порту одиниць у `DocumentHeaderExport`; відкладено (не входить у ліміт знахідок, кандидат у бэклог).
- **Аркуш шапки після даних** як дефект без PO — свідоме рішення D-290 (`10-decisions.md:743`); винесено як X-2 з питанням PO, ✎ PO 11.11 відповів «так» (X-2 тепер до впровадження).
- **Обрізання значень шапки на 50 символах** — задокументоване D-290; закривається X-5 (перенос тексту), окремою знахідкою не рахується.

## Перевірено й у порядку

- **Зворотний імпорт з аркушем шапки:** імпорт шукає аркуші за іменем із карти `_ecr` (`ExcelImporter.cs:233`, `:299`, `:337-339`, `:1233`), аркуш шапки поза картою не читається й не сканується; доказ — `ExcelExporterHeaderSheetTests.cs:194-216` (незмінена книга → 0 змін; правка шапки ігнорується). Тож перенесення шапки першою (X-2) безпечне.
- **Формули в тексті шапки:** ClosedXML 0.104.2 (`Directory.Packages.props:65`) пише рядок як текст, не формулу — доказ `RegistryWorkbookWriterTests.cs:64-97` (`=1+1`, `=HYPERLINK(…)`, `+cmd|…`, `@SUM` → `DataType == Text`, без `<f>`). CSV шапки знешкоджується `CsvFormat.Field` (`DocumentDataExporter.cs:341-344`).
- **Локаль чисел і дат шапки:** у xlsx — типізовані значення (`decimal`, `bool`, `DateTime` з форматом `yyyy-mm-dd`, `ExcelExporter.cs:355-366`), у CSV/JSON — інваріантні (`DocumentHeaderExport.cs:141`, `:151`).
- **Права на шапку в експорті:** перевірка видимості документа до постановки задачі й при завантаженні, лише замовник (`ExcelExchangeHandlers.cs:74`, `DownloadExportHandler.cs:84-99`).
- **Видалені поля шапки не експортуються** (`DocumentHeaderExport.cs:98`; тест `ExcelExporterHeaderSheetTests.cs:136-139`).
- **Імена аркушів даних:** заборонені `[ ] : * ? / \` замінюються, межа 31 символ, дублікати розводяться `~n`, номер копії не обрізається (`ExcelExporter.cs:1410-1474`); шапка з іменем, що збігається з аркушем даних, не губить даних (`ExcelExporterHeaderSheetTests.cs:170-190`).
- **Прихований службовий аркуш `_ecr`** — останній, `Hide()` (`ExcelExporter.cs:1352-1363`).
- **Заморожування:** аркуші даних — рядок заголовка першої таблиці (`:259-264`), шапка — рядок 1 (`:382`).
- **Мова підписів шапки:** ключі `export.header.*` en/ru/kz є (`COLL-a212.sql`), підпис поля — `LabelL10n` з відкатом на en (`LocalizedText.cs:34-45`).

## Самоперевірка

- **X-1:** перевідкрито `DocumentsController.cs:729-735` (рядок `:734` — `$"document-{id}-{exportId}.{extension}"`), `ExcelExchangeHandlers.cs:35`, `:74`, `:109`, `:119-121`, `:160-162` — «було» дослівне; `ExportStore.cs:26`, `:38`, `:41-57`, `:60-75`; `ExcelExportJob.cs:83-85`; `DownloadExportHandler.cs:62`, `:119`; `IExportStore.cs:36-37`, `:49`. Типи існують: `IDocumentStore.FindAsync(long, PeriodKeyFilter, CancellationToken)` (`IDocumentStore.cs:398`), `PeriodKeyFilter(int?)` (`:635`), `DocumentSummary.BusinessKey/NameL10n/PeriodKind/TemplateVersion/TemplateVersionByPeriod` (`:133-162`), `PeriodKind` заповнюється у `FindAsync` (`DocumentStore.cs:201`), значення `Monthly/Quarterly/Yearly/Custom` (`Enums.cs:112-118`). `ExportedBook` створюють лише `ExportStore` і `DownloadExportAccessTests` (`:60`, `:77`, `:97`) — новий параметр необов'язковий; `HandleAsync` повертає тепер `ExportedBook` — оновлено `:81`. Тест падає без фіксу: ім'я містить `exportId`/`FileBaseName == null`. Дублів у KNOWN-ISSUES/OPEN-ITEMS/TODO-REMAINING/WORK-QUEUE/questions/10-decisions/звітах 01–06 немає (`git grep` за «file name|filename|ім'я файлу|Content-Disposition» — збігів про експорт документа немає). ✎ **Після рішення PO 11.11:** формат періоду кварталу змінено на `Q2 2026`; `ForDocument`/`ForSnapshot` тепер — обгортки однієї `ForExport`; додано `AsciiFallback` і явний `Content-Disposition` через `Microsoft.Net.Http.Headers.ContentDispositionHeaderValue` (`FileName`/`FileNameStar`) — тип уже вживається в `Ecr.Api` (`CollectionSchedulesController.cs:4`); `File(byte[], string)` без імені заголовка не пише. `ExcelExportOptions` без фільтра копії (`IExcelExporter.cs:44-46`) — копія аркуша в імені не потрібна. Тест-доказ доповнено кейсами `AsciiFallback` (кирилиця/казахська) і `filename` ASCII в API-тесті.
- **X-2 (✎ переписано після рішення PO 11.11):** перевідкрито `ExcelExporter.cs:278-284` (коментар+виклик), `:310` (єдиний `.Position =` у файлі — потік), `:329-332` (вихід без полів шапки), `:337` (`Worksheets.Add`), `:1352`/`:1362` (`_ecr` додається пізніше й ховається); `ExcelImporter.cs:233`, `:299`, `:337-339`, `:1233` — пошук аркушів за іменем; `ExcelExporterHeaderSheetTests.cs:19-21`, `:114`, `:118-119`, `:189`, `:231-234` — «було» дослівне; `10-decisions.md:743`, `:1055`, `:1139`; `CHECKSUMS.txt:11`; `TESTER-GUIDE.md:1108`. Компіляція: `IXLWorksheet.Position`, `TabSelected`, `TabActive`, `SetTabActive()` — API ClosedXML 0.104.2 (`Directory.Packages.props:65`), `workbook.Worksheets` перелічуваний (`ExcelSheetCopiesTests.cs:94`). Тест падає без фіксу: порядок `["Sheet", "Header", "_ecr"]` і `TabActive == false`. Сценарії з `First(visible)` (`ImportRoundTripScenarios.cs:134`, `ExportFormulaScenarios.cs:45`, `ImportAtomicityScenarios.cs:362`, `BackgroundImportScenarios.cs:135`) — шаблони без полів шапки (перевірено `git grep`), e2e порядку аркушів не читають. Не дублює: D-290 — те саме рішення, яке PO переглянув; інших записів (KNOWN-ISSUES/OPEN-ITEMS/WORK-QUEUE) немає. Змін сіду, контракту, міграцій немає.
- **X-3:** `DocumentHeaderExport.cs:107-117`, `:126-128`, `:134-136`, `:153-159` — «було» дослівне; `LookupTargetEntry(RegistryEntry Entry, string? TypeCode)` (`LookupTargetEntries.cs:11`), `RegistryEntry.DisplayL10n` (`RegistryEntry.cs:50`), `LookupTargetEntries.Label` (`:36-37`), `HeaderFieldDef.SetLookup(int)` (`HeaderFieldDef.cs:75`), `RegistryEntry : Entity<long>` (`RegistryEntry.cs:17`). CSV/JSON читають `Text` (`DocumentDataExporter.cs:344`, `:448`) — не змінюються. Тест падає без фіксу (код замість назви).
- **X-4:** `ExcelExportOptions` (`IExcelExporter.cs:44-46`), `HeaderExportLabels` (`DocumentHeaderExport.cs:25-61`) — «було» дослівне; ключі `document.header.contract.fileNumber/version` існують у сіді (`RC15A-contract.sql:7-10`, `:20-21`); ключ `export.header.period` новий — додано en/ru/kz. `ServiceRows` використовує конструктор із `ContractKey` — тому залежність від X-5 явна. Ім'я константи `PeriodLabelKey` (не `PeriodKey`) — щоб не затінити тип `PeriodKey` у файлі з `using Ecr.Domain.ValueObjects`. ✎ **Після рішення PO 11.11:** рядки й ключі зведено в таблицю (ключі `document.header.contract.fileNumber/version` — `RC15A-contract.sql:7-10`, `:20-21`, тексти en/ru/kz там уже двомовні); період — через `ExportFileName.Period` (X-1, квартал `Q2 2026`); File Number/Version без секції Contract тепер ідуть у хвіст (зміна `HeaderSheetLayout.Order` в X-5) — тест-доказ оновлено. Мітку часу вивантаження не додано (обґрунтовано в ✎-рядку).
- **X-5:** `ExcelExporter.cs:321-383` перевідкрито (повна заміна); `contractSection.ts:14-29`, `:54-59` — перелік і правило підпису перенесено дослівно; `LocalizedText.Values` (`LocalizedText.cs:47`); сід-механізм `#UiChanged` (`99-epilogue.sql:42-52`, зразок `COLL-d14unmatched.sql:84-96`); `COLL-a212.sql:5`, `:9`, `:21` — «було» дослівне. `with { ContractKey = … }` — `HeaderExportRow` є `record`, тож `with` компілюється. `gen_ecr_model.py:639` — рядок `'nameEn': sh['name'].split('. ', 1)[-1]` перевірено. ✎ **Після рішення PO 11.11:** назва за варіантом (1а) — ключ `export.header.sheet` (`DocumentHeaderExport.cs:28`, сід `COLL-a212.sql:5`, `:9`, `:21`); пошуку аркуша шапки за назвою в коді немає (`git grep -n '"Header"' -- src` поза сідом — лише `ExcelExporter.cs:337` і `DocumentHeaderExport.cs:40`), імпорт — за картою (`ExcelImporter.cs:233`, `:299`, `:337-339`, `:1233`), тож старі книги сумісні без змін. Блок активності вкладки в новому методі синхронізовано з X-2 (цикл зняття вибору). `HeaderSheetLayout.Order` змінено під рішення X-4.
- **X-6:** `ExcelExporter.cs:351-352`, `:367-369`, `WriteText` `:1035-1042` — «було» дослівне; механізм доведено наявним тестом `ExcelExportLeadingApostropheTests`.
- **X-7:** `ExcelExporter.cs:1291-1295`, `:1439-1456` — «було» дослівне; назву групи задає користувач без обмеження набору (`DocumentSheetCopyHandler.cs:66`, `:371`); зворотний розбір подвоєного апострофа — `FormulaTranslator.cs:139-146`. Поведінку ClosedXML на `'Name'` позначено «потребує перевірки». Підвищено до «Середньої» через доступність звичайному користувачеві.
- **X-8:** `ExportSnapshotHandler.cs:128-129`, `ReportSnapshotBuilder.cs:588-594`, `IReportSnapshotBuilder.cs:93` перевідкрито; сутності `ReportSnapshot.ReportVersionId/ProjectId/PeriodKey`, `ReportVersion.ReportDefId`, `ReportDef.Code` (`ReportDefinitions.cs:31`, `:72`, `:136-138`), `db.ReportSnapshots/ReportVersions/ReportDefs` (`EcrDbContext.cs:207-209`), `db.Projects` (`:89`); `IReportSnapshotBuilder` реалізує лише `ReportSnapshotBuilder` (`git grep ": IReportSnapshotBuilder"`). ✎ **Після рішення PO 11.11:** ім'я — `ExportFileName.ForSnapshot` → `ForExport` (те саме правило X-1) з назвою звіту `ReportDef.NameL10n` (`ReportDefinitions.cs:32`) мовою `currentUser.Language` (`ExportSnapshotHandler.cs:36`, `:141`; `ICurrentUser.cs:20` — `string`); `ReportsController.cs:196-203` — той самий явний заголовок, вміст — потік.
- **Вилучено/змінено під час перевірки:** (1) «імпорт не знайде дані, якщо шапка перша» — відкинуто (імпорт за іменами); (2) «формула-ін'єкція в тексті шапки xlsx» — відкинуто (доказ `RegistryWorkbookWriterTests`); (3) «назви аркушів даних Land без номера» — не окрема знахідка, а пункт 10 чекліста X-5 (зміна даних шаблону, рішення PO); (4) «Unit-поле шапки як id» — у «Відкинуто/відкладено»; (5) «маршрутний `{id}` в імені файлу» — включено в X-1, а не окремо.
