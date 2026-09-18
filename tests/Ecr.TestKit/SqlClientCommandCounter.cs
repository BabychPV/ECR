using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Ecr.TestKit;

/// <summary>
/// Лічильник УСІХ команд <c>Microsoft.Data.SqlClient</c> у процесі — і тих, що
/// випустив EF, і сирих.
/// </summary>
/// <remarks>
/// ⛔ Причина існування — в <see cref="DbCommandCounter"/>: гарячий шлях запису
/// (<c>NormalizedCellStore</c>) не проходить через EF, тому перехоплювач EF
/// його не бачить. Храповики <c>WR-04</c>/<c>RD-03</c> без цього класу
/// рахували б периферію, а не <c>MERGE doc.CellValue</c>.
///
/// ⚠ Обсяг — увесь процес, а не один <c>DbContext</c>. Це і сила (нічого не
/// губиться), і обмеження: у тесті, що ділить процес із іншими, числа
/// змішаються. Тому <see cref="CommandTally.Reset"/> викликається
/// безпосередньо перед виміряною дією, а сам замір не паралелиться з чужою
/// роботою по базі.
///
/// ⛔ Механізм — <c>DiagnosticListener</c> провайдера, а не наш код. Отже він
/// може ТИХО перестати працювати, якщо провайдер перейменує подію або змінить
/// форму корисного навантаження. Мовчазний нуль тут — найгірший результат:
/// «звернень 0» прочиталося б як «оптимізація вдалася». Тому клас рахує
/// <see cref="UnreadablePayloads"/> і має <see cref="AssertObserved"/>, яку
/// слід кликати в кінці кожного заміру.
/// </remarks>
public sealed class SqlClientCommandCounter : IObserver<DiagnosticListener>, IDisposable
{
    /// <summary>Ім'я слухача провайдера.</summary>
    private const string ListenerName = "SqlClientDiagnosticListener";

    /// <summary>Подія «команда ось-ось виконається».</summary>
    /// <remarks>
    /// ⚠ Саме <c>Before</c>, а не <c>After</c>: <c>After</c> не настає для
    /// команди, що впала (там окрема <c>WriteCommandError</c>), і замір під
    /// навантаженням, де частина запитів дає взаємне блокування, занизив би
    /// число звернень рівно на кількість збоїв.
    /// </remarks>
    private const string CommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";

    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;
    private int _unreadable;

    /// <summary>Підрахунок.</summary>
    public CommandTally Tally { get; }

    /// <summary>
    /// Скільки подій прийшло, але команду з них дістати не вдалося.
    /// </summary>
    /// <remarks>
    /// Не нуль означає, що форма події змінилася і лічильник треба лагодити, а
    /// не що навантаження було меншим.
    /// </remarks>
    public int UnreadablePayloads => Volatile.Read(ref _unreadable);

    /// <summary>Підписується на діагностику провайдера негайно.</summary>
    public SqlClientCommandCounter()
        : this(new CommandTally())
    {
    }

    /// <summary>Підписується і пише у спільний підрахунок.</summary>
    /// <param name="tally">Куди складати.</param>
    public SqlClientCommandCounter(CommandTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        Tally = tally;
        Tally.Declare("sqlclient-diagnostics");
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <inheritdoc/>
    public void OnNext(DiagnosticListener value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(value.Name, ListenerName, StringComparison.Ordinal))
        {
            lock (_subscriptions)
            {
                _subscriptions.Add(value.Subscribe(new EventSink(this)));
            }
        }
    }

    /// <inheritdoc/>
    public void OnCompleted()
    {
    }

    /// <inheritdoc/>
    public void OnError(Exception error)
    {
    }

    /// <summary>
    /// Кидає, якщо лічильник нічого не побачив або не зміг прочитати події.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Лічильник мовчазно зламався — числа заміру недійсні.
    /// </exception>
    /// <remarks>
    /// ⛔ Це і є захист від хибнозеленого храповика. Перевірка «звернень ≤ 10»
    /// на зламаному лічильнику проходить завжди; перевірка «звернень ≤ 10 І
    /// лічильник щось бачив» — ні.
    /// </remarks>
    public void AssertObserved()
    {
        var snapshot = Tally.Snapshot();

        if (UnreadablePayloads > 0)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "SqlClientCommandCounter: {0} подій, з яких не вдалося дістати команду. "
                + "Форма '{1}' змінилася — числа заміру НЕДІЙСНІ.",
                UnreadablePayloads,
                CommandBefore));
        }

        if (snapshot.Total == 0)
        {
            throw new InvalidOperationException(
                "SqlClientCommandCounter не побачив жодної команди. Або дія до бази не ходила, "
                + "або підписка на " + ListenerName + " не спрацювала. Нуль звернень не є доказом.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        _allListeners.Dispose();
    }

    /// <summary>
    /// Дістає команду з корисного навантаження події.
    /// </summary>
    /// <param name="payload">Об'єкт події.</param>
    /// <remarks>
    /// ⚠ Через рефлексію, бо тип навантаження в провайдері <c>internal</c> і
    /// мінявся між версіями (анонімний тип → іменована структура). Рефлексія
    /// по імені властивості <c>Command</c> пережила обидві форми; якщо не
    /// переживе третьої — це буде видно в <see cref="UnreadablePayloads"/>, а
    /// не тихо.
    /// </remarks>
    private void Observe(object? payload)
    {
        if (payload is null)
        {
            Interlocked.Increment(ref _unreadable);
            return;
        }

        var property = payload.GetType().GetProperty("Command", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(payload) is not DbCommand command)
        {
            Interlocked.Increment(ref _unreadable);
            return;
        }

        Tally.Add(command.CommandText, command.Transaction is not null, command.Parameters.Count);
    }

    private sealed class EventSink(SqlClientCommandCounter owner) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (string.Equals(value.Key, CommandBefore, StringComparison.Ordinal))
            {
                owner.Observe(value.Value);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
