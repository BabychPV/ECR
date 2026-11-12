// src/Ecr.Application/Documents/CalculationYear/GetCalculationYearHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Security;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// «Calculation results — усі місяці року» (CALC-RESULTS.md): <c>GET …/calculation-results/year/summary</c> (місяці,
/// таблиці з лічильниками, варіанти фільтрів) і <c>GET …/calculation-results/year/rows</c> (сторінка виходів однієї
/// таблиці).
/// </summary>
/// <remarks>
/// ⚠ Право те саме, що в <see cref="GetCalculationResultsHandler"/> (<c>Calculation.View</c> + грант на проєкт;
/// невидимий документ — 404), нового права немає. Межі читання — <see cref="CalculationYearViewBuilder"/>, однакові для
/// обох запитів і експорту.
/// <para>
/// ⛔ Таблиця, якої читач не бачить (Deny на аркуш/таблицю) або якої немає, у зведенні відсутня, а її сторінка — 200 із
/// <c>total = 0</c> і без рядків: та сама відповідь, що й для таблиці без виходів, тож запит рядків не відрізняє
/// «схована» від «немає» (без оракула існування, як і огляд року загалом).
/// </para>
/// </remarks>
public sealed class GetCalculationYearHandler(
    CalculationYearViewBuilder builder,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право — те саме, що в <see cref="GetCalculationResultsHandler.Permission"/> (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Найменший і найбільший рік запиту — за межами це помилка адреси, а не «порожній рік».</summary>
    public const int MinYear = 2000, MaxYear = 2100;

    /// <summary>Найдовший рядок пошуку.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Зведення року.</summary>
    public async Task<CalculationYearSummaryDto> SummaryAsync(
        long documentId, int year, CalculationYearFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var profile = await AuthorizeAsync(documentId, year, filter, ct).ConfigureAwait(false);
        var view = await builder
            .BuildAsync(profile, documentId, year, CalculationYearScope.Summary(filter), ct)
            .ConfigureAwait(false);
        return CalculationYearPages.Summary(view, filter);
    }

    /// <summary>Сторінка виходів таблиці <paramref name="table"/> після курсора <paramref name="cursor"/>.</summary>
    /// <remarks>
    /// Зіпсований курсор — 422 (<c>reason = cursorInvalid</c>); курсор, виданий з іншого набору рядків (перерахунок,
    /// інший фільтр чи таблиця), — 422 (<c>reason = cursorStale</c>): клієнт читає таблицю з першої сторінки
    /// (<see cref="CalculationYearCursor"/>).
    /// </remarks>
    public async Task<CalculationYearRowsDto> RowsAsync(
        long documentId, int year, string? table, CalculationYearFilter filter, string? cursor, int? limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var profile = await AuthorizeAsync(documentId, year, filter, ct).ConfigureAwait(false);
        var take = limit ?? CalculationYearPages.DefaultLimit;
        if (!CalculationYearPages.TryParseKey(table, out var slice) || take < 1 || take > CalculationYearPages.MaxLimit)
        {
            throw PageInvalid(table, $"таблиця «{table}» або розмір {take} не підходять", reason: null);
        }

        if (!CalculationYearCursor.TryDecode(cursor, out var after))
        {
            throw PageInvalid(table, "курсор зіпсований", "cursorInvalid");
        }

        var view = await builder
            .BuildAsync(profile, documentId, year, CalculationYearScope.Page(slice), ct)
            .ConfigureAwait(false);
        return CalculationYearPages.Page(view, CalculationYearPages.KeyOf(slice), filter, after, take)
               ?? throw PageInvalid(table, "курсор видано з іншого набору рядків", "cursorStale");
    }

    private static Errors.BusinessRuleException PageInvalid(string? table, string what, string? reason)
    {
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = "err.ECR-REQ-0422.calcResultsPageInvalid",
            ["table"] = table ?? "",
            ["max"] = CalculationYearPages.MaxLimit.ToString(CultureInfo.InvariantCulture),
        };
        if (reason is not null)
        {
            details["reason"] = reason;
        }

        return new Errors.BusinessRuleException(
            Ecr.Domain.Errors.ErrorCodes.RequestInvalid, $"Сторінка огляду року: {what}.", details);
    }

    /// <summary>Рік у межах <see cref="MinYear"/>…<see cref="MaxYear"/>; інакше <c>ECR-REQ-0422</c>.</summary>
    public static void RequireYear(int year)
    {
        if (year is < MinYear or > MaxYear)
        {
            throw new Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Рік {year} поза межами {MinYear}…{MaxYear}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.documentYearOutOfRange",
                    ["min"] = MinYear.ToString(CultureInfo.InvariantCulture),
                    ["max"] = MaxYear.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Рядок пошуку не довший за <see cref="MaxQueryLength"/>; інакше <c>ECR-REQ-0422</c>.</summary>
    public static void RequireFilter(CalculationYearFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Query is { Length: > MaxQueryLength })
        {
            throw new Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Рядок пошуку довший за {MaxQueryLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.calcResultsQueryTooLong",
                    ["max"] = MaxQueryLength.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    private async Task<AccessProfile> AuthorizeAsync(long documentId, int year, CalculationYearFilter filter, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, profile, documentId, Permission, ct)
            .ConfigureAwait(false);
        RequireYear(year);
        RequireFilter(filter);
        return profile;
    }
}
