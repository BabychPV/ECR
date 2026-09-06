using Ecr.Application.Expressions;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Мова виразів: перевірка тексту і склад мови для редактора (<c>ФВ-9.15a</c>).
/// </summary>
[ApiController]
[Route("api/v1/expressions")]
[Authorize]
public sealed class ExpressionsController(
    ValidateExpressionHandler validate,
    GetExpressionMetadataHandler metadata) : ControllerBase
{
    /// <summary>Перевіряє вираз так само, як це зробить публікація.</summary>
    /// <param name="request">Вираз, діалект і місце в структурі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зауваження з позиціями, тип результату і пропущені перевірки.</returns>
    /// <remarks>
    /// ⛔ <c>200</c> означає «перевірка виконалася», а НЕ «зауважень немає» —
    /// так само, як у <c>POST /documents/{id}/validate</c>. Відмовляти
    /// <c>422</c> на кожен проміжний стан тексту було б неправильно за суттю:
    /// користувач друкує вираз посимвольно, і половина станів синтаксично
    /// невалідна за побудовою. Помилка транспорту і незакінчена формула — різні
    /// події, і однаковий код зробив би їх нерозрізнюваними.
    /// </remarks>
    [HttpPost("validate")]
    [ProducesResponseType<ExpressionValidationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Validate(
        [FromBody] ValidateExpressionBody request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await validate
            .HandleAsync(
                new ExpressionValidationRequest(
                    request.Expression,
                    request.Dialect,
                    request.TemplateVersionId,
                    request.TableDefId,
                    request.RowKey,
                    request.ColumnDefId),
                ct)
            .ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>Склад мови: функції діалекту і символи контексту.</summary>
    /// <param name="dialect">Діалект (<c>D-113</c>).</param>
    /// <param name="templateVersionId">Версія шаблону — джерело полів <c>HDR.</c>.</param>
    /// <param name="methodologyVersionId">Версія методології — джерело <c>CST.</c>, <c>!</c>, <c>@</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Функції з сигнатурами і символи для автодоповнення.</returns>
    [HttpGet("metadata")]
    [ProducesResponseType<ExpressionMetadataDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Metadata(
        [FromQuery] ExpressionDialect dialect,
        [FromQuery] int? templateVersionId,
        [FromQuery] int? methodologyVersionId,
        CancellationToken ct)
    {
        var result = await metadata
            .HandleAsync(dialect, templateVersionId, methodologyVersionId, ct)
            .ConfigureAwait(false);

        return Ok(result);
    }
}

/// <summary>Тіло запиту на перевірку виразу.</summary>
/// <param name="Expression">Текст виразу.</param>
/// <param name="Dialect">Діалект (<c>D-113</c>).</param>
/// <param name="TemplateVersionId">
/// Версія шаблону; <c>null</c> — перевіряється лише синтаксис.
/// </param>
/// <param name="TableDefId">Таблиця, в якій живе вираз.</param>
/// <param name="RowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
/// <param name="ColumnDefId">Колонка — для підстановки <c>{Month}</c>.</param>
public sealed record ValidateExpressionBody(
    string Expression,
    ExpressionDialect Dialect,
    int? TemplateVersionId,
    int? TableDefId,
    string? RowKey,
    int? ColumnDefId);
