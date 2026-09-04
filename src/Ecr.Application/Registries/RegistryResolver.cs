// src/Ecr.Application/Registries/RegistryResolver.cs
namespace Ecr.Application.Registries;

/// <summary>
/// Темпоральний вибір записів довідника з урахуванням каскаду і фільтра.
/// Виділений окремо, бо потрібен і в UI, і у валідації, і в резолвінгу
/// посилань виразів — три місця з однаковим правилом.
/// </summary>
public sealed class RegistryResolver
{
    /// <summary>Чинні на дату записи з урахуванням батьківського вибору.</summary>
    public IReadOnlyList<long> Resolve(int registryDefId, DateOnly asOf, long? parentEntryId)
        => throw new NotImplementedException(
            "TODO: 1) фільтр IsValidOn(asOf): межі ВКЛЮЧНІ з обох боків;\n" +
            "2) IsActive і не IsDeleted;\n" +
            "3) каскад: якщо задано parentEntryId — лише записи з відповідним " +
            "   зв'язком у RegistryEntryLink;\n" +
            "4) порядок — за Ordinal, потім за Code: стабільний вивід потрібен, " +
            "   щоб список у UI не «стрибав» між запитами.");
}
