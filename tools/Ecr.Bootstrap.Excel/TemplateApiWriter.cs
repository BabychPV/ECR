using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Bootstrap.Excel;

/// <summary>Куди і як записати план.</summary>
/// <param name="TemplateId">Наявний шаблон; <c>null</c> — створити новий з <paramref name="TemplateCode"/>.</param>
/// <param name="TemplateCode">Код нового шаблону.</param>
/// <param name="TemplateName">Назва нового шаблону.</param>
/// <param name="VersionNumber">Номер нової версії-чернетки (<c>Major.Minor.Patch.Build</c>).</param>
/// <param name="Language">Мова каталогу, якою записуються назви (<c>ru</c>, <c>en</c>, <c>kk</c>).</param>
public sealed record ApplyTarget(int? TemplateId, string? TemplateCode, string? TemplateName, string VersionNumber, string Language);

/// <summary>Що створено записом.</summary>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="VersionId">Версія-чернетка.</param>
/// <param name="Requests">Кількість успішних запитів запису.</param>
public sealed record ApplyResult(int TemplateId, int VersionId, int Requests);

/// <summary>Запис перервано відмовою сервера.</summary>
public sealed class ApplyFailedException : Exception
{
    /// <summary>Створює виняток.</summary>
    /// <param name="message">Що не вдалося і відповідь сервера.</param>
    /// <param name="versionId">Версія-чернетка, якщо її вже створено.</param>
    public ApplyFailedException(string message, int? versionId)
        : base(message) => VersionId = versionId;

    /// <summary>Порожній конструктор для серіалізації.</summary>
    public ApplyFailedException() { }

    /// <summary>Конструктор із повідомленням.</summary>
    /// <param name="message">Повідомлення.</param>
    public ApplyFailedException(string message) : base(message) { }

    /// <summary>Конструктор із внутрішнім винятком.</summary>
    /// <param name="message">Повідомлення.</param>
    /// <param name="inner">Причина.</param>
    public ApplyFailedException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Версія-чернетка, яку встигли створити; <c>null</c> — не встигли.</summary>
    public int? VersionId { get; }
}

