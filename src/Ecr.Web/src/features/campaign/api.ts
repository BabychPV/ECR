import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Огляд кампанії звітності за період (`BE-22`).
 *
 * ⛔ Перелік НЕ звужується проєктами, на які в користувача є грант: доступ дає
 * окреме право `Report.ViewCampaign` (рішення людини `Q15-07`). Тому клієнт і
 * не передає жодного фільтра проєктів — його тут просто немає.
 *
 * ⚠ Тип — зі ЗГЕНЕРОВАНОЇ схеми, не рукописний: саме рукописні типи відповідей
 * і дали `A7-34`/`A7-35`/`A7-36`.
 */
export type CampaignSummary = components['schemas']['CampaignSummaryResponse'];

/** Рядок огляду: один проєкт і його лічильники. */
export type CampaignProject = components['schemas']['CampaignProjectSummary'];

/**
 * Підсумки кампанії по ВСІХ проєктах періоду, без стелі переліку.
 *
 * ⛔ Не сума `projects`: перелік обрізано стелею, і сума по ньому читалася б як
 * стан кампанії.
 */
export type CampaignTotals = components['schemas']['CampaignTotals'];

/**
 * «Хто затримує кампанію» — класифікація проєкту, яку рахує СЕРВЕР (одне
 * джерело правди для рядків і підсумків): `Done` — усе затверджено і є зріз;
 * `Overdue` — строк подання минув; `AtRisk` — до останнього дня ≤ N діб;
 * `InProgress` — решта.
 */
export type CampaignProgress = components['schemas']['CampaignProgress'];

/**
 * Зведення кампанії за період.
 *
 * ⛔ Адреса записана ПОВНІСТЮ і поруч із `apiFetch`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта саме
 * літерал `/api/v1/…` разом із функцією поруч, і винесений префікс зробив би
 * дію «недосяжною з інтерфейсу».
 */
export function campaignSummary(periodKey: number): Promise<CampaignSummary> {
  return apiFetch<CampaignSummary>(`/api/v1/campaign/summary?periodKey=${String(periodKey)}`);
}

/**
 * Огляд кампанії.
 *
 * ⚠ Без періоду запит НЕ йде: стан документа поза періодом не визначений
 * (`D-93`), і сервер відповів би `422`.
 */
export function useCampaignSummary(periodKey: number | null): UseQueryResult<CampaignSummary> {
  return useQuery({
    queryKey: ['campaign', 'summary', periodKey],
    queryFn: () => campaignSummary(periodKey ?? 0),
    enabled: periodKey !== null,
  });
}

/**
 * Чи бачить користувач кампанію цілком.
 *
 * ⛔ Відповідь сервера обрізана стелею переліку, і `totalProjects` — єдине, що
 * про це каже. Екран, який мовчки показує 200 рядків із 300, відповідає на
 * питання «хто затримує кампанію» неправдою: відстаючий може бути саме в тій
 * сотні, якої не видно.
 */
export function isCampaignTruncated(summary: CampaignSummary): boolean {
  return summary.totalProjects > summary.projects.length;
}
