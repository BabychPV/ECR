// tests/Ecr.Infrastructure.Tests/Persistence/PermitFixtures.cs
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Опублікований тип дозволу для тестів екземплярів (Permits, PM-04).</summary>
internal static class PermitFixtures
{
    /// <summary>
    /// Публікує тип із двома таблицями: <c>Limits</c> (ключ <c>Name</c>, обов'язкове число <c>Annual</c>, примітка) і
    /// <c>Places</c> (ключ <c>Name</c>).
    /// </summary>
    /// <param name="h">Обробники з правами на повний цикл типу.</param>
    /// <param name="typeCode">Код типу.</param>
    public static async Task<PermitTypeDto> PublishTypeAsync(PermitHarness h, string typeCode)
    {
        ArgumentNullException.ThrowIfNull(h);

        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(typeCode, Name(typeCode), "Air"), default);
        var limits = Table("Limits", [Col("Name", "String", key: true), Col("Annual", "Decimal", key: false), Col("Note", "String", key: false, required: false)]);
        var places = Table("Places", [Col("Name", "String", key: true)]);

        await using var put = await h.NextRequestAsync();
        var draft = await put.Replace.HandleAsync(typeCode, new ReplacePermitTypeRequest(created.RowVersion, Name(typeCode), "Air", [limits, places], []), default);

        await using var publish = await h.NextRequestAsync();
        return await publish.Publish.HandleAsync(typeCode, new PublishPermitTypeRequest("публікація типу для тестів", draft.RowVersion), default);
    }

    /// <summary>Кількість живих записів реєстру.</summary>
    /// <param name="h">Обробники.</param>
    /// <param name="registryDefId">Реєстр.</param>
    public static Task<int> LiveEntriesAsync(PermitHarness h, int registryDefId)
        => h.Db.RegistryEntries.AsNoTracking().CountAsync(e => e.RegistryDefId == registryDefId && !e.IsDeleted);

    /// <summary>Реєстр таблиці типу за кодом таблиці.</summary>
    /// <param name="h">Обробники.</param>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="tableCode">Код таблиці.</param>
    public static async Task<RegistryDef> TableDefAsync(PermitHarness h, string typeCode, string tableCode)
    {
        var type = (await h.Permits.FindByCodeAsync(typeCode, default))!;
        var table = (await h.Permits.ListTablesAsync(type.Id, default)).Single(t => t.Code == tableCode);
        return (await h.Registries.FindDefinitionByIdAsync(table.RegistryDefId, default))!;
    }

    /// <summary>Додає рядок у таблицю дозволу (наявним писачем реєстрів, як це робить клієнт через entries/batch).</summary>
    /// <param name="h">Обробники.</param>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="permitId">Дозвіл-батько.</param>
    /// <param name="name">Значення ключового стовпця <c>Name</c>.</param>
    /// <param name="annual">Значення <c>Annual</c> (лише таблиця <c>Limits</c>).</param>
    public static async Task<long> AddRowAsync(PermitHarness h, string typeCode, string tableCode, long permitId, string name, decimal? annual = null)
    {
        var table = await TableDefAsync(h, typeCode, tableCode);
        var values = new Dictionary<string, object?> { ["PERMIT"] = permitId, ["Name"] = name };
        if (annual is { } value)
        {
            values["Annual"] = value;
        }

        var result = await h.Writer.WriteAsync(
            new Ecr.Application.Registries.RegistryEntryWriteBatch(
                table.Id, [new Ecr.Application.Registries.RegistryEntryWrite($"R{Guid.NewGuid():N}"[..12], values)]),
            default);
        Assert.True(result.Applied, string.Join(";", result.Errors.Select(e => e.MessageKey)));
        return result.Rows.Single().Entry.Id;
    }

    /// <summary>Вмикає запис шапки напряму писачем реєстру (стан <c>Active</c> без обробника активації).</summary>
    /// <param name="h">Обробники.</param>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="window">Нове вікно чинності; <c>null</c> - не міняти.</param>
    public static async Task ActivateDirectAsync(PermitHarness h, string typeCode, long permitId, Ecr.Domain.ValueObjects.ValidityWindow? window = null)
    {
        var type = (await h.Permits.FindByCodeAsync(typeCode, default))!;
        var result = await h.Writer.UpdateAsync(
            new Ecr.Application.Registries.RegistryEntryUpdateBatch(
                type.HeaderRegistryDefId,
                [new Ecr.Application.Registries.RegistryEntryUpdate(permitId, new Dictionary<string, object?>()) { IsActive = true, Validity = window }]),
            default);
        Assert.True(result.Applied, string.Join(";", result.Errors.Select(e => e.MessageKey)));
    }

    /// <summary>Назва лише англійською.</summary>
    /// <param name="en">Текст.</param>
    public static Dictionary<string, string> Name(string en) => new() { ["en"] = en };

    private static PermitTypeTableDto Table(string code, IReadOnlyList<PermitTypeColumnDto> columns)
        => new(code, Name(code), 1, columns, []);

    private static PermitTypeColumnDto Col(string code, string dataType, bool key, bool required = true)
        => new(code, Name(code), dataType, null, required || key, key, null, null, null, null, null, null, null, 1);
}
