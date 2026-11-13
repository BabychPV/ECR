import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import { listDocuments } from '@/features/documents/api';
import { fetchAllProjects } from '@/features/projects/allProjects';

/** Код шаблону HSE400 (склад газу для розрахунків Land, `Build-Hse400Template`). */
export const Hse400TemplateCode = 'HSE400';

type TemplateTimeline = components['schemas']['ProjectTemplateTimelineDto'];
type DocumentSummary = components['schemas']['DocumentSummary'];

/** ✎ B93 (b): скільки шкал проєктів запитується одночасно (порція до першого збігу, а не всі N на клік). */
const TimelineBatchSize = 4;

/**
 * Чи діє в проєкті шаблон HSE400 у році `year` (часова шкала версій шаблону проєкту).
 *
 * ✎ B93 (a): дивиться версію, ЧИННУ в році, — останній елемент шкали з `fromPeriodKey` ≤ кінця року, — а не «будь-коли
 * була HSE400»: пізніший шаблон (напр. Land), що замінив HSE400 до кінця року, означає, що документ року — не HSE400.
 */
function runsHse400(timeline: TemplateTimeline | null, year: number): boolean {
  if (timeline === null) return false;
  const yearEnd = year * 100 + 99;

  let effective: TemplateTimeline['items'][number] | null = null;
  for (const item of timeline.items) {
    if (item.fromPeriodKey <= yearEnd && (effective === null || item.fromPeriodKey >= effective.fromPeriodKey)) {
      effective = item;
    }
  }

  return (effective?.templateCode ?? '').toUpperCase() === Hse400TemplateCode;
}

/** Шкала версій проєкту; невидимий/недоступний проєкт (403/404) — `null`, інша відмова — далі. */
async function timelineOf(projectId: number): Promise<TemplateTimeline | null> {
  try {
    return await apiFetch<TemplateTimeline>(`/api/v1/projects/${String(projectId)}/template-versions`);
  } catch (error) {
    if (error instanceof EcrApiError && (error.problem.status === 403 || error.problem.status === 404)) return null;
    throw error;
  }
}

/**
 * ✎ B93 (c): який документ року відкрити в проєкті, коли їх кілька, — явне правило, а не порядок переліку:
 * 1) не сам поточний документ; 2) спершу документ, що має період панелі (`periodStates` містить `periodKey` — перехід
 * відкриває саме цей період); 3) далі найменший Id (найстаріший документ року). Проєкт поточного документа перевіряється
 * першим ще до цього правила (див. `findHse400Document`), рік — фільтр сервера `year`.
 */
function pickDocument(
  items: readonly Pick<DocumentSummary, 'id' | 'periodStates'>[],
  currentDocumentId: number,
  periodKey: number | null,
): number | null {
  const candidates = items.filter((item) => item.id !== currentDocumentId);
  if (candidates.length === 0) return null;

  const withPeriod = periodKey === null
    ? []
    : candidates.filter((item) => (item.periodStates ?? []).some((state) => state.periodKey === periodKey));
  const pool = withPeriod.length > 0 ? withPeriod : candidates;

  return Math.min(...pool.map((item) => item.id));
}

/** Документ HSE400 у проєкті за рік або `null` (шаблон не HSE400 чи документів немає). */
async function documentIn(
  projectId: number,
  timeline: TemplateTimeline | null,
  year: number,
  currentDocumentId: number,
  periodKey: number | null,
): Promise<number | null> {
  if (!runsHse400(timeline, year)) return null;

  const page = await listDocuments({ periodKey: null, year, projectId });
  return pickDocument(page.items, currentDocumentId, periodKey);
}

/**
 * ✎ B78 F13 P3-2: документ HSE400 для «Відкрити HSE400» — за КОДОМ ШАБЛОНУ проєкту, а не першим збігом пошуку «HSE400».
 *
 * Шаблон, чинний у році (`GET /projects/{id}/template-versions`, право `Document.View`), → документ року в такому
 * проєкті (`GET /documents?year=&projectId=`, правило вибору — `pickDocument`). Контракт не змінено: перелік документів
 * коду шаблону не несе, тому шаблон береться з проєкту.
 *
 * ✎ B93 (b): спершу ЛИШЕ проєкт поточного документа (одна шкала, без переліку проєктів); не HSE400 — решта видимих
 * проєктів (`GET /projects`) порціями по {@link TimelineBatchSize} до першого збігу, а не N паралельних запитів на клік.
 * Один запит на сервер потребував би фільтра документів за кодом шаблону — це зміна контракту (поза цією lane).
 *
 * @param periodKey Період панелі — перевага документу з цим періодом (`pickDocument`); `null` — без переваги.
 * @returns Id документа; `null` — такого документа немає (або він читачеві невидимий). Відмова API — виняток.
 */
export async function findHse400Document(
  currentProjectId: number | null,
  year: number,
  currentDocumentId: number,
  periodKey: number | null = null,
): Promise<number | null> {
  if (currentProjectId !== null) {
    const own = await documentIn(currentProjectId, await timelineOf(currentProjectId), year, currentDocumentId, periodKey);
    if (own !== null) return own;
  }

  const projects = await fetchAllProjects();
  const others = projects.items.map((project) => project.id).filter((id) => id !== currentProjectId);

  for (let start = 0; start < others.length; start += TimelineBatchSize) {
    const batch = others.slice(start, start + TimelineBatchSize);
    const timelines = await Promise.all(batch.map((id) => timelineOf(id)));
    for (const [index, projectId] of batch.entries()) {
      const found = await documentIn(projectId, timelines[index] ?? null, year, currentDocumentId, periodKey);
      if (found !== null) return found;
    }
  }

  return null;
}
