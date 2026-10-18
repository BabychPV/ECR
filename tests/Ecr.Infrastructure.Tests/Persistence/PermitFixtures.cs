// tests/Ecr.Infrastructure.Tests/Persistence/PermitFixtures.cs
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>Назва лише англійською.</summary>
    /// <param name="en">Текст.</param>
    public static Dictionary<string, string> Name(string en) => new() { ["en"] = en };

    private static PermitTypeTableDto Table(string code, IReadOnlyList<PermitTypeColumnDto> columns)
        => new(code, Name(code), 1, columns, []);

    private static PermitTypeColumnDto Col(string code, string dataType, bool key, bool required = true)
        => new(code, Name(code), dataType, null, required || key, key, null, null, null, null, null, null, null, 1);
}
