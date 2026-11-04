using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Зліпок значень документа за період (і шапки) для зрізу подання — ОДНЕ джерело для подання
/// (<see cref="SubmitSheetHandler"/>) і для повторного зрізу при затвердженні після правки погоджувача
/// (<see cref="ApprovalSnapshotRefresh"/>, D-442).
/// </summary>
/// <remarks>
/// ⛔ Дві копії побудови payload розійшлися б мовчки: зріз затвердження мав би іншу форму чи порядок,
/// і <c>ContentHash</c> тих самих даних відрізнявся б від зрізу подання.
/// </remarks>
internal static class SubmissionPayloadSource
{
    /// <summary>Payload зрізу: клітинки всіх екземплярів документа за період і значення шапки.</summary>
    /// <remarks>
    /// ⛔ P3 (перф-аудит): ОДИН пакетний запит на всі екземпляри. Порядок, у якому пакет повертає
    /// комірки, на payload не впливає: <see cref="SubmissionPayload.Write"/> сам сортує за (рядок,
    /// колонка), тож <c>ContentHash</c> для тих самих даних той самий байт-у-байт.
    /// </remarks>
    public static async Task<string> ReadAsync(
        ICellStore cellStore,
        IDocumentHeaderStore headers,
        long documentId,
        IReadOnlyList<TableInstanceRef> instances,
        PeriodKey periodKey,
        IReadOnlyDictionary<int, string> headerFieldCodes,
        CancellationToken ct)
    {
        var cells = new List<CellRecord>();

        if (instances.Count > 0)
        {
            var slices = await cellStore
                .ReadSlicesAsync([.. instances.Select(i => i.TableInstanceId)], periodKey, ct)
                .ConfigureAwait(false);
            foreach (var instance in instances)
            {
                if (slices.TryGetValue(instance.TableInstanceId, out var slice))
                {
                    cells.AddRange(slice);
                }
            }
        }

        var headerRaw = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var headerByCode = headerRaw
            .Where(kv => headerFieldCodes.ContainsKey(kv.Key))
            .ToDictionary(kv => headerFieldCodes[kv.Key], kv => kv.Value, StringComparer.Ordinal);

        return SubmissionPayload.Write(
            cells.Where(c => c.Address.PeriodKey.Value == periodKey.Value),
            headerByCode);
    }

    /// <summary>
    /// RS: екземпляри документа для подання/затвердження аркуша або його копії — таблиці ЦЬОГО аркуша лише тієї копії
    /// (<paramref name="sheetCopyId"/>; <c>null</c> — оригінал), решта документа як є.
    /// </summary>
    /// <param name="instances">Усі екземпляри документа за період (оригінали й копії).</param>
    /// <param name="sheet">Аркуш, що подається; <c>null</c> — аркуша немає у версії (нічого не відсіюється).</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <remarks>
    /// ⛔ Без цього зріз і валідація оригіналу брали б і екземпляри його копій (ті самі <c>TableDefId</c>): правка копії
    /// «змінювала» б затверджений оригінал (<see cref="ApprovalSnapshotRefresh.SheetChanged"/>), а словники за
    /// таблицею мовчки змішували б оригінал і копію.
    /// </remarks>
    public static IReadOnlyList<TableInstanceRef> ForSheetCopy(
        IReadOnlyList<TableInstanceRef> instances, Domain.Entities.Configuration.SheetDef? sheet, long? sheetCopyId)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (sheet is null)
        {
            return sheetCopyId is null ? instances : [.. instances.Where(i => i.SheetCopyId is null)];
        }

        var sheetTables = sheet.Tables.Select(t => t.Id).ToHashSet();
        return [.. instances.Where(i => !sheetTables.Contains(i.TableDefId) || i.SheetCopyId == sheetCopyId)];
    }

    /// <summary>Контрольна сума payload (<c>calc.SubmissionSnapshot.ContentHash</c>), hex у нижньому регістрі.</summary>
    public static string Hash(string payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
