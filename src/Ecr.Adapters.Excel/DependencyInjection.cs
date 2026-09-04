using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.Excel;

/// <summary>Реєстрація адаптерів Excel.</summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1 і в заголовку секції `05g`
/// «<c>Ecr.Adapters.Excel/DependencyInjection.cs</c> і
/// <c>Ecr.Adapters.PiAf/DependencyInjection.cs</c>», але блок коду в пакеті є
/// лише для другого (Q-010, Q-015). Метод <c>AddExcelAdapters()</c>
/// викликається з <c>Program.cs</c>, тому без нього не збирається `Ecr.Api`.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Додає експорт, імпорт і їхні допоміжні класи.</summary>
    public static IServiceCollection AddExcelAdapters(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddScoped<IExcelExporter, ExcelExporter>();\n" +
            "AddScoped<IExcelImporter, ExcelImporter>();\n" +
            "AddScoped<ImportDiffBuilder>();\n" +
            "AddScoped<StyleMapper>();\n" +
            "AddSingleton<FormulaTranslator>();\n" +
            "⚠ Імпорт реєструється ЛИШЕ разом із ImportDiffBuilder: імпорт без " +
            "попереднього перегляду diff заборонений (ФВ-4.3).");
}
