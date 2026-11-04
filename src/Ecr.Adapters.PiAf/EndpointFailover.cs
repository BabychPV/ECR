using System.Collections.Concurrent;
using Ecr.Domain.Entities.External;
using Microsoft.Extensions.Logging;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Перемикання на запасну адресу джерела (<c>ext.DataSource.SecondaryEndpoint</c>) при відмові ЗВ'ЯЗКУ з
/// основною (D-462).
/// </summary>
/// <param name="log">Журнал: перемикання — попередження з кодом джерела і роллю адреси, без самої адреси.</param>
/// <param name="transport">Назва транспорту для журналу.</param>
/// <remarks>
/// ⛔ Перемикає ЛИШЕ відмова зв'язку (класифікацію дає адаптер: мережа, тайм-аут, «не з'єднується»). Відмова
/// в автентифікації (<c>H-20</c>) і помилка конфігурації (зіпсований рядок з'єднання, адресу відхилено
/// політикою) — ні: та сама облікова чи та сама помилка налаштування на запасній адресі дала б ту саму
/// відповідь, а перемикання сховало б справжню причину.
/// <para>
/// ⚠ Липке на життя екземпляра (scope задачі): після відмови основної наступні звернення того самого прогону
/// йдуть одразу на запасну — інакше кожен запит платив би повну серію повторів до мертвої адреси. Наступний
/// прогін (новий scope) знову починає з основної.
/// </para>
/// <para>
/// ⚠ Обидві адреси проходять ту саму політику адреси (AllowedHosts, link-local/metadata) — і при збереженні
/// джерела (<c>DataSourceHandlers</c>), і перед з'єднанням (адаптер перевіряє кожну адресу, якою ходить).
/// </para>
/// </remarks>
internal sealed partial class EndpointFailover(ILogger log, string transport)
{
    /// <summary>Джерела, для яких цей екземпляр уже перейшов на запасну адресу.</summary>
    private readonly ConcurrentDictionary<int, bool> onSecondary = new();

    /// <summary>
    /// ✎ Аудит 31.10 (G5): чи цей екземпляр уже перейшов на запасну адресу джерела — тоді й наступні звернення
    /// прогону обслуговує вона (перемикання липке). З'єднання, відкрите ще на основній, дає хибне «так» — лише в
    /// безпечний бік (видалення зниклих точок пропускається).
    /// </summary>
    /// <param name="sourceId">Джерело.</param>
    public bool IsOnSecondary(int sourceId) => onSecondary.ContainsKey(sourceId);

    /// <summary>Роль адреси, яка обслужила останнє звернення до джерела; для журналу й тестів.</summary>
    public string? LastServedBy { get; private set; }

    /// <summary>Виконує звернення до основної адреси, а при відмові зв'язку — до запасної.</summary>
    /// <typeparam name="T">Результат звернення.</typeparam>
    /// <param name="source">Джерело.</param>
    /// <param name="operation">Звернення за адресою.</param>
    /// <param name="isConnectionFailure">Чи це відмова саме зв'язку (а не автентифікації чи конфігурації).</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Результат звернення.</returns>
    public async Task<T> RunAsync<T>(
        DataSource source,
        Func<string, CancellationToken, Task<T>> operation,
        Func<Exception, bool> isConnectionFailure,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isConnectionFailure);

        var secondary = string.IsNullOrWhiteSpace(source.SecondaryEndpoint) ? null : source.SecondaryEndpoint.Trim();

        if (secondary is null || !onSecondary.ContainsKey(source.Id))
        {
            try
            {
                var primary = await operation(source.Endpoint, ct).ConfigureAwait(false);
                LastServedBy = Primary;
                return primary;
            }
            catch (Exception ex) when (secondary is not null && !ct.IsCancellationRequested && isConnectionFailure(ex))
            {
                onSecondary[source.Id] = true;
                LogFailover(log, transport, source.Code, ex.GetType().Name);
            }
        }

        var result = await operation(secondary!, ct).ConfigureAwait(false);
        LastServedBy = Secondary;
        return result;
    }

    /// <summary>Роль основної адреси в журналі.</summary>
    public const string Primary = "primary";

    /// <summary>Роль запасної адреси в журналі.</summary>
    public const string Secondary = "secondary";

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Transport}: основна адреса джерела {SourceCode} не відповідає ({Failure}); до кінця прогону джерело обслуговує ЗАПАСНА адреса (SecondaryEndpoint, D-462).")]
    private static partial void LogFailover(ILogger logger, string transport, string sourceCode, string failure);
}
