using System.Data.Odbc;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI SQL Client (RTQP) — ODBC.
/// </summary>
/// <remarks>
/// Підтверджені факти: RTQP **у продуктиві** (63 land-процедури читають через
/// нього), він **тільки для читання**, **не потребує Kerberos-делегування** і
/// працює через ODBC, не OLE DB (`D-46`). Це основний транспорт для масового
/// читання історії й довідників.
/// </remarks>
public sealed class PiSqlClientDataSource : IExternalDataSource
{
    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiSqlClient;

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прочитати каталог елементів і атрибутів AF через RTQP; повернути дескриптори " +
            "з іменами, типами і UOM. Каталог потрібен конфігуратору, щоб користувач обирав " +
            "зі списку, а не вводив імена руками (ФВ-13.6).");

    /// <inheritdoc />
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: побудувати запит із фільтром по часу; читати через OdbcDataReader ПОТОКОВО, " +
            "не матеріалізуючи все в пам'ять; підключення під СЕРВІСНИМ обліковим записом " +
            "(D-34); секрет брати за іменем із SecretName, ніколи не з конфігу. " +
            "⚠ Адаптер не створює жодних артефактів у чужій БД — жодних вʼюх і таблиць " +
            "в AF-базі: вони не їдуть із застосунком і стають джерелом поломок при " +
            "переїзді середовища (ER-I-01).");
}
