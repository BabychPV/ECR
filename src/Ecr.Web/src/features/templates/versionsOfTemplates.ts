import type { QueryClient } from '@tanstack/react-query';
import type { CursorPage } from '@/api/fetchAllPages';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateVersionSummary } from '@/api/types';
import { templateVersionsQuery } from './templateQueries';

/*
 * ⚠ Окремим модулем від 	emplateQueries.ts (рецензія L10, M3): пул потрібен лише діалогу створення проєкту, а
 * 	emplateQueries статично тягне TemplateVersionPage на межі бюджету D-132 — кожен байт там на рахунку.
 */

/** Скільки шаблонів читаються одночасно в `versionsOfTemplatesQuery`. */
export const VersionsConcurrency = 4;

/**
 * Версії КІЛЬКОХ шаблонів — по шаблону, але не більше `VersionsConcurrency` запитів одночасно (рецензія L10, L5).
 *
 * ⛔ Діалог створення проєкту ставив `useQueries` на КОЖЕН шаблон переліку — одночасно; із переліком на всі сторінки
 * (PSS-19c) це сотні запитів в одну мить. Пакетний `GET /templates/versions?ids=` тут не годиться: сервер віддає в
 * ньому лише перші 100 версій шаблону (`PerTemplateVersionLimit`) від найстаріших — найновіші опубліковані, які й
 * обирають для нового проєкту, на довгій історії мовчки зникли б. Тож — той самий повний перелік версій на шаблон
 * (`templateVersionsQuery`, його ключ і кеш), лише пулом із чотирьох.
 *
 * ⚠ Відповідь — масив у порядку `templateIds`. Кожен шаблон читається через `fetchQuery` під своїм ключем
 * `versionsOf(id)`: картка шаблону й крихти беруть звідти ж, без другого запиту.
 */
export function versionsOfTemplatesQuery(client: QueryClient, templateIds: readonly number[]) {
  return {
    queryKey: [...queryKeys.templates.allVersionsOf(), 'many', ...templateIds] as const,
    queryFn: async ({ signal }: { signal?: AbortSignal }): Promise<CursorPage<TemplateVersionSummary>[]> => {
      const results: CursorPage<TemplateVersionSummary>[] = new Array(templateIds.length);
      let next = 0;

      const worker = async (): Promise<void> => {
        while (next < templateIds.length) {
          signal?.throwIfAborted();
          const index = next;
          next += 1;
          results[index] = await client.fetchQuery(templateVersionsQuery(templateIds[index]));
        }
      };

      await Promise.all(Array.from({ length: Math.min(VersionsConcurrency, templateIds.length) }, worker));

      return results;
    },
  } as const;
}
