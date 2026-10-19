// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.RowMode.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Режим рядків таблиці змінили ПІСЛЯ прив'язки (Permits, PM-09/PM-10, аудит B22): <c>Dynamic</c> → <c>Fixed</c>.
/// </summary>
/// <remarks>
/// Фіксує поведінку, а не лише спосіб її уникнути: правило «рядки з дозволу лише в Dynamic/Mixed» перевіряється
/// ПРИ ЗБЕРЕЖЕННІ прив'язки; рантайм режиму не питає (рядки з дозволу - машинні, а не додані користувачем).
/// ⚠ Редактор шаблону такий стан більше не створює: <c>SaveTableDefHandler</c> відмовляє 409 <c>ECR-PRM-4102</c> (PM-09b,
/// <c>PermitSheetGuardTests</c>). Тут режим змінено прямим SQL - так моделюється дані, збережені до охоронця, або імпорт повз
/// обробник; лишається видимим повторне збереження прив'язки - 422.
/// </remarks>
public sealed partial class PermitRuntimeRowsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_що_стала_Fixed_після_прив_язки_все_одно_отримує_рядки_з_дозволу_а_повторне_збереження_прив_язки_дає_422()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        await scene.ExecuteAsync($"UPDATE cfg.TableDef SET RowMode = 0 WHERE Id = {scene.Sheet.Doc.TableDefId}");

        // Рантайм не питає режим: рядки з дозволу - не рядки, додані користувачем.
        await using var db = scene.NewContext();
        await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        Assert.Equal(scene.Permit.RowEntryIds.Select(id => $"P{id}"), (await scene.RowsAsync()).Select(r => r.RowKey));

        // Але редактор шаблону при повторному збереженні прив'язки бачить відмову з назвою таблиці.
        var doc = scene.Sheet.Doc;
        await using var resave = await scene.H.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => resave.Save.HandleAsync(
            doc.TemplateVersionId,
            $"B{_tag}",
            new SavePermitBindingRequest(
                doc.TableDefId, Type, "Emissions", "Rows", "ByFacilityPeriodEnd", null, true,
                [
                    new PermitBindingColumnRequest("RowKey", "Substance", doc.ColumnDefIds[0]),
                    new PermitBindingColumnRequest("Limit", "Limit", doc.ColumnDefIds[1]),
                    new PermitBindingColumnRequest("Info", "Note", scene.Sheet.NoteColumnId),
                ]),
            default));

        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        Assert.Contains("bindingRowsNeedDynamic", ((string)error.Details!["caseKeys"]!).Split(','));
    }
}
