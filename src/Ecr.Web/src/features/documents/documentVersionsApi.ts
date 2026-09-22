import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Версії документа й різниця між ними (`ФВ-5.22`).
 *
 * ⛔ «Версія» — це ЗРІЗ ПОДАННЯ (`calc.SubmissionSnapshot`), інших збережених
 * версій документа не існує. Тому перелік порожній не тому, що щось зламалося,
 * а тому, що документ ще жодного разу не подавали, — і сказати це треба словами
 * (`DocumentVersionCompare`), а не порожнім списком.
 *
 * ⚠ Типи — зі згенерованої схеми (`D-137`). Рукописна копія форми відповіді
 * розійшлася б із сервером мовчки: поле, якого сервер не віддає, дало б
 * `undefined` там, де компілятор обіцяв рядок.
 */
export type DocumentVersion = components['schemas']['DocumentVersionDto'];
export type DocumentCompare = components['schemas']['DocumentCompareDto'];
export type CellChange = components['schemas']['CellChangeDto'];
export type RowChange = components['schemas']['RowChangeDto'];

/**
 * Значення `to`, що означає «поточний стан документа», а не збережений зріз.
 *
 * ⛔ Рядок, а не `null` чи `0`: так його приймає сервер
 * (`?to=current`), і будь-яке інше значення він відхиляє
 * `ECR-DOC-0422 .compareVersion`. Друге уявлення про те саме («порожньо
 * означає поточний») розійшлося б із сервером на першій же зміні.
 */
export const CurrentState = 'current';

/** З чим порівнюємо: збережена версія або поточний стан. */
export type CompareTarget = number | typeof CurrentState;

/**
 * Адреса переліку версій за період.
 *
 * ⛔ Параметр рівно один — `periodKey`. Стелю (200) ставить СЕРВЕР, і клієнт не
 * має права ані просити більше, ані просити менше: `?limit=…`, доданий тут,
 * мовчки змінив би поведінку, яку описує контракт, а перевірити її можна було б
 * лише на сервері.
 *
 * ⚠ Окрема функція, а не рядок на місці виклику, саме щоб тест міг перевірити
 * адресу ПОБУКВЕНО — інакше «зайвий параметр» ловився б лише оком рецензента.
 */
export function documentVersionsUrl(documentId: number, periodKey: number): string {
  return `/api/v1/documents/${String(documentId)}/versions?periodKey=${String(periodKey)}`;
}

/**
 * Адреса різниці двох версій.
 *
 * ⚠ Та сама причина, що й вище: стелю кожного переліку (1000 записів) і
 * прапорець `truncated` рахує сервер. Клієнт лише називає дві версії.
 */
export function documentCompareUrl(
  documentId: number,
  from: number,
  to: CompareTarget,
): string {
  return `/api/v1/documents/${String(documentId)}/compare?from=${String(from)}&to=${String(to)}`;
}

/**
 * Зрізи подання документа за період, найновіші перші (стеля 200 — на сервері).
 *
 * ⚠ Ключ під префіксом `['document', id, period]` — як у журналу переходів
 * (`features/workflow/api.ts`): будь-яка дія робочого процесу скидає цей
 * префікс, тож щойно зроблене подання з'являється в переліку без окремого
 * зв'язку між компонентами.
 *
 * @param enabled запит іде лише тоді, коли блок розгорнуто (`L2`).
 */
export function useDocumentVersions(
  documentId: number,
  periodKey: number,
  enabled: boolean,
): UseQueryResult<DocumentVersion[]> {
  return useQuery({
    queryKey: ['document', documentId, periodKey, 'versions'],
    queryFn: () => apiFetch<DocumentVersion[]>(documentVersionsUrl(documentId, periodKey)),
    enabled,
  });
}

/**
 * Різниця двох версій.
 *
 * ⛔ `from` окремим станом, а не «перша з переліку»: порівняння запускає людина,
 * і мовчазний вибір за неї означав би, що екран показує різницю, про яку ніхто
 * не просив.
 *
 * @param from версія-джерело; `null` — порівняння ще не замовляли.
 */
export function useDocumentCompare(
  documentId: number,
  from: number | null,
  to: CompareTarget,
): UseQueryResult<DocumentCompare> {
  return useQuery({
    queryKey: ['document', documentId, 'compare', from, to],
    queryFn: () => apiFetch<DocumentCompare>(documentCompareUrl(documentId, from ?? 0, to)),
    enabled: from !== null,

    /*
     * ⛔ Повтор вимкнено навмисно. Відмови цього ендпоінта — це `404`
     * (`ECR-DOC-0404`, версії немає) і `422` (`ECR-DOC-0422`, версії різних
     * періодів або нерозбірливе значення); жодна з них від повтору не
     * зміниться, а три спроби перетворюють миттєву відповідь на очікування,
     * після якого людина бачить той самий текст.
     */
    retry: false,
  });
}
