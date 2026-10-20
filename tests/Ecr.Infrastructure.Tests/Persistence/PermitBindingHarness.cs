// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingHarness.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Обробники прив'язок аркуша до дозволу над справжніми сховищами й базою (Permits, PM-09): один контекст на «запит».
/// </summary>
internal sealed class PermitBindingHarness : IAsyncDisposable
{
    private readonly PermitHarness _inner;

    private PermitBindingHarness(PermitHarness inner, Func<ITemplateVersionStore, ITemplateVersionStore>? wrapStore = null)
    {
        _inner = inner;
        Bindings = new PermitBindingStore(inner.Db);
        Store = new TemplateVersionStore(inner.Db);

        // Обгортка порту версій (тест гонки ставить паузу перед блоком версії); без неї - справжнє сховище.
        ITemplateVersionStore store = wrapStore?.Invoke(Store) ?? Store;
        Versions = new Repository<TemplateVersion, int>(inner.Db);
        MetadataCache = Substitute.For<IMetadataCache>();
        Classifier = new ChangeClassifier();

        List = new ListPermitBindingsHandler(Versions, Bindings, inner.Access, inner.User);
        Get = new GetPermitBindingHandler(Versions, Bindings, inner.Access, inner.User);
        Save = new SavePermitBindingHandler(
            Versions, Bindings, inner.Permits, inner.Registries, store, Classifier, MetadataCache,
            inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User);
        Delete = new DeletePermitBindingHandler(
            Versions, Bindings, store, Classifier, MetadataCache, inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User);
        SaveTable = new Ecr.Application.Templates.SaveTableDefHandler(
            store, Classifier, MetadataCache, inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User, Bindings);
        DeleteTable = new Ecr.Application.Templates.DeleteTableDefHandler(
            store, Classifier, MetadataCache, inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User, Bindings);
        DeleteColumn = new Ecr.Application.Templates.DeleteColumnDefHandler(
            store, Classifier, MetadataCache, inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User, Bindings);
        DeleteSheet = new Ecr.Application.Templates.DeleteSheetDefHandler(
            store, Classifier, MetadataCache, inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User, Bindings);
        Preview = new PreviewPermitBindingHandler(
            Versions, Bindings, inner.Permits, inner.Registries, store, new PermitResolver(inner.Registries),
            new RegistrySnapshotLoader(inner.Db), Documents, Headers, Periods, RowsStore, CellsStore, new UnitStore(inner.Db),
            inner.Access, inner.User, inner.Clock);
    }

    public PermitHarness Types => _inner;

    public PermitBindingStore Bindings { get; }

    public TemplateVersionStore Store { get; }

    public Repository<TemplateVersion, int> Versions { get; }

    public IMetadataCache MetadataCache { get; }

    public ChangeClassifier Classifier { get; }

    public ListPermitBindingsHandler List { get; }

    public GetPermitBindingHandler Get { get; }

    public SavePermitBindingHandler Save { get; }

    public DeletePermitBindingHandler Delete { get; }

    public PreviewPermitBindingHandler Preview { get; }

    /// <summary>Правка таблиці шаблону (PM-09b): охоронець прив'язок.</summary>
    public Ecr.Application.Templates.SaveTableDefHandler SaveTable { get; }

    /// <summary>Видалення таблиці шаблону (PM-09b).</summary>
    public Ecr.Application.Templates.DeleteTableDefHandler DeleteTable { get; }

    /// <summary>Видалення колонки шаблону (PM-09b).</summary>
    public Ecr.Application.Templates.DeleteColumnDefHandler DeleteColumn { get; }

    /// <summary>Видалення аркуша шаблону (PM-09b2).</summary>
    public Ecr.Application.Templates.DeleteSheetDefHandler DeleteSheet { get; }

    /// <summary>Порти документа: у перегляді без документа не торкаються, у перегляді з документом - заглушки тесту.</summary>
    public IDocumentStore Documents { get; } = Substitute.For<IDocumentStore>();

    public IDocumentHeaderStore Headers { get; } = Substitute.For<IDocumentHeaderStore>();

    public IPeriodStore Periods { get; } = Substitute.For<IPeriodStore>();

    public IRowStore RowsStore { get; } = Substitute.For<IRowStore>();

    public ICellStore CellsStore { get; } = Substitute.For<ICellStore>();

    public EcrDbContext Db => _inner.Db;

    public static string[] Everything => ["Template.View", "Template.Edit", .. PermitHarness.Everything];

