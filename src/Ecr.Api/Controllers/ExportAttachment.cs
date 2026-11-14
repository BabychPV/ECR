using Ecr.Application.Common;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>Заголовок <c>Content-Disposition</c> для файлів експорту (аудит 2026-11, X-1 і X-8).</summary>
/// <remarks>
/// ⛔ Одне місце для документа і зрізу звіту: повне ім'я (кирилиця, казахські літери) — у <c>filename*=UTF-8''…</c>
/// (RFC 5987), а в <c>filename</c> — ASCII-запасне для клієнтів, що RFC 5987 не розуміють.
/// <c>File(content, type, name)</c> дав би запасне з <c>_</c> замість кожної кириличної літери, а
/// <c>System.Net.Http.Headers</c> — RFC 2047 для не-ASCII; тому <c>Microsoft.Net.Http.Headers</c>.
/// </remarks>
internal static class ExportAttachment
{
    /// <summary>Значення заголовка для вкладення.</summary>
    /// <param name="baseName">Основа імені без розширення (може містити не-ASCII).</param>
    /// <param name="asciiDefault">ASCII-основа, коли від імені не лишилося друкованих ASCII-символів.</param>
    /// <param name="extension">Розширення без крапки.</param>
    /// <returns>Рядок заголовка <c>Content-Disposition</c>.</returns>
    public static string Header(string baseName, string asciiDefault, string extension)
        => new ContentDispositionHeaderValue("attachment")
        {
            FileName = $"{ExportFileName.AsciiFallback(baseName, asciiDefault)}.{extension}",
            FileNameStar = $"{baseName}.{extension}",
        }.ToString();
}
