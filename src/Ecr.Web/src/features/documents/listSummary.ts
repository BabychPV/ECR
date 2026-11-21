import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/*
 * Зведення переліку документів (`BE-09`) — окремим модулем від `./api` (бюджет `D-132`): його бере бейдж меню
 * оболонки (`useStaleResultsNavCount`), тобто вхідний чанк КОЖНОГО маршруту, а решта API документів йому не потрібна.
 */

/** Лічильники над переліком документів (`BE-09`); тип — зі згенерованої схеми. */
type DocumentListSummary = components['schemas']['DocumentListSummaryResponse'];

/**
 * Зведення переліку за період (і, за потреби, проєкт).
 *
 * ⚠ Адреса — через `URLSearchParams` у шаблоні з одним літералом `/api/v1/documents/summary?…`, як і в
 * `listDocuments`: сторож споживачів бере весь вміст між лапками.
 */
function documentListSummary(
  periodKey: number | null,
  projectId: number | null,
  year: number | null,
): Promise<DocumentListSummary> {
  // ✎ Period clarity: місяць важить більше; без нього — зведення за рік (`year`, те саме правило, що фільтр стану).
  const query = new URLSearchParams(
    periodKey !== null ? { periodKey: String(periodKey) } : { year: String(year ?? 0) },
  );
  if (projectId !== null) query.set('projectId', String(projectId));

  return apiFetch<DocumentListSummary>(`/api/v1/documents/summary?${query.toString()}`);
}

/**
 * Зведення переліку документів.
 *
 * ⚠ Без періоду й без року запит НЕ йде: стан документа поза періодом не визначений
 * (`D-93`), і сервер на це відповів би `422`. ✎ Period clarity: з роком — зведення за рік.
 *
 * ⚠ Ключ починається з `'documents'` навмисно: усе, що інвалідовує перелік
 * (створення документа), тим самим префіксом оновлює і смугу над ним.
 *
 * ⛔ Y7-02: `projectId` — той самий фільтр, що й у переліку (`?projectId=`). Сервер уміє зводити за проєктом
 * (`GET /documents/summary?projectId=`), а клієнт його не передавав: за вибраного проєкту смуга над таблицею
 * рахувала ВСІ видимі документи, тобто числа розходились із рядками під нею. Без проєкту ключ лишається
 * `['documents','summary',periodKey]` — на нього спираються інвалідації (`refreshStaleness`).
 */
export function useDocumentListSummary(
  periodKey: number | null,
  policy?: DocumentListSummaryPolicy,
  projectId: number | null = null,
  year: number | null = null,
): UseQueryResult<DocumentListSummary> {
  // ✎ Period clarity: без місяця, але з роком — зведення за рік (окремий ключ, той самий префікс `'documents'`).
  const yearScope = periodKey === null && year !== null;
  const monthKey = projectId === null ? ['documents', 'summary', periodKey] : ['documents', 'summary', periodKey, projectId];

  return useQuery({
    queryKey: yearScope ? ['documents', 'summary', 'year', year, projectId] : monthKey,
    queryFn: () => documentListSummary(periodKey, projectId, year),
    enabled: periodKey !== null || year !== null,
    ...policy,
  });
}

/**
 * Власна політика свіжості спостерігача зведення (AN-108 / P2-01).
 *
 * ⚠ Бейдж меню — підказка, а не стан, який треба тримати секундно точним: зведення — найдорожчий запит
 * переліку (агрегат по всіх документах + лічильники застарілості по журналу правок).
 */
export interface DocumentListSummaryPolicy {
  readonly staleTime?: number;
  readonly refetchOnWindowFocus?: boolean;
}