    /// <summary>Нова «сесія» тієї самої особи: свій контекст, як у наступному запиті.</summary>
    public async Task<PermitBindingHarness> NextRequestAsync(Func<ITemplateVersionStore, ITemplateVersionStore>? wrapStore = null)
        => new(await _inner.NextRequestAsync().ConfigureAwait(false), wrapStore);

    /// <summary>Будує обробники для нового користувача з потрібними правами.</summary>
    /// <param name="sql">Фікстура бази.</param>
    /// <param name="permissions">Глобальні права.</param>
    public static async Task<PermitBindingHarness> CreateAsync(SqlServerFixture sql, string[] permissions)
        => new(await PermitHarness.CreateAsync(sql, permissions).ConfigureAwait(false));

    /// <summary>
    /// Створює й публікує тип дозволу з таблицею <c>Emissions</c>: ключ <c>Substance</c> (текст), ліміт
    /// <c>Limit</c> (Decimal 12,3), примітка <c>Note</c> (текст).
    /// </summary>
    /// <param name="typeCode">Код типу.</param>
    public async Task PublishEmissionsTypeAsync(string typeCode)
    {
        static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
        static PermitTypeColumnDto Col(string code, string type, bool key)
            => new(code, Name(code), type, null, key, key, null, null, null, null, null, null, null, 1);

        var created = await Types.Create.HandleAsync(new CreatePermitTypeRequest(typeCode, Name("Air"), "Air"), default).ConfigureAwait(false);
        var table = new PermitTypeTableDto(
            "Emissions",
            Name("Emissions"),
            1,
            [
                Col("Substance", "String", true),
                Col("Limit", "Decimal", false) with { Precision = 12, Scale = 3 },
                Col("Note", "String", false),
            ],
            []);

        await using var put = await Types.NextRequestAsync().ConfigureAwait(false);
        var draft = await put.Replace.HandleAsync(
            typeCode, new ReplacePermitTypeRequest(created.RowVersion, Name("Air"), "Air", [table], []), default).ConfigureAwait(false);

        await using var publish = await Types.NextRequestAsync().ConfigureAwait(false);
        await publish.Publish.HandleAsync(typeCode, new PublishPermitTypeRequest("перший випуск для прив'язок", draft.RowVersion), default)
            .ConfigureAwait(false);
    }

    /// <summary>Заводить дозвіл (шапку й рядки таблиці <c>Emissions</c>) прямо в реєстри - як це робить сторінка дозволу.</summary>
    /// <param name="typeCode">Код опублікованого типу з <see cref="PublishEmissionsTypeAsync"/>.</param>
    /// <param name="number">Номер дозволу (він же код запису).</param>
    /// <param name="location">Розташування.</param>
    /// <param name="validFrom">Перший чинний день.</param>
    /// <param name="validTo">Перший нечинний день.</param>
    /// <param name="isActive">Чи активний (чернетка - ні).</param>
    /// <param name="emissions">Рядки: речовина, ліміт, примітка.</param>
    public async Task<SeededPermit> SeedPermitAsync(
        string typeCode,
        string number,
        string location,
        DateOnly? validFrom,
        DateOnly? validTo,
        bool isActive,
        params (string Substance, decimal Limit, string? Note)[] emissions)
    {
        var type = (await Types.Permits.FindByCodeAsync(typeCode, default).ConfigureAwait(false))!;
        var header = (await Types.Registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, default).ConfigureAwait(false))!;
        var table = (await Types.Permits.ListTablesAsync(type.Id, default).ConfigureAwait(false)).Single(t => t.Code == "Emissions");
        var rows = (await Types.Registries.FindDefinitionByIdAsync(table.RegistryDefId, default).ConfigureAwait(false))!;

        var entry = new RegistryEntry(header.Id, EcrCode.Create(number), PermitHarness.Text(number), 1, DateTime.UnixEpoch);
        entry.SetValidity(validFrom, validTo);
        if (!isActive)
        {
            entry.Deactivate();
        }

        Db.RegistryEntries.Add(entry);
        await Db.SaveChangesAsync().ConfigureAwait(false);
        Value(entry.Id, header, "Permit_Number", CellDataType.String, number);
        Value(entry.Id, header, "Location", CellDataType.String, location);

        var rowIds = new List<long>();
        var ordinal = 0;
        foreach (var (substance, limit, note) in emissions)
        {
            var row = new RegistryEntry(rows.Id, EcrCode.Create($"{number}_{++ordinal}"), PermitHarness.Text(substance), 1, DateTime.UnixEpoch);
            row.SetOrdinal(ordinal);
            Db.RegistryEntries.Add(row);
            await Db.SaveChangesAsync().ConfigureAwait(false);
            Value(row.Id, rows, "PERMIT", CellDataType.Lookup, entry.Id);
            Value(row.Id, rows, "Substance", CellDataType.String, substance);
            Value(row.Id, rows, "Limit", CellDataType.Decimal, limit);
            Value(row.Id, rows, "Note", CellDataType.String, note);
            rowIds.Add(row.Id);
        }

