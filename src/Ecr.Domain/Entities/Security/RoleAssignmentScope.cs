// src/Ecr.Domain/Entities/Security/RoleAssignmentScope.cs
using System.Text.Json;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Область дії призначення ролі (ФВ-6.14): роль діє ЛИШЕ в перелічених
/// проєктах і — за бажанням — лише на перелічених аркушах і в проміжку
/// звітних періодів. Зберігається в <c>sec.RoleAssignment.ScopeJson</c> як
/// <c>{"projects":[1,2],"sheets":["F1"],"periods":{"from":202601,"to":null}}</c>.
/// </summary>
/// <remarks>
/// ⛔ D-214 (рішення людини 2026-09-29): усі виміри — ПЕРЕТИН, кожен лише
/// звужує. Порожнє або відсутнє <c>sheets</c>/<c>periods</c> — без обмеження за
/// цим виміром; <c>projects</c> обов'язкове й непорожнє.
///
/// ⚠ Аркуш адресується КОДОМ (<c>SheetDef.Code</c>), а не ідентифікатором:
/// код унікальний у версії шаблону (<c>UQ_SheetDef</c>), переходить у клон
/// версії без змін і є ідентичністю аркуша для виразів, тоді як
/// <c>SheetDefId</c> у кожної версії свій. Область, записана за
/// ідентифікатором, мовчки перестала б діяти після переходу проєкту на нову
/// версію шаблону.
///
/// ⚠ Невідомий ключ у збереженому JSON НЕ ігнорується (<see cref="TryParse"/>):
/// поле, яке записали й не застосували, розширило б права мовчки.
/// </remarks>
public sealed class RoleAssignmentScope
{
    private const string ProjectsKey = "projects";
    private const string SheetsKey = "sheets";
    private const string PeriodsKey = "periods";
    private const string FromKey = "from";
    private const string ToKey = "to";

    private RoleAssignmentScope(
        IReadOnlyList<int> projectIds, IReadOnlyList<string> sheetCodes, PeriodKey? periodFrom, PeriodKey? periodTo)
    {
        ProjectIds = projectIds;
        SheetCodes = sheetCodes;
        PeriodFrom = periodFrom;
        PeriodTo = periodTo;
    }

    /// <summary>Проєкти, у яких діє призначення; впорядковані, без повторів, непорожні.</summary>
    public IReadOnlyList<int> ProjectIds { get; }

    /// <summary>Коди аркушів; порожньо — усі аркуші. Впорядковані ординально, без повторів.</summary>
    public IReadOnlyList<string> SheetCodes { get; }

    /// <summary>Перший звітний період проміжку, включно; <c>null</c> — від початку.</summary>
    public PeriodKey? PeriodFrom { get; }

    /// <summary>Останній звітний період проміжку, включно; <c>null</c> — без кінця.</summary>
    public PeriodKey? PeriodTo { get; }

    /// <summary>Чи звужує область щось, крім проєктів.</summary>
    public bool IsNarrowed => SheetCodes.Count > 0 || PeriodFrom is not null || PeriodTo is not null;

    /// <summary>Чи обмежено період.</summary>
    public bool HasPeriods => PeriodFrom is not null || PeriodTo is not null;

    /// <summary>Створює область.</summary>
    /// <param name="projectIds">Проєкти; непорожній перелік додатних ідентифікаторів без повторів.</param>
    /// <param name="sheetCodes">Коди аркушів; <c>null</c> чи порожньо — усі. Коди — за правилом <see cref="EcrCode"/>, без повторів.</param>
    /// <param name="periodFrom">Перший період, включно; <c>null</c> — відкрито.</param>
    /// <param name="periodTo">Останній період, включно; <c>null</c> — відкрито.</param>
    /// <returns>Область дії.</returns>
    /// <exception cref="DomainException">Будь-яке порушення — <c>ECR-REQ-0422</c>.</exception>
    /// <remarks>
    /// ⛔ Порожня область проєктів — не «без обмежень», а роль, що не дає нічого.
    /// Таку річ не записуємо зовсім: відсутність області й порожня область
    /// читалися б однаково тим, хто дивиться в базу, і діяли б протилежно.
    /// </remarks>
    public static RoleAssignmentScope Create(
        IEnumerable<int> projectIds,
        IEnumerable<string>? sheetCodes = null,
        PeriodKey? periodFrom = null,
        PeriodKey? periodTo = null)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        var list = projectIds.ToList();
        if (list.Count == 0 || list.Exists(id => id <= 0) || list.Distinct().Count() != list.Count)
        {
            throw Invalid("Область дії ролі: потрібен непорожній перелік проєктів без повторів.");
        }

        var sheets = (sheetCodes ?? []).ToList();
        if (sheets.Exists(code => !EcrCode.TryCreate(code, out _))
            || sheets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Count)
        {
            throw Invalid("Область дії ролі: коди аркушів — без повторів і за правилом коду.");
        }

        if ((periodFrom is { IsValid: false }) || (periodTo is { IsValid: false })
            || (periodFrom is { } from && periodTo is { } to && from.Value > to.Value))
        {
            throw Invalid("Область дії ролі: межі періодів — ключі Рік*100+Номер, початок не пізніше кінця.");
        }

