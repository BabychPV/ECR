// src/Ecr.Application/Ports/IReportViewStatus.cs
namespace Ecr.Application.Ports;

/// <summary>Збій генерації вʼюх: що саме не створено і чому.</summary>
/// <param name="TemplateVersionId">Версія, для якої йшов виклик; <c>null</c> — прогін по всіх.</param>
/// <param name="Code">Номер помилки SQL: 50422 (таблиця &gt; 250 колонок), 50409 (зіткнення імен), інший.</param>
/// <param name="Message">Повідомлення процедури: містить шаблон, версію, аркуш і таблицю.</param>
/// <param name="StableTemplateId">
/// ✎ P-4b P2: збій саме стабільних вʼюх (без версії) шаблону з цим id — окремий ключ, бо задача шаблону кличе їх окремо від
/// <c>_vN</c>; <c>null</c> — збій <c>_vN</c> версії, повної генерації версії чи прогону по всіх.
/// </param>
public sealed record ReportViewFailure(int? TemplateVersionId, int Code, string Message, int? StableTemplateId = null);

/// <summary>
/// Останній стан генерації вʼюх <c>rpt.v_*</c> для <c>/health/ready</c> (картка
/// <c>reportviews</c>). Живе в памʼяті: старт завжди перезапускає генерацію по всіх
/// версіях, тож стан після рестарту відновлюється сам — без таблиці й міграції.
/// </summary>
/// <remarks>
/// ✎ P-4b — який успіх знімає який збій (ключів три види):
/// <list type="bullet">
/// <item>прогін по всіх (<c>Succeeded(null)</c>, обидві процедури без помилки) знімає ВСІ збої — і лише він знімає збій
/// прогону по всіх (ключ <c>null</c>): успіх одного шаблону не доводить, що вʼюхи решти шаблонів є;</item>
/// <item>успіх <c>_vN</c> версії V (або повної генерації V) — <c>Succeeded(V)</c> — знімає збій з ключем V;</item>
/// <item>успіх стабільних вʼюх шаблону T — <see cref="StableSucceeded"/> — знімає збій стабільних вʼюх T
/// (<see cref="ReportViewFailure.StableTemplateId"/> = T).</item>
/// </list>
/// </remarks>
public interface IReportViewStatus
{
    /// <summary>Фіксує збій; той самий ключ (версія / стабільні вʼюхи шаблону / прогін по всіх) перезаписується.</summary>
    /// <param name="failure">Збій.</param>
    public void Failed(ReportViewFailure failure);

    /// <summary>
    /// Успіх: прогін по всіх (<c>null</c>) скидає всі збої, по одній версії — лише її.
    /// </summary>
    /// <param name="templateVersionId">Версія виклику або <c>null</c>.</param>
    public void Succeeded(int? templateVersionId);

    /// <summary>✎ P-4b P3: стабільні вʼюхи шаблону згенеровано — знімає лише збій стабільних вʼюх цього шаблону.</summary>
    /// <param name="templateId">Шаблон.</param>
    public void StableSucceeded(int templateId)
    {
    }

    /// <summary>
    /// ✎ P-4b P2: як <see cref="Pending(int)"/>, і ще запам'ятовує версію <paramref name="templateVersionId"/> у наборі
    /// версій шаблону, що чекають <c>_vN</c>. Набір переживає злиття задач: публікація, чий payload злиття замінило, однаково
    /// отримує вʼюхи — задача забирає весь набір (<see cref="DrainWaitingVersions"/>).
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="templateVersionId">Опублікована версія.</param>
    /// <returns>Покоління позначки.</returns>
    public long Pending(int templateId, int templateVersionId) => Pending(templateId);

    /// <summary>✎ P-4b P2: забирає (і очищає) набір версій шаблону, що чекають <c>_vN</c>.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <returns>Версії за зростанням.</returns>
    public IReadOnlyList<int> DrainWaitingVersions(int templateId) => [];

    /// <summary>
    /// ✎ P-4b P2: повертає версії в набір (прогін задачі впав — «Restart» чи наступна публікація збудує їх знову).
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="templateVersionIds">Версії.</param>
    public void RequeueWaitingVersions(int templateId, IEnumerable<int> templateVersionIds)
    {
    }

    /// <summary>Поточні незакриті збої.</summary>
    /// <returns>Знімок.</returns>
    public IReadOnlyList<ReportViewFailure> Snapshot();

    /// <summary>
    /// ✎ P-4b (D-479): вʼюхи шаблону поставлено на перегенерацію у фоні (<see cref="IReportViewsJob"/>) — картка
    /// <c>reportviews</c> показує «оновлюються», доки задача не завершиться.
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <returns>Покоління позначки (зростає з кожним викликом; 0 — стан не ведеться).</returns>
    /// <remarks>
    /// ⚠ Члени P-4b мають порожню реалізацію за замовчуванням: реалізацій-записувачів у тестах кілька (лише збої), і
    /// стан «оновлюються» їм не потрібен; справжню дає <c>ReportViewStatus</c>.
    /// </remarks>
    public long Pending(int templateId) => 0;

    /// <summary>
    /// ✎ P-4b: поточне покоління позначки «оновлюються» шаблону (0 — позначки немає). Задача читає його на СТАРТІ.
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <returns>Покоління.</returns>
    public long PendingGeneration(int templateId) => 0;

    /// <summary>
    /// ✎ P-4b: задача перегенерації вʼюх шаблону завершилась (успіхом чи збоєм — збій фіксує <see cref="Failed"/>).
    /// Позначка знімається, лише якщо після <paramref name="generation"/> не було нового <see cref="Pending(int)"/> —
    /// інакше в черзі чекає злитий повтор, і картка лишається «оновлюються» до його завершення.
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="generation">Покоління, яке задача побачила на старті (<see cref="PendingGeneration"/>).</param>
    public void Settled(int templateId, long generation)
    {
    }

    /// <summary>✎ P-4b: шаблони, чиї вʼюхи зараз чекають перегенерації у фоні.</summary>
    /// <returns>Знімок.</returns>
    public IReadOnlyList<int> PendingTemplates() => [];
}
