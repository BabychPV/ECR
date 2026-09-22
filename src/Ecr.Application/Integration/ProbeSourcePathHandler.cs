// src/Ecr.Application/Integration/ProbeSourcePathHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>Наслідок пробного читання одного значення мапінгу (ФВ-13.17).</summary>
/// <param name="Path">Шлях, який пробували.</param>
/// <param name="HasValue">
/// <c>false</c> — шлях є в каталозі джерела, але в пробному вікні
/// (<see cref="ProbeSourcePathHandler.ProbeWindowDays"/>) для нього немає жодної точки.
/// </param>
/// <param name="ValueNumeric">Числове значення в одиниці джерела.</param>
/// <param name="ValueString">Текстове значення для нечислових атрибутів.</param>
/// <param name="UnitSymbol">UOM джерела; <c>null</c> — джерело одиниці не назвало.</param>
/// <param name="Timestamp">Мітка часу прочитаної точки; <c>null</c> — <see cref="HasValue"/> хибне.</param>
/// <param name="Quality">Якість у термінах джерела.</param>
public sealed record SourcePathProbeResult(
    string Path,
    bool HasValue,
    decimal? ValueNumeric,
    string? ValueString,
    string? UnitSymbol,
    DateTime? Timestamp,
    string? Quality);

/// <summary>
/// «Перевірити конфігурацію» до першого збору (ФВ-13.17): пробний запит ОДНОГО
/// значення мапінгу до РЕАЛЬНОГО джерела, без запису результату в постійне
/// сховище; на неіснуючий шлях — підказка схожих імен. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Не плутати з переглядом мапінгу (ФВ-13.14, <see cref="Sources.PreviewMappingHandler"/>):
/// той читає вже ЗІБРАНІ точки (<c>ext.RawDataPoint</c>) і працює лише ПІСЛЯ
/// першого збору — читання наживо там навмисно відсутнє (<see cref="Sources.IMappingPreviewStore"/>).
/// Ця проба — навпаки, ДО першого збору: ходить у джерело наживо тим самим
/// адаптером, яким потім збиратимуть (<see cref="IExternalDataSource.ReadAsync"/>),
/// і нічого не зберігає.
///
/// ⛔ Існування шляху перевіряється КАТАЛОГОМ (<see cref="ISourceCatalogReader"/>,
/// той самий порт і той самий обхід, що для ФВ-13.13 — обхід тут не
/// дублюється), а не HTTP-статусом живого читання: адаптер
/// (<c>PiWebApiDataSource.GetAsync</c>) віддає 404 на неіснуючий шлях як
/// звичайну відмову джерела (той самий <c>BusinessRuleException</c>, що й
/// «джерело лежить», доведено тестом
/// <c>DiscoverAsync_404_КидаєBusinessRuleException_БезПовторів</c>), і
/// розрізнити «шляху немає» від «джерело недоступне» за цим винятком
/// неможливо. Каталог — надійне джерело істини про те, що там є, і живе
/// читання виконується, лише коли каталог уже підтвердив шлях.
/// </remarks>
public sealed class ProbeSourcePathHandler(
    IDataSourceStore store,
    ISourceCatalogReader reader,
    IEnumerable<IExternalDataSource> adapters,
    SourceCatalogPolicy policy,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стеля довжини шляху мапінгу.</summary>
    public const int MaxPathLength = 500;

    /// <summary>Стеля кількості підказок схожих імен.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>Вікно пробного читання назад від «зараз».</summary>
    /// <remarks>
    /// ⚠ Судження, не вимога ФВ-13.17: проба доводить, що шлях РЕАЛЬНО
    /// читається, а не яку саме точку показати. 30 днів — досить, щоб не
    /// впертися в порожнє вікно на рідко оновлюваному атрибуті, і досить
    /// вузько, щоб не тягнути роками історії заради однієї точки.
    /// </remarks>
    public const int ProbeWindowDays = 30;

    /// <summary>Виконує пробу шляху <paramref name="path"/> у джерелі <paramref name="id"/>.</summary>
    public async Task<SourcePathProbeResult> HandleAsync(int id, string? path, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct).ConfigureAwait(false);

        var trimmed = path?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxPathLength)
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.probePathInvalid",
                $"Шлях мапінгу для проби — від 1 до {MaxPathLength} символів.");
        }

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var (parent, leaf) = SplitPath(trimmed);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(policy.Timeout);

        try
        {
            var candidates = await LevelAsync(source.Id, parent, bounded.Token).ConfigureAwait(false);

            if (!candidates.Any(d => string.Equals(d.EntityPath, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new NotFoundException(
                    ErrorCodes.SourceEntityNotFound,
                    $"Шлях «{trimmed}» не знайдено в джерелі «{source.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0404.sourcePathNotFound",
                        ["path"] = trimmed,
                        ["code"] = source.Code,
                        ["suggestions"] = Suggest(leaf, candidates.Select(d => d.Code)),
                    });
            }

            var adapter = adapters.FirstOrDefault(a => a.Transport == source.Transport)
                          ?? throw Unavailable(source, "err.ECR-INT-0503.probeUnavailable", "недоступне");

            var to = clock.UtcNow;
            var from = to.AddDays(-ProbeWindowDays);

            // ⚠ `SourceEntityId: 0` — заглушка. Жоден адаптер її не читає:
            // ReadAsync іде за SourcePath/DataSourceId (PiWebApiDataSource.ReadAsync,
            // PiSqlClientDataSource.ReadAsync), поле існує заради природного
            // ключа ext.RawDataPoint при ЗБЕРЕЖЕННІ — якого тут немає.
            var result = await adapter
                .ReadAsync(new CollectionRequest(source.Id, 0, trimmed, from, to, MaxPoints: 1), bounded.Token)
                .ConfigureAwait(false);

            var point = result.Points.Count > 0 ? result.Points[0] : null;

            return new SourcePathProbeResult(
                trimmed, point is not null, point?.ValueNumeric, point?.ValueString,
                point?.SourceUnitSymbol, point?.Timestamp, point?.Quality);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(source, "err.ECR-INT-0503.probeTimeout", "не відповіло вчасно");
        }

        // ⚠ Та сама конвенція, що в каталозі (SourceCatalogHandler.ReadAsync):
        // будь-яка неЕCR-відмова транспорту або BusinessRuleException із кодом
        // SourceUnavailable — це «джерело недоступне», а не 404. Відмова
        // автентифікації (SourceAuthenticationException) проходить як є.
        catch (Exception e) when (e is not EcrException and not OperationCanceledException
                                  || e is BusinessRuleException { ErrorCode: ErrorCodes.SourceUnavailable })
        {
            throw Unavailable(source, "err.ECR-INT-0503.probeUnavailable", "недоступне");
        }
    }

    /// <summary>Елементи й атрибути одного рівня каталогу (той самий склад, що для ФВ-13.13).</summary>
    private async Task<List<SourceEntityDescriptor>> LevelAsync(
        int dataSourceId, string? parent, CancellationToken ct)
    {
        var found = new List<SourceEntityDescriptor>(
            await reader.BrowseAsync(dataSourceId, parent, ct).ConfigureAwait(false));

        if (parent is not null)
        {
            found.AddRange(await reader.AttributesAsync(dataSourceId, parent, ct).ConfigureAwait(false));
        }

        return found;
    }

    private BusinessRuleException Unavailable(DataSource source, string messageKey, string what)
        => new(
            ErrorCodes.SourceUnavailable,
            $"Джерело «{source.Code}» {what}: пробу не виконано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["code"] = source.Code,
                ["timeoutSeconds"] = (int)policy.Timeout.TotalSeconds,
            });

    /// <summary>Розбиває шлях мапінгу на «рівень» (шлях елемента) і останній сегмент (лист).</summary>
    /// <remarks>
    /// ⚠ Судження: конвенція шляху — та сама, що вживають обидва адаптери PI
    /// (<c>PiSqlClientDataSource.Split</c>, поле <c>Path</c> атрибута PI Web
    /// API з <c>PiWebApiCatalogTests</c>) — <c>|</c> відділяє атрибут від
    /// елемента, <c>\</c> — елемент від батька. Формального визначення шляху
    /// мапінгу в ФВ-13.17 немає.
    /// </remarks>
    public static (string? Parent, string Leaf) SplitPath(string path)
    {
        var pipe = path.LastIndexOf('|');
        if (pipe >= 0)
        {
            return (path[..pipe], path[(pipe + 1)..]);
        }

        var slash = path.LastIndexOf('\\');
        return slash >= 0 ? (path[..slash], path[(slash + 1)..]) : (null, path);
    }

    /// <summary>Найближчі за написанням імена серед кандидатів того самого рівня.</summary>
    public static IReadOnlyList<string> Suggest(string leaf, IEnumerable<string> candidates)
        => [.. candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => (Code: code, Distance: EditDistance(leaf, code)))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(x => x.Code)];

    /// <summary>
    /// Відстань Левенштейна без урахування регістру: AF регістру в іменах не
    /// розрізняє (той самий вибір, що в <c>PiAfCatalogReader.IsChildOf</c>).
    /// </summary>
    public static int EditDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var d = new int[a.Length + 1, b.Length + 1];

        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