/// <summary>
/// Записує план у чернетку версії шаблону через HTTP API застосунку —
/// ті самі <c>PUT …/sheets|tables|columns|rows|formulas</c>, що й конструктор у
/// веб-інтерфейсі.
/// </summary>
/// <remarks>
/// ⛔ Навмисно через API, а не прямо в БД: права (<c>Template.Edit</c>),
/// перевірка кодів, заморожування опублікованої версії, аудит і скидання кешу
/// метаданих живуть в обробниках застосунку. Другий шлях запису в <c>cfg.*</c>
/// обходив би всі ці правила — і розійшовся б із ними тихо.
///
/// ⚠ Створюється завжди НОВА версія-чернетка: публікацію (ФВ-2.9) робить
/// людина після звірки звіту.
/// </remarks>
public sealed class TemplateApiWriter(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private int _requests;

    /// <summary>Вхід локального користувача.</summary>
    /// <param name="userName">Ім'я.</param>
    /// <param name="password">Пароль.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Завдання.</returns>
    public async Task LoginLocalAsync(string userName, string password, CancellationToken ct)
        => await SendAsync(HttpMethod.Post, "api/v1/login/local", new { userName, password }, "вхід", null, ct).ConfigureAwait(false);

    /// <summary>Вхід доменного користувача (Negotiate — облікові дані процесу).</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Завдання.</returns>
    public async Task LoginWindowsAsync(CancellationToken ct)
        => await SendAsync(HttpMethod.Post, "api/v1/login/windows", null, "вхід Windows", null, ct).ConfigureAwait(false);

    /// <summary>Коди одиниць каталогу → ідентифікатори.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Словник код → Id.</returns>
    public async Task<IReadOnlyDictionary<string, int>> GetUnitsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/units", null, "каталог одиниць", null, ct).ConfigureAwait(false);
        var units = await response.Content.ReadFromJsonAsync<List<UnitItem>>(Json, ct).ConfigureAwait(false) ?? [];
        return units.ToDictionary(u => u.Code, u => u.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Звіряє одиниці плану з живим каталогом: код, якого там немає, — помилка звіту.
    /// </summary>
    /// <param name="plan">План.</param>
    /// <param name="units">Каталог одиниць.</param>
    /// <param name="report">Звіт.</param>
    public static void CheckUnits(StructurePlan plan, IReadOnlyDictionary<string, int> units, ImportReport report)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(report);

        foreach (var sheet in plan.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var column in table.Columns.Where(c => c.UnitCode is not null && !units.ContainsKey(c.UnitCode)))
                {
                    report.Error($"{sheet.Code}.{table.Code}.{column.Code}", $"одиниці {column.UnitCode} немає в каталозі сервера");
                }
            }
        }
    }

    /// <summary>Записує план у нову версію-чернетку.</summary>
    /// <param name="plan">План без помилок.</param>
    /// <param name="target">Шаблон і версія.</param>
    /// <param name="units">Каталог одиниць (код → Id).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Створені шаблон і версія.</returns>
    /// <exception cref="ApplyFailedException">Сервер відмовив; повідомлення містить крок і відповідь.</exception>
    public async Task<ApplyResult> ApplyAsync(
        StructurePlan plan, ApplyTarget target, IReadOnlyDictionary<string, int> units, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(units);
        _requests = 0;

        var templateId = target.TemplateId ?? await CreateTemplateAsync(target, ct).ConfigureAwait(false);

        var version = await ReadAsync<IdResponse>(
            HttpMethod.Post, $"api/v1/templates/{Id(templateId)}/versions",
            new { versionNumber = target.VersionNumber, cloneFromVersionId = (int?)null },
            "створення версії", null, ct).ConfigureAwait(false);
        var versionId = version.VersionId ?? throw new ApplyFailedException("сервер не повернув ідентифікатор версії", (int?)null);
        var v = $"api/v1/template-versions/{Id(versionId)}";

        foreach (var sheet in plan.Sheets)
        {
            await SendAsync(
                HttpMethod.Put, $"{v}/sheets/{Uri.EscapeDataString(sheet.Code)}",
                new { nameL10n = Text(target, sheet.Name), ordinal = sheet.Ordinal, sheetGroup = (string?)null, isMandatory = true, isVisible = true },
                $"аркуш {sheet.Code}", versionId, ct).ConfigureAwait(false);

            foreach (var table in sheet.Tables)
            {
                var savedTable = await ReadAsync<IdResponse>(
                    HttpMethod.Put, $"{v}/sheets/{Uri.EscapeDataString(sheet.Code)}/tables/{Uri.EscapeDataString(table.Code)}",
                    new { nameL10n = Text(target, table.Name), ordinal = table.Ordinal, layoutKind = table.LayoutKind, rowMode = table.RowMode, maxDynamicRows = (int?)null },
                    $"таблиця {sheet.Code}.{table.Code}", versionId, ct).ConfigureAwait(false);
                var t = $"{v}/tables/{Id(savedTable.Id)}";

                foreach (var row in table.Rows)
                {
                    await SendAsync(
                        HttpMethod.Put, $"{t}/rows/{Uri.EscapeDataString(row.Key)}",
                        new { labelL10n = Text(target, row.Label), ordinal = row.Ordinal, rowKind = row.Kind, parentRowKey = row.ParentKey, isReadOnly = false },
                        $"рядок {table.Code}.{row.Key}", versionId, ct).ConfigureAwait(false);
                }

                foreach (var column in table.Columns)
                {
                    var saved = await ReadAsync<IdResponse>(
                        HttpMethod.Put, $"{t}/columns/{Uri.EscapeDataString(column.Code)}",
                        new
                        {
                            headerL10n = Text(target, column.Header),
                            ordinal = column.Ordinal,
                            dataType = column.DataType,
                            isRequired = false,
                            isReadOnly = column.IsReadOnly,
                            isHidden = false,
                            precision = column.Scale is null ? (byte?)null : (byte)18,
                            scale = column.Scale,
                            defaultValue = (string?)null,
                            displayFormat = (string?)null,
                            styleId = (int?)null,
                            lookupRegistryDefId = (int?)null,
                            lookupFilter = (string?)null,
                            unitId = column.UnitCode is null ? (int?)null : units[column.UnitCode],
                        },
                        $"колонка {table.Code}.{column.Code}", versionId, ct).ConfigureAwait(false);

                    if (column.Formula is not null)
                    {
                        await SendAsync(
                            HttpMethod.Put, $"{t}/formulas/column/{Id(saved.Id)}",
                            new { dialect = "Template", expression = column.Formula },
                            $"формула {table.Code}.{column.Code}", versionId, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        return new ApplyResult(templateId, versionId, _requests);
    }

    private async Task<int> CreateTemplateAsync(ApplyTarget target, CancellationToken ct)
    {
        var created = await ReadAsync<IdResponse>(
            HttpMethod.Post, "api/v1/templates",
            new { code = target.TemplateCode, nameL10n = Text(target, target.TemplateName ?? target.TemplateCode ?? string.Empty) },
            "створення шаблону", null, ct).ConfigureAwait(false);
        return created.TemplateId ?? throw new ApplyFailedException("сервер не повернув ідентифікатор шаблону", (int?)null);
    }

    private static Dictionary<string, string> Text(ApplyTarget target, string text) => new() { [target.Language] = text };

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, object? body, string step, int? versionId, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, step, versionId, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
               ?? throw new ApplyFailedException($"{step}: порожня відповідь сервера", versionId);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, string step, int? versionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ApplyFailedException(
                $"{step}: {method} {path} → {status.ToString(CultureInfo.InvariantCulture)} {Truncate(detail)}", versionId);
        }

        _requests++;
        return response;
    }

    private static string Truncate(string text) => text.Length > 600 ? text[..600] + "…" : text;

    private sealed record UnitItem(int Id, string Code);

    private sealed record IdResponse(int Id, int? TemplateId, int? VersionId);
}
