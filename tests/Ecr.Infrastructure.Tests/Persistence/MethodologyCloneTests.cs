// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyCloneTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Клон версії методології на **реальному** SQL Server (`ФВ-9.1`).
/// </summary>
/// <remarks>
/// ⛔ Перевірка саме на базі, а не на фейку сховища. Дочірні записи
/// посилаються на версію ЧИСЛОМ, тож їхній зовнішній ключ проставляється лише
/// після того, як база призначила ключ чернетці. Фейк, який роздає ключі сам,
/// показав би зелене на коді, що в базі кладе всі копії на версію
/// <c>Id = 0</c>, — тобто нікуди.
///
/// ⚠ Сусідній сторож (<c>MethodologyCloneCompletenessTests</c>) питає, чи
/// ЗГАДАНО кожен набір; цей — чи справді він доїхав, і з якими значеннями.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCloneTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.1")]
    public async Task Клон_переносить_увесь_вміст_опублікованої_версії()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];

        int sourceId;
        int unitId;
        int libraryId;

        await using (var seed = builder.CreateContext())
        {
            unitId = await seed.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            var library = new Methodology(EcrCode.Create($"LIB_{tag}"), Name("library"));
            var methodology = new Methodology(EcrCode.Create($"ITEST_{tag}"), Name("clone probe"));
            seed.Methodologies.AddRange(library, methodology);
            await seed.SaveChangesAsync();

            libraryId = library.Id;

            var source = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);

            // ⛔ Режими джерела — не Legacy/Actual: саме на них ловиться клон,
            // який бере значення з конструктора замість того, щоб перенести їх
            // (ФВ-9.9, ФВ-16.11).
            source.SetModes(NumericMode.Strict, CalendarMode.Fixed365, TraceLevel.Full);

            seed.MethodologyVersions.Add(source);
            await seed.SaveChangesAsync();

            sourceId = source.Id;

            var numeric = source.AddFormula(
                EcrCode.Create("gsec"), "@Flow * CST.k1", FormulaResultType.Number, unitId);
            var text = source.AddFormula(
                EcrCode.Create("verdict"), "'Сверхнорматив'", FormulaResultType.Text, null);

            var constant = new MethodologyConstant(
                sourceId, EcrCode.Create("k1"), 0.8500000000m, unitId);
            constant.SetScope("offshore", null);
            constant.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
            constant.SetSource("Наказ 137");

            var unresolved = MethodologyConstant.FromImport(
                sourceId, EcrCode.Create("n_broken"), "-", ConstantKind.Numeric, unitId);

            var rule = new MethodologyRule(sourceId, EcrCode.Create("winter"), """{"k":1}""", 5);
            rule.SetActive(false);

            seed.MethodologyFormulas.AddRange(numeric, text);
            seed.MethodologyConstants.AddRange(constant, unresolved);
            seed.MethodologyRules.Add(rule);
            seed.MethodologyImports.Add(new MethodologyImport(sourceId, libraryId, methodology.Id));
            seed.MethodologyOutputs.Add(
                new MethodologyOutput(sourceId, EcrCode.Create("gsec"), unitId, 1));
            seed.MethodologyTestCases.Add(new MethodologyTestCaseEntity(
                sourceId, "case1", """{"periodKey":202601}""", """{"gsec":12.5}""", 0.01m));

            await seed.SaveChangesAsync();

            // Публікуємо ЧЕРЕЗ агрегат: клонують саме опубліковану версію, і
            // тест має відтворити той стан, у якому клон і потрібен.
            var aggregate = await seed.Methodologies
                .Include(m => m.Versions)
                .FirstAsync(m => m.Id == methodology.Id);

            aggregate.PublishVersion(
                aggregate.Versions.Single(v => v.Id == sourceId),
                publishedByUserId: 2,
                changeReason: "Первинна публікація",
                effectiveFrom: new DateOnly(2026, 1, 1),
                testsPassed: true,
                Now);

            await seed.SaveChangesAsync();
        }

        int draftId;

        await using (var write = builder.CreateContext())
        {
            var source = await write.MethodologyVersions.FirstAsync(v => v.Id == sourceId);
            var aggregate = await write.Methodologies
                .Include(m => m.Versions)
                .FirstAsync(m => m.Id == source.MethodologyId);

            var draft = aggregate.Versions.Single(v => v.Id == sourceId).CloneAsDraft("2.0", 1, Now);
            aggregate.AddVersion(draft);

            draftId = await new MethodologyDraftStore(write).SaveDraftAsync(draft, sourceId, default);
        }

        await using var read = builder.CreateContext();

        var clone = await read.MethodologyVersions.AsNoTracking().FirstAsync(v => v.Id == draftId);

        Assert.Equal(TemplateVersionStatus.Draft, clone.Status);
        Assert.Equal(NumericMode.Strict, clone.NumericMode);
        Assert.Equal(CalendarMode.Fixed365, clone.CalendarMode);
        Assert.Null(clone.EffectiveFrom);

        var formulas = await read.MethodologyFormulas.AsNoTracking()
            .Where(f => f.MethodologyVersionId == draftId).OrderBy(f => f.Code).ToListAsync();

        Assert.Equal(2, formulas.Count);
        Assert.Equal("@Flow * CST.k1", formulas[0].Expression);
        Assert.Equal(unitId, formulas[0].OutputUnitId);
        Assert.Equal(FormulaResultType.Text, formulas[1].ResultType);
        Assert.Null(formulas[1].OutputUnitId);

        var constants = await read.MethodologyConstants.AsNoTracking()
            .Where(c => c.MethodologyVersionId == draftId).OrderBy(c => c.Code).ToListAsync();

        Assert.Equal(2, constants.Count);

        // ⛔ Значення коефіцієнта — те, за що система відповідає перед
        // регулятором. Копія через рядок виглядала б рівноцінною і поставила б
        // числа звіту в залежність від форматування.
        Assert.Equal(0.8500000000m, constants[0].Value);
        Assert.Equal("offshore", constants[0].Category);
        Assert.Equal(new DateOnly(2026, 12, 31), constants[0].ValidTo);
        Assert.Equal("Наказ 137", constants[0].Source);

        // ⚠ Нерозібраний рядок лишається нерозібраним: він мусить доїхати до
        // публікації, щоб людина ухвалила рішення, а не стати тихим нулем.
        Assert.Null(constants[1].Value);
        Assert.Equal("-", constants[1].TextValue);

        var rules = await read.MethodologyRules.AsNoTracking()
            .Where(r => r.MethodologyVersionId == draftId).ToListAsync();

        // ⛔ Вимкнене правило лишається вимкненим: клон, який його вмикає, тихо
        // змінює те, ЩО ВЗАГАЛІ рахується (ФВ-13.4).
        Assert.False(Assert.Single(rules).IsActive);
        Assert.Equal(5, rules[0].Priority);

        Assert.Equal(
            libraryId,
            Assert.Single(await read.MethodologyImports.AsNoTracking()
                .Where(i => i.MethodologyVersionId == draftId).ToListAsync())
                .ImportedMethodologyId);

        Assert.Single(await read.MethodologyOutputs.AsNoTracking()
            .Where(o => o.MethodologyVersionId == draftId).ToListAsync());

        // ⛔ Без тестів клон неможливо опублікувати взагалі (ФВ-9.12) — тобто
        // «майже повний» клон означав би, що методологію більше не змінити.
        Assert.Equal(0.01m, Assert.Single(await read.MethodologyTestCases.AsNoTracking()
            .Where(t => t.MethodologyVersionId == draftId).ToListAsync()).Tolerance);

        // ⚠ Джерело не постраждало: клонування не має бути правкою того, з чого
        // клонує.
        Assert.Equal(
            2,
            await read.MethodologyFormulas.AsNoTracking()
                .CountAsync(f => f.MethodologyVersionId == sourceId));

        // ⛔ І головне, заради чого порт заведено окремо (`Q-100`): перелік
        // конфігуратора віддає ЧЕРНЕТКУ. Доти її не показував жоден маршрут —
        // `GetPublishedVersionsAsync` фільтрує за `Status = Published`, — тож
        // редагувати було не лише нічим, а й нічого.
        var listed = await new MethodologyDraftStore(read)
            .GetAllVersionsAsync(clone.MethodologyId, default);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, v => v.Id == draftId && v.Status == TemplateVersionStatus.Draft);
        Assert.Contains(listed, v => v.Id == sourceId && v.IsPublished);

        // Той самий факт очима клієнта: правити можна лише чернетку.
        Assert.True(Ecr.Application.Calculations.ListMethodologyVersionsHandler
            .Map(listed.Single(v => v.Id == draftId)).IsEditable);
        Assert.False(Ecr.Application.Calculations.ListMethodologyVersionsHandler
            .Map(listed.Single(v => v.Id == sourceId)).IsEditable);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = value });
}
