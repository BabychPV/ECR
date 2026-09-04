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
    public static IServiceCollection AddEcrApplication(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO: зареєструвати обробники з Templates/, Documents/, Periods/, Workflow/, " +
            "Registries/, Units/, Calculations/, Security/, Localization/ — усі як Scoped; " +
            "ValidationEngine і RecalculationService — Scoped. " +
            "⚠ Реєструвати поіменно, а не скануванням збірки: Assembly.Load і " +
            "Activator.CreateInstance за рядком заборонені архітектурним правилом 8 (tz/03 §3.3).");
}
