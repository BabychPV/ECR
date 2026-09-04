using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Синхронний доступ до вже завантаженого знімка структури версії.
/// </summary>
/// <remarks>
/// ⚠ Порт існує через одну конкретну обставину: <see cref="IFormulaEngine"/>
/// заморожений контрактом і оголошує
/// <c>ExtractDependencies</c> СИНХРОННИМ, тоді як
/// <see cref="IMetadataCache.GetAsync"/> асинхронний. Обійти це очікуванням
/// (<c>GetAwaiter().GetResult()</c>) не можна: блокувальні виклики заборонені
/// архітектурним правилом, і не formально — саме вони виїдають пул потоків на
/// піку останнього дня періоду.
///
/// Тому контракт тут інший і сильніший: знімок МАЄ БУТИ вже завантажений.
/// Реалізація нічого не читає з бази — вона лише дістає готове з кешу, а якщо
/// його немає, каже про це прямо. Порядок «спершу <c>GetAsync</c>, потім
/// розбір формул» на шляху публікації виконується завжди.
/// </remarks>
public interface ITemplateStructure
{
    /// <summary>Знімок структури версії.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <exception cref="InvalidOperationException">
    /// Знімка немає в кеші: спершу викличте <see cref="IMetadataCache.GetAsync"/>.
    /// </exception>
    public TemplateVersionSnapshot Get(int templateVersionId);
}
