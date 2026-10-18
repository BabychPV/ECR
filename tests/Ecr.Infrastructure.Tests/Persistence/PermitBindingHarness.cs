// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingHarness.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
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

    private PermitBindingHarness(PermitHarness inner)
    {
        _inner = inner;
        Bindings = new PermitBindingStore(inner.Db);
        Store = new TemplateVersionStore(inner.Db);
        Versions = new Repository<TemplateVersion, int>(inner.Db);
        MetadataCache = Substitute.For<IMetadataCache>();
        Classifier = new ChangeClassifier();

        List = new ListPermitBindingsHandler(Versions, Bindings, inner.Access, inner.User);
        Get = new GetPermitBindingHandler(Versions, Bindings, inner.Access, inner.User);
        Save = new SavePermitBindingHandler(
            Versions, Bindings, inner.Permits, inner.Registries, Store, Classifier, MetadataCache,
            inner.Audit, inner.Uow, inner.Clock, inner.Access, inner.User);
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

    public EcrDbContext Db => _inner.Db;

    public static string[] Everything => ["Template.View", "Template.Edit", .. PermitHarness.Everything];

    /// <summary>Нова «сесія» тієї самої особи: свій контекст, як у наступному запиті.</summary>
    public async Task<PermitBindingHarness> NextRequestAsync()
        => new(await _inner.NextRequestAsync().ConfigureAwait(false));

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

    /// <summary>
    /// Шаблон-чернетка для прив'язки: таблиця з динамічними рядками, колонки <c>C1</c> (текст), <c>C2</c> (Decimal 12,3, лише
    /// для читання), <c>C3</c> (Decimal, редагована), додана <c>C4</c> (текст, лише для читання) і поле шапки <c>Facility</c>.
    /// </summary>
    /// <param name="sql">Фікстура бази.</param>
    public static async Task<PreparedSheet> PrepareSheetAsync(SqlServerFixture sql)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 0, rowMode: TableRowMode.Dynamic, ct: default).ConfigureAwait(false);

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

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Підготовлений шаблон для тестів прив'язок.</summary>
/// <param name="Doc">Ланцюг шаблону: <c>ColumnDefIds[0]</c> - текст, <c>[1]</c> - ліміт, <c>[2]</c> - редагована.</param>
/// <param name="NoteColumnId">Колонка-примітка (текст, лише для читання).</param>
/// <param name="FacilityHeaderId">Поле шапки <c>Facility</c>.</param>
internal sealed record PreparedSheet(TestDocument Doc, int NoteColumnId, int FacilityHeaderId);
