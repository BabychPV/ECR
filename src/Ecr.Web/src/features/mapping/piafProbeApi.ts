import { EcrApiError, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Наслідок пробного читання одного значення мапінгу (`ФВ-13.17`).
 *
 * ⚠ Псевдонім згенерованого типу, а не власне оголошення (`D-137`) — той самий
 * прийом, що в `piafCatalogApi.ts`: поле, яке сервер додасть завтра, має
 * з'явитися тут само.
 */
export type SourcePathProbeResult = components['schemas']['SourcePathProbeResult'];

/**
 * Пробує ОДНЕ значення мапінгу в реальному джерелі, без запису результату
 * (`ФВ-13.17`).
 *
 * ⛔ Адреса записана ПОВНІСТЮ одним літералом — той самий прийом, що в
 * `piafCatalogApi.fetchSourceCatalog`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`, і
 * зібрана з частин адреса для нього не існує.
 */
export function probeSourcePath(dataSourceId: number, path: string): Promise<SourcePathProbeResult> {
  return apiFetch<SourcePathProbeResult>(`/api/v1/data-sources/${dataSourceId}/probe`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  });
}

/** «Шляху немає в каталозі джерела» — той самий код, що в `ErrorCodes.cs`. */
export const ProbePathNotFoundCode = 'ECR-INT-0404';

/** `messageKey`, яким сервер позначає саме цю відмову (а не «джерела немає взагалі»). */
export const ProbePathNotFoundKey = 'err.ECR-INT-0404.sourcePathNotFound';

/**
 * Чи це відмова «шляху немає в каталозі джерела» — сервер додав до неї
 * підказки найближчих імен (`suggestions`, до 5).
 *
 * ⚠ Перевіряється `messageKey`, а не лише код: `ECR-INT-0404` — той самий
 * код, що й «джерела даних не існує» (`DataSourceHandlers.NotFound`), і
 * розрізняються вони саме ключем.
 */
export function isProbePathNotFound(error: unknown): boolean {
  return (
    error instanceof EcrApiError &&
    error.problem.errorCode === ProbePathNotFoundCode &&
    error.problem.extensions2?.['messageKey'] === ProbePathNotFoundKey
  );
}

/**
 * Підказки найближчих імен, якщо відмова саме про відсутній шлях; інакше
 * порожній перелік.
 *
 * ⚠ `L10`: порожній перелік — очікуваний стан (шлях геть не схожий на
 * жодне ім'я цього рівня), а не ознака збою. Блок підказок ховає САМЕ це
 * порожнє значення — рішення належить показу, не цій функції.
 */
export function probeSuggestions(error: unknown): readonly string[] {
  if (!isProbePathNotFound(error)) return [];

  const raw = (error as EcrApiError).problem.extensions2?.['suggestions'];

  return Array.isArray(raw) ? raw.filter((item): item is string => typeof item === 'string') : [];
}