        list.Sort();
        sheets.Sort(StringComparer.Ordinal);
        return new RoleAssignmentScope(list, sheets, periodFrom, periodTo);
    }

    /// <summary>Чи входить проєкт в область.</summary>
    /// <param name="projectId">Проєкт.</param>
    public bool Includes(int projectId) => ProjectIds.Contains(projectId);

    /// <summary>Чи діє область на аркуші.</summary>
    /// <param name="sheetCode">Код аркуша; <c>null</c> — аркуш невідомий.</param>
    /// <remarks>⛔ Невідомий аркуш при звуженні за аркушами — поза областю (закрито за замовчуванням).</remarks>
    public bool IncludesSheet(string? sheetCode)
        => SheetCodes.Count == 0 || (sheetCode is not null && SheetCodes.Contains(sheetCode, StringComparer.Ordinal));

    /// <summary>Чи діє область у періоді.</summary>
    /// <param name="period">Період; <c>null</c> — невідомий.</param>
    /// <remarks>⛔ Невідомий період при звуженні за періодами — поза областю (закрито за замовчуванням).</remarks>
    public bool IncludesPeriod(PeriodKey? period)
        => !HasPeriods
           || (period is { } p
               && (PeriodFrom is not { } from || p.Value >= from.Value)
               && (PeriodTo is not { } to || p.Value <= to.Value));

    /// <summary>Чи перетинає проміжок періодів області проміжок <c>[first, last]</c>.</summary>
    /// <param name="first">Перший період проєкту.</param>
    /// <param name="last">Останній період проєкту.</param>
    public bool OverlapsPeriods(PeriodKey first, PeriodKey last)
        => (PeriodFrom is not { } from || last.Value >= from.Value)
           && (PeriodTo is not { } to || first.Value <= to.Value);

    /// <summary>JSON для <c>sec.RoleAssignment.ScopeJson</c>.</summary>
    /// <remarks>
    /// ⚠ Незадані виміри в JSON не пишуться: область лише з проєктами лишається
    /// дослівно тим самим <c>{"projects":[…]}</c>, що й до D-214.
    /// </remarks>
    public string ToJson()
    {
        var root = new Dictionary<string, object> { [ProjectsKey] = ProjectIds };

        if (SheetCodes.Count > 0)
        {
            root[SheetsKey] = SheetCodes;
        }

        if (HasPeriods)
        {
            root[PeriodsKey] = new Dictionary<string, int?>
            {
                [FromKey] = PeriodFrom?.Value,
                [ToKey] = PeriodTo?.Value,
            };
        }

        return JsonSerializer.Serialize(root);
    }

    /// <summary>Розбирає збережену область.</summary>
    /// <param name="json">Вміст <c>ScopeJson</c>.</param>
    /// <returns>
    /// Область, або <c>null</c> — JSON зіпсований, несе невідомий ключ чи
    /// значення, що не проходить <see cref="Create"/>.
    /// </returns>
    /// <remarks>
    /// ⛔ Викликач трактує <c>null</c> тут як «область, що не містить нічого»
    /// (роль не діє), а НЕ як «області немає» (роль діє скрізь): зіпсований
    /// запис має відбирати права, а не роздавати їх.
    /// </remarks>
    public static RoleAssignmentScope? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<int>? projects = null;
            List<string>? sheets = null;
            PeriodKey? from = null;
            PeriodKey? to = null;

            foreach (var property in root.EnumerateObject())
            {
                var ok = property.Name switch
                {
                    ProjectsKey => TryReadProjects(property.Value, out projects),
                    SheetsKey => TryReadSheets(property.Value, out sheets),
                    PeriodsKey => TryReadPeriods(property.Value, out from, out to),
                    _ => false,
                };

                if (!ok)
                {
                    return null;
                }
            }

            return projects is null ? null : Create(projects, sheets, from, to);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DomainException)
        {
            return null;
        }
    }

    private static bool TryReadProjects(JsonElement value, out List<int>? projects)
    {
        projects = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var id))
            {
                return false;
            }

            result.Add(id);
        }

        projects = result;
        return true;
    }

    private static bool TryReadSheets(JsonElement value, out List<string>? sheets)
    {
        sheets = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            result.Add(item.GetString()!);
        }

        sheets = result;
        return true;
    }

    private static bool TryReadPeriods(JsonElement value, out PeriodKey? from, out PeriodKey? to)
    {
        from = null;
        to = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var bound in value.EnumerateObject())
        {
            PeriodKey? key;
            if (bound.Value.ValueKind == JsonValueKind.Null)
            {
                key = null;
            }
            else if (bound.Value.ValueKind == JsonValueKind.Number && bound.Value.TryGetInt32(out var raw))
            {
                key = new PeriodKey(raw);
            }
            else
            {
                return false;
            }

            switch (bound.Name)
            {
                case FromKey:
                    from = key;
                    break;
                case ToKey:
                    to = key;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static DomainException Invalid(string message)
        => new(
            ErrorCodes.RequestInvalid,
            message,
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422" });
}
