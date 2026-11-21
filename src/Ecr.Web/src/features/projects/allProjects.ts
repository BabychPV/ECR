import { queryKeys } from '@/api/queryKeys';
import type { PagedProjects } from '@/api/types';

/**
 * УСІ видимі проєкти — для вибору проєкту (`X-07`).
 *
 * ⛔ Екрани просили `?limit=200` і показували те, що прийшло: 201-й проєкт
 * просто не існував для вибору, і ніщо на екрані не казало, що перелік
 * обрізано. Відповідь курсорна, тож тут — прохід сторінками до кінця
 * (`fetchAllPages`: 500 на сторінку, запобіжник 20 сторінок — PSS-19c зробив
 * цей прохід спільним для всіх переліків).
 *
 * ⚠ Форма відповіді та сама (`PagedProjects`), і ключ кешу той самий
 * (`['projects']`): сусідні екрани, що читають його, отримують той самий об'єкт.
 * `nextCursor` не `null` лише тоді, коли спрацював запобіжник — тобто перелік
 * справді неповний, і викликач може це сказати.
 *
 * ⚠ `fetchAllPages` — ледачим `import()` (рецензія L10, M3): цей модуль у вхідному чанку застосунку
 * (`breadcrumbResolvers`), і статичний імпорт поклав би прохід сторінками на рахунок КОЖНОГО маршруту (`D-132`).
 */
export async function fetchAllProjects(context: { signal?: AbortSignal } = {}): Promise<PagedProjects> {
  const { fetchAllPages } = await import('@/api/fetchAllPages');

  return fetchAllPages<PagedProjects['items'][number]>('/api/v1/projects', { signal: context.signal });
}

/**
 * Єдиний опис запиту «усі проєкти» (AN-91 / L8-10): ключ + `queryFn` разом, щоб жоден екран не клав під
 * `['projects']` власну форму (обрізаний `?limit=200` тощо). Вживати як `useQuery({ ...projectsQuery, enabled })`.
 *
 * ⚠ `DocumentPage` переліку проєктів більше не читає (PSS-19a: стан проєкту — з картки документа); сторож
 * `projectsQueryKey.guard.test.ts` вимагає, щоб кожен, хто бере ключ `['projects']`, брав і `fetchAllProjects`.
 */
export const projectsQuery = {
  queryKey: queryKeys.projects.all(),
  queryFn: fetchAllProjects,
} as const;