        await Db.SaveChangesAsync().ConfigureAwait(false);
        return new SeededPermit(entry.Id, header.Id, rowIds);
    }

    private void Value(long entryId, RegistryDef definition, string fieldCode, CellDataType type, object? value)
    {
        var field = definition.Fields.Single(f => f.Code == fieldCode);
        var stored = new RegistryValue(entryId, field.Id);
        stored.Set(type, value, null);
        Db.RegistryValues.Add(stored);
    }

    /// <summary>
    /// Шаблон-чернетка для прив'язки: таблиця з динамічними рядками, колонки <c>C1</c> (текст), <c>C2</c> (Decimal 12,3, лише
    /// для читання), <c>C3</c> (Decimal, редагована), додана <c>C4</c> (текст, лише для читання) і поле шапки <c>Facility</c>.
    /// </summary>
    /// <param name="sql">Фікстура бази.</param>
    /// <param name="withDocument">
    /// <c>false</c> - версія без жодного документа (структурні зміни в ній не <c>Breaking</c>); тоді в
    /// <see cref="PreparedSheet.Doc"/> заповнені лише ідентифікатори шаблону, аркуша, таблиці й колонок.
    /// </param>
    public static async Task<PreparedSheet> PrepareSheetAsync(SqlServerFixture sql, bool withDocument = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = withDocument
            ? await builder.BuildAsync(rowCount: 0, rowMode: TableRowMode.Dynamic, ct: default).ConfigureAwait(false)
            : await BuildWithoutDocumentAsync(builder).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);

        var note = new ColumnDef(doc.TableDefId, EcrCode.Create($"C4_{doc.TableDefId}"), PermitHarness.Text("Note"), 4, CellDataType.String);
        note.SetReadOnly(true);
        db.ColumnDefs.Add(note);

        var facility = new HeaderFieldDef(doc.TemplateVersionId, EcrCode.Create("Facility"), PermitHarness.Text("Facility"), 1, CellDataType.String);
        db.HeaderFieldDefs.Add(facility);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new PreparedSheet(doc, note.Id, facility.Id);
    }

    private static async Task<TestDocument> BuildWithoutDocumentAsync(TestDocumentBuilder builder)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await using var db = builder.CreateContext();

        var template = new Template(EcrCode.Create($"PTP{tag}"), PermitHarness.Text($"Template {tag}"), 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, $"1.0.0.{tag}", 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"PSH{tag}"), PermitHarness.Text("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"PTB{tag}"), PermitHarness.Text("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columns = new[]
        {
            new ColumnDef(table.Id, EcrCode.Create($"C1_{tag}"), PermitHarness.Text("C1"), 1, CellDataType.String),
            new ColumnDef(table.Id, EcrCode.Create($"C2_{tag}"), PermitHarness.Text("C2"), 2, CellDataType.Decimal),
            new ColumnDef(table.Id, EcrCode.Create($"C3_{tag}"), PermitHarness.Text("C3"), 3, CellDataType.Decimal),
        };
        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new TestDocument(
            template.Id, version.Id, sheet.Id, sheet.Code, table.Id, [.. columns.Select(c => c.Id)], [],
            ProjectId: 0, PeriodKey: new PeriodKey(202601), DocumentId: 0, TableInstanceId: 0, RowIds: []);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Заведений дозвіл.</summary>
/// <param name="EntryId">Запис-шапка.</param>
/// <param name="HeaderRegistryDefId">Реєстр шапок типу.</param>
/// <param name="RowEntryIds">Записи таблиці <c>Emissions</c> у порядку заведення.</param>
internal sealed record SeededPermit(long EntryId, int HeaderRegistryDefId, IReadOnlyList<long> RowEntryIds);

/// <summary>Підготовлений шаблон для тестів прив'язок.</summary>
/// <param name="Doc">Ланцюг шаблону: <c>ColumnDefIds[0]</c> - текст, <c>[1]</c> - ліміт, <c>[2]</c> - редагована.</param>
/// <param name="NoteColumnId">Колонка-примітка (текст, лише для читання).</param>
/// <param name="FacilityHeaderId">Поле шапки <c>Facility</c>.</param>
internal sealed record PreparedSheet(TestDocument Doc, int NoteColumnId, int FacilityHeaderId);
