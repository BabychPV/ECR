// src/Ecr.Application/Localization/GetUiStringsHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Localization;

/// <summary>
/// Каталог рядків інтерфейсу за мовою і областю (ФВ-14.9, D-95, D-114).
/// </summary>
/// <remarks>
/// Область `Public` віддається **анонімно** — сторінка входу потребує підписів
/// кнопок раніше, ніж хтось автентифікований. `Private` — після входу, бо
/// назви адміністративних областей не мають бути видимі невідомому
/// відвідувачу (ФВ-14.2).
/// </remarks>
public sealed class GetUiStringsHandler(IUiStringCatalog catalog)
{
    public Task<UiStringCatalog> HandleAsync(string languageCode, bool publicOnly, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) кеш за ключем {lang}:{scope}:{revision} (ФВ-14.9c) — та сама " +
            "   схема, що з метаданими: нова версія дає новий ключ, тому " +
            "   інвалідація не потрібна і два інстанси не розійдуться;\n" +
            "2) fallback: немає ключа в мові → мова за замовчуванням → САМ КЛЮЧ. " +
            "   Порожнечу не повертати ніколи: одна забута локалізація не має " +
            "   ламати екран;\n" +
            "3) ключі err.<код> — звідси ж, окремого сховища немає (ФВ-14.9a);\n" +
            "4) віддавати Revision як ETag; збіг з If-None-Match → 304.");
}
