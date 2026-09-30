using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;
using NSubstitute;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Пакетні методи підмін сховищ і служби прав — через уже налаштовані
/// поштучні (P8: імпорт книги пише книжковим шляхом <c>PatchCellsHandler</c>).
/// </summary>
/// <remarks>
/// ⚠ Контракт той самий, що в справжніх реалізацій: поштучний метод і є пакетним
/// з одним екземпляром (<c>RowStoreBatchEquivalenceTests</c>,
/// <c>AccessDecisionBatchEquivalenceTests</c>, <c>CellStoreBatchEquivalenceTests</c>).
/// Тому очікування тестів імпорту (порядок записів, винна таблиця) не змінюються:
/// пакетний запис проходить набори в порядку входу, а конфлікт на пакеті з
/// кількох наборів несе <c>tableInstanceId</c>, як і <c>NormalizedCellStore.ApplyBatchAsync</c>.
/// </remarks>
internal static class BatchStoreStubs
{
    public static void DelegateToSingle(IRowStore rows, ICellStore cells, IAccessDecisionService access)
    {
        rows.ResolveTableInstancesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var result = new Dictionary<long, TableInstanceRef>();
                foreach (var id in c.ArgAt<IReadOnlyCollection<long>>(0))
                {
                    result[id] = await rows.ResolveTableInstanceAsync(id, c.ArgAt<CancellationToken>(1));
                }

                return (IReadOnlyDictionary<long, TableInstanceRef>)result;
            });

        rows.GetRowsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var result = new Dictionary<long, IReadOnlyList<RowState>>();
                foreach (var id in c.ArgAt<IReadOnlyList<long>>(0))
                {
                    result[id] = await rows.GetRowsAsync(id, c.ArgAt<PeriodKey>(1), c.ArgAt<CancellationToken>(2));
                }

                return (IReadOnlyDictionary<long, IReadOnlyList<RowState>>)result;
            });

        access.CanEditCellsBatchAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<CellsAccessRequest>>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var result = new Dictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>();
                foreach (var request in c.ArgAt<IReadOnlyCollection<CellsAccessRequest>>(1))
                {
                    result[request.TableInstanceId] = await access.CanEditCellsAsync(
                        c.ArgAt<AccessProfile>(0), request.TableInstanceId, request.PeriodKey, request.Addresses,
                        c.ArgAt<CancellationToken>(2));
                }

                return (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)result;
            });

        access.CanCreateRowsBatchAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyDictionary<long, IReadOnlyCollection<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var result = new Dictionary<long, IReadOnlyDictionary<string, NewRowAccess>>();
                foreach (var (id, keys) in c.ArgAt<IReadOnlyDictionary<long, IReadOnlyCollection<string>>>(1))
                {
                    result[id] = await access.CanCreateRowsAsync(c.ArgAt<AccessProfile>(0), id, keys, c.ArgAt<CancellationToken>(2));
                }

                return (IReadOnlyDictionary<long, IReadOnlyDictionary<string, NewRowAccess>>)result;
            });

        cells.ApplyBatchAsync(Arg.Any<IReadOnlyCollection<CellChangeSet>>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var sets = c.ArgAt<IReadOnlyCollection<CellChangeSet>>(0);
                var result = new Dictionary<long, IReadOnlyDictionary<long, string>>();
                foreach (var set in sets)
                {
                    try
                    {
                        result[set.TableInstanceId] = await cells.ApplyAsync(set, c.ArgAt<CancellationToken>(1));
                    }
                    catch (ConcurrencyConflictException error) when (sets.Count > 1)
                    {
                        var details = new Dictionary<string, object?>(
                            error.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
                        {
                            ["tableInstanceId"] = set.TableInstanceId.ToString(CultureInfo.InvariantCulture),
                        };
                        throw new ConcurrencyConflictException(error.ErrorCode, error.Message, details);
                    }
                }

                return (IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>)result;
            });
    }
}
