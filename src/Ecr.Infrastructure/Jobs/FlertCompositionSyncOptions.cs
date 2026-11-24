// src/Ecr.Infrastructure/Jobs/FlertCompositionSyncOptions.cs
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Налаштування разового імпорту складу газу FLERT → ECR (<c>D-476</c> F2; D-NEW: імпорт, не щоденний синк).</summary>
/// <param name="Enabled">Чи застосовувати знімок (рушій; задача імпорту завжди передає <c>true</c> — це явна дія людини).</param>
/// <param name="DataSourceCode">Код джерела <c>ext.DataSource</c> (транспорт <c>Sql</c>) — з'єднання з FLERT.</param>
/// <param name="MaxDeactivatePercent">Частка чинних кейсів (0–100 %), яку один прогін може вимкнути (<see cref="DeactivationLimit"/>).</param>
/// <remarks>
/// ⚠ Ключі — ЛІТЕРАЛАМИ (сторож <c>ConfigurationKeysTests</c> шукає повний рядок ключа).
/// <para>
/// ⛔ D-NEW: ключа «увімкнено» і розкладу більше немає — FLERT читається один раз, кнопкою на сторінці «Склад газу», і лише
/// в порожню базу. З'єднання — джерело <c>ext.DataSource</c> з кодом <see cref="DataSourceCodeKey"/>; запит
/// <c>Sql:&lt;код&gt;:CompositionQuery</c> — у каналі секретів (<c>Secrets:Sql:…</c>), типового тексту немає.
/// </para>
/// </remarks>
public sealed record FlertCompositionSyncOptions(
    bool Enabled,
    string DataSourceCode,
    int MaxDeactivatePercent = FlertCompositionSyncOptions.DefaultMaxDeactivatePercent)
{
    /// <summary>
    /// Ключ: яку частку (0–100 %) ЧИННИХ кейсів дзеркала один прогін може вимкнути. Більше — знімок вважається
    /// неповним (частковий результат FLERT), і прогін не пише НІЧОГО (<c>Degraded</c>, <c>deactivationLimit</c>).
    /// </summary>
    public const string MaxDeactivatePercentKey = "Flert:Sync:MaxDeactivatePercent";

    /// <summary>Дефолт частки вимкнення: 10 %.</summary>
    public const int DefaultMaxDeactivatePercent = 10;

    /// <summary>
    /// Нижня межа в штуках: до стількох кейсів вимикається завжди, хоч би яким малим було дзеркало (інакше на
    /// дзеркалі з 20 кейсів 10 % не дозволили б прибрати й двох).
    /// </summary>
    public const int DeactivateFloor = 5;

    /// <summary>Скільки кейсів дозволено вимкнути за прогін при <paramref name="activeCases"/> чинних.</summary>
    /// <param name="activeCases">Живих увімкнених кейсів у дзеркалі до прогону.</param>
    public int DeactivationLimit(int activeCases)
        => Math.Max(DeactivateFloor, (int)((long)activeCases * Math.Clamp(MaxDeactivatePercent, 0, 100) / 100));

    /// <summary>Ключ: код джерела FLERT.</summary>
    public const string DataSourceCodeKey = "Flert:Sync:DataSourceCode";

    /// <summary>Дефолт коду джерела.</summary>
    public const string DefaultDataSourceCode = "FLERT";

    /// <summary>
    /// Стеля рядків знімка. На копії 11.11 їх 982; більше — знімок неповний, і синк його НЕ застосовує
    /// (видалити «зниклі» за обрізаним знімком означало б втратити дані).
    /// </summary>
    public const int MaxRows = 100_000;

    /// <summary>Читає налаштування; порожнє значення — дефолт.</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static FlertCompositionSyncOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new FlertCompositionSyncOptions(
            Enabled: true,
            DataSourceCode: configuration[DataSourceCodeKey] is { Length: > 0 } code && !string.IsNullOrWhiteSpace(code)
                ? code.Trim()
                : DefaultDataSourceCode,
            MaxDeactivatePercent: int.TryParse(
                configuration[MaxDeactivatePercentKey],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percent)
                ? Math.Clamp(percent, 0, 100)
                : DefaultMaxDeactivatePercent);
    }
}
