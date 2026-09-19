// src/Ecr.Application/Reporting/ReportSnapshotHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Reporting;

/// <summary>
/// Перелік побудованих зрізів. Право <c>Report.ViewRegulatory</c>.
/// </summary>
/// <remarks>
/// ⚠ Самих звітів у системі немає: звітність лишається в SSRS (D-52). Наша
/// межа — <c>rpt.*</c>, незмінний зріз, який SSRS читає.
/// <para>
/// ⛔ Q-239 (аудит фази 3, звітність/експорт). До цього перевірялося лише
/// глобальне право <c>Report.ViewRegulatory</c>, а <c>projectId</c> був
/// НЕОБОВʼЯЗКОВИМ фільтром на розсуд клієнта: <c>GET
/// /api/v1/reports/snapshots</c> без нього віддавав зрізи ВСІХ проєктів
/// системи. Це той самий клас дефекту, який
/// <see cref="Documents.ListDocumentsHandler"/> закриває власним фільтром за
/// грантом, із власним поясненням: «перелік документів чужого проєкту — це
/// вже відомості про те, які обʼєкти звітують і як часто». Тут витікало
/// більше: ідентифікатори проєктів, періоди, статуси подання, кількість
/// рядків і КОНТРОЛЬНА СУМА вмісту — саме те, чим звіт звіряють на
/// незмінність.
/// </para>
/// </remarks>
public sealed class ListReportSnapshotsHandler(
    IReportSnapshotBuilder snapshots,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд регуляторної звітності (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.ViewRegulatory";

    /// <summary>Віддає зрізи з часом побудови й контрольною сумою.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі ВИДИМІ користувачу.</param>
    /// <param name="periodKey">Період; <c>null</c> — усі.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<ReportSnapshotSummary>> HandleAsync(
        int? projectId, int? periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await snapshots
            .ListAsync(projectId, periodKey, VisibleProjects(profile), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Проєкти, на які в профілі є грант рівня <c>Read</c> і вище.</summary>
    /// <remarks>
    /// ⚠ Рішення по кожному ключу приймає <see cref="AccessProfile.LevelFor"/>,
    /// а не сам факт наявності запису в <see cref="AccessProfile.Grants"/>:
    /// заборона виграє на будь-якому рівні (ФВ-6.6), і проєкт із явним
    /// <c>IsDeny</c> лежить у <c>Grants</c> так само, як дозволений.
    /// Обчислювати це другим, власним правилом означало б завести другу
    /// відповідь на те саме питання — рівно те, що <c>Q-188</c> уже зводив
    /// назад в одне місце.
    /// </remarks>
    private static List<int> VisibleProjects(AccessProfile profile)
    {
        var prefix = $"{ResourceKind.Project}:";
        var visible = new List<int>();

        foreach (var key in profile.Grants.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(
                    key.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            if (profile.LevelFor(ResourceKind.Project, id) >= GrantLevel.Read)
            {
                visible.Add(id);
            }
        }

        return visible;
    }
}

/// <summary>
/// Побудова зрізу. Право <c>Report.BuildSnapshot</c>.
/// </summary>
/// <remarks>
/// Зріз **незмінний**: повторна побудова створює новий, а не переписує
/// старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні давали
/// б різні числа без жодного сліду.
/// <para>
/// ⛔ Q-239 (аудит фази 3, звітність/експорт). До цього перевірялося лише
/// глобальне <c>Report.BuildSnapshot</c> — без гранта на ПРОЄКТ, чий
/// ідентифікатор приходить тілом запиту. Це дослівно та сама прогалина, яку
/// <c>Q-238</c> закрив у <c>RunCalculationHandler</c> (перерахунок чужого
/// проєкту), і наслідок тут не «зайвий рядок у таблиці»: побудова кличе
/// <c>SwitchCurrentAsync</c>, тобто ЗНІМАЄ поточність із наявного зрізу
/// чужого проєкту й ставить на своє місце власний. Регуляторна вʼюха
/// <c>rpt.v_*</c> після цього віддає не той зріз, який власник проєкту
/// побудував і звірив, а той, який побудував сторонній — з іншим часом,
/// іншою контрольною сумою і, якщо дані відтоді змінилися, іншими числами.
/// </para>
/// </remarks>
public sealed class BuildReportSnapshotHandler(
    IReportDefinitionStore definitions,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на побудову зрізу (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.BuildSnapshot";

    /// <summary>Ставить побудову в чергу.</summary>
    /// <param name="code">Код звіту.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    /// <exception cref="NotFoundException">Звіту з таким кодом немає.</exception>
    /// <exception cref="AccessDeniedException">
    /// Немає гранта на проєкт — <c>ECR-AUTH-0403</c> (Q-239).
    /// </exception>
    public async Task<string> HandleAsync(string code, int projectId, int periodKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Поріг — `Read`, той самий, що й у `CanReadDocumentAsync` і у
        // `RunCalculationHandler` (Q-238): саме право на побудову несе окрема
        // функціональна перевірка вище, грант лише звужує «для ЯКОГО проєкту».
        // ⛔ Перевірка стоїть ПЕРЕД резолвом версії навмисно: інакше сторонній
        // дізнавався б із коду відповіді, які коди звітів існують у системі, ще
        // до того, як дійде до відмови в доступі.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Read)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до проєкту {projectId}: зріз за ним не будується.");
        }

        // ⚠ Версія резолвиться ТУТ, а не в задачі. Невідомий код звіту має
        // дати 404 одразу, а не через хвилину у вигляді задачі, яка
        // «завершилася помилкою»: користувач не зрозуміє, що просто помилився
        // в коді.
        var versionId = await definitions.FindCurrentVersionIdAsync(code, ct).ConfigureAwait(false)
                        ?? throw new NotFoundException(
                            ErrorCodes.ReportNotFound,
                            $"Звіту «{code}» немає або в нього немає чинної версії.");

        return await jobs
            .EnqueueAsync<IReportSnapshotJob>(
                new ReportSnapshotTask(versionId, projectId, periodKey), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Перевірка незмінності зрізу (BE-17). Право <c>Report.ViewRegulatory</c> —
/// те саме, що відкриває перелік: перевірка нічого не змінює, вона лише
/// відповідає на питання, заради якого суму в переліку й показують.
/// </summary>
public sealed class VerifyReportSnapshotHandler(
    IReportSnapshotBuilder snapshots,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Перераховує суму збереженого вмісту й порівнює зі збереженою.</summary>
    /// <param name="snapshotId">Зріз.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Зрізу немає або він у невидимому проєкті.</exception>
    public async Task<SnapshotVerifyResponse> HandleAsync(long snapshotId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListReportSnapshotsHandler.Permission, ct)
            .ConfigureAwait(false);

        var projectId = await snapshots.FindProjectIdAsync(snapshotId, ct).ConfigureAwait(false);

        // ⛔ Чужий проєкт — той самий 404, що й неіснуючий зріз, а не 403:
        // перелік (Q-239) чужих зрізів не показує взагалі, і відмова «є, але
        // не твій» розповідала б перебором ідентифікаторів те, що перелік
        // приховує.
        if (projectId is not { } project
            || profile.LevelFor(ResourceKind.Project, project) < GrantLevel.Read)
        {
            throw NotFound(snapshotId);
        }

        var hashes = await snapshots.VerifyAsync(snapshotId, ct).ConfigureAwait(false)
                     ?? throw NotFound(snapshotId);

        // ⚠ Порожня збережена сума — «не збігається», а не «нема чого
        // перевіряти»: зріз без суми довести свою незмінність не може.
        var format = MatchedFormat(hashes);

        return new SnapshotVerifyResponse(format is not null, hashes.Stored, hashes.Actual, format);
    }

    /// <summary>Формат суми, за яким збігся вміст.</summary>
    public const string FormatCurrent = "current";

    /// <summary>Формат до BE-17.</summary>
    /// <remarks>
    /// ⚠ ТИМЧАСОВО: приймається для зрізів, побудованих до BE-17; прибрати,
    /// коли таких не лишиться.
    /// </remarks>
    public const string FormatLegacy = "legacy";

    private static string? MatchedFormat(SnapshotHashes hashes)
    {
        if (hashes.Stored.Length == 0)
        {
            return null;
        }

        if (string.Equals(hashes.Stored, hashes.Actual, StringComparison.OrdinalIgnoreCase))
        {
            return FormatCurrent;
        }

        return hashes.LegacyActual is { Length: > 0 } legacy
               && string.Equals(hashes.Stored, legacy, StringComparison.OrdinalIgnoreCase)
            ? FormatLegacy
            : null;
    }

    private static NotFoundException NotFound(long snapshotId)
        => new(ErrorCodes.ReportNotFound, $"Зрізу {snapshotId} немає.");
}

/// <summary>
/// Рядки зрізу сторінками (D-52a): другий споживач <c>rpt.*</c> поруч із SSRS.
/// Право <c>Report.ViewRegulatory</c> і грант на проєкт зрізу — як у перевірки.
/// </summary>
public sealed class GetSnapshotRowsHandler(
    IReportSnapshotBuilder snapshots,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Рядків на сторінці, якщо клієнт не сказав.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Стеля сторінки.</summary>
    public const int MaxLimit = 500;

    /// <summary>Віддає сторінку рядків після курсора.</summary>
    /// <param name="snapshotId">Зріз.</param>
    /// <param name="cursor">Останній уже отриманий <c>RowNo</c>; <c>null</c> — з початку.</param>
    /// <param name="limit">Розмір сторінки; обрізається до <see cref="MaxLimit"/>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Зрізу немає або він у невидимому проєкті.</exception>
    public async Task<SnapshotRowsPage> HandleAsync(long snapshotId, int? cursor, int? limit, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListReportSnapshotsHandler.Permission, ct)
            .ConfigureAwait(false);

        var projectId = await snapshots.FindProjectIdAsync(snapshotId, ct).ConfigureAwait(false);

        // ⛔ Чужий = неіснуючий, той самий 404, що й у перевірки (BE-17, Q-239).
        if (projectId is not { } project
            || profile.LevelFor(ResourceKind.Project, project) < GrantLevel.Read)
        {
            throw NotFound(snapshotId);
        }

        return await snapshots
                   .RowsAsync(
                       snapshotId, Math.Max(cursor ?? 0, 0), Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct)
                   .ConfigureAwait(false)
               ?? throw NotFound(snapshotId);
    }

    private static NotFoundException NotFound(long snapshotId)
        => new(
            ErrorCodes.ReportNotFound,
            $"Зрізу {snapshotId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0404.snapshot",
                ["snapshotId"] = snapshotId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>Підсумок перевірки зрізу.</summary>
/// <param name="Matches">Чи перерахована сума збіглася зі збереженою.</param>
/// <param name="Stored">Сума, записана при побудові (hex).</param>
/// <param name="Actual">Сума, перерахована за збереженими рядками (hex).</param>
/// <param name="MatchedFormat">
/// За яким форматом суми збіглося: <c>current</c>, <c>legacy</c> (зріз,
/// побудований до BE-17) або <c>null</c> — не збіглося за жодним.
/// </param>
public sealed record SnapshotVerifyResponse(bool Matches, string Stored, string Actual, string? MatchedFormat);

/// <summary>Завдання на побудову зрізу.</summary>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record ReportSnapshotTask(int ReportVersionId, int ProjectId, int PeriodKey);
