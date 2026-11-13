import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import { listDocuments } from '@/features/documents/api';
import { fetchAllProjects } from '@/features/projects/allProjects';

/** Код шаблону HSE400 (склад газу для розрахунків Land, `Build-Hse400Template`). */
export const Hse400TemplateCode = 'HSE400';

type TemplateTimeline = components['schemas']['ProjectTemplateTimelineDto'];

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
 * ✎ B78 F13 P3-2: документ HSE400 для «Відкрити HSE400» — за КОДОМ ШАБЛОНУ проєкту, а не першим збігом пошуку «HSE400».
 *
 * Проєкти (`GET /projects`, право `Document.View`) → часова шкала версій шаблону кожного
 * (`GET /projects/{id}/template-versions`, те саме право) → проєкти, де в році діє шаблон `HSE400` (спершу проєкт
 * поточного документа) → перший документ року в такому проєкті (`GET /documents?year=&projectId=`). Контракт не змінено:
 * перелік документів коду шаблону не несе, тому шаблон береться з проєкту.
 *
 * @returns Id документа; `null` — такого документа немає (або він читачеві невидимий). Відмова API — виняток.
 */
export async function findHse400Document(
  currentProjectId: number | null,
  year: number,
  currentDocumentId: number,
): Promise<number | null> {
  const projects = await fetchAllProjects();
  const ids = projects.items.map((project) => project.id);
  const ordered = currentProjectId !== null && ids.includes(currentProjectId)
    ? [currentProjectId, ...ids.filter((id) => id !== currentProjectId)]
    : ids;

  const timelines = await Promise.all(ordered.map((id) => timelineOf(id)));
  for (const [index, projectId] of ordered.entries()) {
    if (!runsHse400(timelines[index] ?? null, year)) continue;

    const page = await listDocuments({ periodKey: null, year, projectId });
    const found = page.items.find((item) => item.id !== currentDocumentId);
    if (found !== undefined) return found.id;
  }

  return null;
}
