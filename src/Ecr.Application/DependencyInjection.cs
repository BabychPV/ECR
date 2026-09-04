using Ecr.Application.Documents;
using Ecr.Application.Templates;
using Ecr.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Application;

/// <summary>Реєстрація use-cases застосунку в контейнері.</summary>
/// <remarks>
/// ⚠ Файла немає ні в дереві `05-skeleton.md` §1, ні в `05c`, але
/// <c>Program.cs</c> (`05h`) викликає <c>AddEcrApplication()</c> — без нього
/// не збирається `Ecr.Api` (Q-015).
///
/// Тут реєструються <b>лише</b> обробники і доменні служби застосунку.
/// Реалізацій портів тут немає і бути не може: вони живуть в
/// <c>Ecr.Infrastructure</c>, і саме ця межа робить застосунок тестованим
/// без бази (`05-skeleton.md` §4).
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Додає обробники use-cases, валідацію і перерахунок.</summary>
    /// <remarks>
    /// ⚠ Реєстрація <b>поіменна</b>, а не скануванням збірки:
    /// <c>Assembly.Load</c> і <c>Activator.CreateInstance</c> за рядком
    /// заборонені архітектурним правилом 8 (tz/03 §3.3). Ціна — цей список
    /// треба доповнювати руками; вигода — список видно, і забутий обробник
    /// падає при старті, а не на першому запиті користувача.
    ///
    /// ⚠ Чого тут ПОКИ немає і чому: <c>ValidationEngine</c>,
    /// <c>RecalculationService</c>, <c>PatchCellsHandler</c>,
    /// <c>ValidateDocumentHandler</c> і <c>PublishTemplateVersionHandler</c>
    /// прямо чи через <c>ValidationEngine</c> залежать від
    /// <c>IFormulaEngine</c>, а рушій виразів — це Етап 2. Зареєструвати їх
    /// «на майбутнє» неможливо: у Development контейнер перевіряється при
    /// побудові, і застосунок не стартував би взагалі (`Q-051`).
    /// </remarks>
    public static IServiceCollection AddEcrApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Метадані шаблонів (модуль 1.7)
        services.AddScoped<CreateTemplateVersionHandler>();
        services.AddScoped<CloneTemplateVersionHandler>();
        services.AddScoped<GetTemplateStructureHandler>();
        services.AddScoped<DiffTemplateVersionsHandler>();
        services.AddScoped<PatchPresentationHandler>();

        // Документи і комірки (модуль 1.8)
        services.AddScoped<CreateDocumentHandler>();
        services.AddScoped<GetTableSliceHandler>();
        services.AddScoped<CreateRowHandler>();

        // Доменні служби без стану
        services.AddSingleton<ChangeClassifier>();

        return services;
    }
}
