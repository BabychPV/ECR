using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// S18: перемикання набору довідників (<c>EntityId = 0</c>) для історії ОДНОГО довідника фільтруються за
/// кодом у запиті — до стелі.
/// </summary>
/// <remarks>
/// ⛔ Обробник історії брав «останні 100 перемикань» і фільтрував їх у пам'яті: свої перемикання зникали,
/// щойно за ними набиралось сто чужих. Перевіряється проти справжньої СУБД — <c>OPENJSON</c> і <c>TOP</c>
/// підміна не доведе.
/// <para>
/// Мутаційні докази: прибрати умову <c>EXISTS (… OPENJSON …)</c> → <see cref="Фільтр_за_кодом_іде_до_стелі"/>
/// червоний (повертаються два новіші чужі); замінити <c>j.value = @code</c> на <c>LIKE '%' + @code + '%'</c> →
/// червоний на коді-підрядку.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderRegistrySetSwitchTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_за_кодом_іде_до_стелі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await builder.BuildAsync(ct: CancellationToken.None);

        // Унікальні коди: журнал спільний для всіх тестів колекції.
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var own = "W" + suffix;
        var longer = own + "_BODY";
        var other = "X" + suffix;

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        // Своє — НАЙСТАРІШЕ; новіші за нього — чужі, код-підрядок і запис старішого формату.
        await writer.WriteStructureChangeAsync(Switch(At, own, other, "своє"), CancellationToken.None);
        await writer.WriteStructureChangeAsync(Switch(At.AddMinutes(1), other, null, "чуже-1"), CancellationToken.None);
        await writer.WriteStructureChangeAsync(Switch(At.AddMinutes(2), longer, null, "підрядок"), CancellationToken.None);
        await writer.WriteStructureChangeAsync(
            new StructureChangeRecord(
                At.AddMinutes(3), 0, "cfg.RegistryDef", 0, ChangeClass.Guarded, "SwitchSourceSet",
                null, "not json " + own, "старий формат", 1, null),
            CancellationToken.None);

        var rows = await new AuditReader(db).ReadRegistrySetSwitchesAsync(own, 2, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("своє", row.ChangeReason);
        Assert.Equal(0, row.EntityId);
    }

    private static StructureChangeRecord Switch(DateTime at, string code, string? neighbour, string reason)
    {
        var codes = neighbour is null ? $"\"{code}\"" : $"\"{code}\",\"{neighbour}\"";
        return new StructureChangeRecord(
            at, 0, "cfg.RegistryDef", 0, ChangeClass.Guarded, "SwitchSourceSet",
            null, $$"""{"sourceKind":"External","registryCodes":[{{codes}}]}""", reason, 1, null);
    }
}
