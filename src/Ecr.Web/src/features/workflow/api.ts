import { keepPreviousData, useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Подія журналу переходів стану аркуша (`BE-11b`); тип — зі згенерованої схеми. */
export type WorkflowEvent = components['schemas']['WorkflowEventDto'];

/**
 * Журнал переходів документа за період, найновіші перші.
 *
 * ⚠ Ключ починається з `['document', id, period]` навмисно: `SheetActions`
 * після кожної дії скидає саме цей префікс, тож щойно зроблене подання
 * з'являється в розгорнутій історії без окремого зв'язку між компонентами.
 *
 * @param enabled запит іде лише тоді, коли блок розгорнуто.
 */
export function useWorkflowHistory(
  documentId: number,
  periodKey: number,
  enabled: boolean,
): UseQueryResult<WorkflowEvent[]> {
  return useQuery({
    queryKey: ['document', documentId, periodKey, 'workflow-history'],
    queryFn: () =>
      apiFetch<WorkflowEvent[]>(
        `/api/v1/documents/${String(documentId)}/workflow/history?periodKey=${String(periodKey)}`,
      ),
    enabled,
  });
}

/** Тіло відкликання аркуша (`BE-31`). */
export type RecallSheetRequest = components['schemas']['RecallSheetRequest'];

/**
 * Чи може ПОТОЧНИЙ користувач відкликати аркуш: автор подання, рівень `Submit`,
 * жоден крок маршруту не підписано. Рішення сервера — клієнт його не вгадує.
 *
 * ⚠ Ключ під префіксом `['document', id, period]` — з тієї ж причини, що й
 * історія: будь-яка дія робочого процесу скидає і цю відповідь.
 */
export function useRecallAvailability(
  documentId: number,
  sheetDefId: number,
  periodKey: number,
  enabled: boolean,
): UseQueryResult<components['schemas']['RecallAvailabilityDto']> {
  return useQuery({
    queryKey: ['document', documentId, periodKey, 'recall', sheetDefId],
    queryFn: () =>
      apiFetch<components['schemas']['RecallAvailabilityDto']>(
        `/api/v1/documents/${String(documentId)}/recall?sheetDefId=${String(sheetDefId)}&periodKey=${String(periodKey)}`,
      ),
    enabled,
  });
}

/** Чи може поточний користувач правити значення поданого аркуша (D-442/D-445); тип — зі згенерованої схеми. */
export type ApproverEditAvailability = components['schemas']['ApproverEditAvailabilityDto'];

/**
 * ✎ борг D-445: чи правка ПОДАНОГО аркуша цим користувачем пройде на сервері — лише погоджувач
 * поточного кроку маршруту. Рішення сервера тим самим правилом, що й запис; клієнт його не вгадує (роль кроку,
 * маршрут, грант знає лише сервер). Відповідь — лише для форми екрана: запис однаково перевіряє правило.
 *
 * ⚠ Ключ під префіксом `['document', id, period]` — як `useRecallAvailability`: погодження кроку переводить
 * чергу далі, і `SheetActions` після кожної дії скидає саме цей префікс.
 *
 * @param enabled запит іде лише для поданого аркуша (`editsDeferToServer`).
 */
export function useApproverEditAvailability(
  documentId: number,
  sheetDefId: number,
  periodKey: number,
  enabled: boolean,
): UseQueryResult<ApproverEditAvailability> {
  return useQuery({
    queryKey: ['document', documentId, periodKey, 'approver-edit', sheetDefId],
    queryFn: () =>
      apiFetch<ApproverEditAvailability>(
        `/api/v1/documents/${String(documentId)}/approver-edit?sheetDefId=${String(sheetDefId)}&periodKey=${String(periodKey)}`,
      ),
    enabled,
  });
}

/** Що подасть «Подати» на аркуші (D-441); тип — зі згенерованої схеми. */
export type SubmissionTarget = components['schemas']['SubmissionTargetDto'];

/** Стан аркуша за період місячної колонки — `Submitted`/`Approved` позначаються в заголовку. */
export type MonthColumnStatus = components['schemas']['MonthColumnStatusDto'];

/**
 * Ключ відповіді `GET …/submission-target` для аркуша й відкритого на екрані періоду.
 *
 * ⚠ Окремий префікс, а не `['document', id, period]`: подання іде в ІНШИЙ період, ніж відкритий (D-441),
 * і скидання ключа відкритого періоду цю відповідь не оновило б. `SheetActions` скидає
 * `['submission-target', id]` після кожної дії робочого процесу.
 */
export function submissionTargetKey(documentId: number, sheetDefId: number, periodKey: number): readonly unknown[] {
  return ['submission-target', documentId, sheetDefId, periodKey];
}

export function fetchSubmissionTarget(documentId: number, sheetDefId: number, periodKey: number): Promise<SubmissionTarget> {
  return apiFetch<SubmissionTarget>(
    `/api/v1/documents/${String(documentId)}/submission-target?sheetDefId=${String(sheetDefId)}&periodKey=${String(periodKey)}`,
  );
}

/**
 * Цільовий період подання (D-441): найраніший неподаний `Open`/`Grace`, наступний, відкриті періоди для вибору
 * і статуси місячних колонок. Рішення — сервера: стан періоду для подання ЕФЕКТИВНИЙ (за датами).
 *
 * ✎ D-441 «миготіння скелета»: при зміні аркуша чи періоду (новий ключ) лишається відповідь ПОПЕРЕДНЬОГО ключа
 * (`keepPreviousData`), щоб рядок дій не підмінявся скелетом на один запит. ⛔ Ця відповідь — лише для ФОРМИ рядка:
 * `isPlaceholderData === true` означає «ціль ще чужого аркуша/періоду», і подавати за нею не можна —
 * `useSheetActions` тоді вимикає «Подати» (`submit.checking`) і бере ціль лише з непідставних даних.
 */
export function useSubmissionTarget(
  documentId: number,
  sheetDefId: number,
  periodKey: number,
): UseQueryResult<SubmissionTarget> {
  return useQuery({
    queryKey: submissionTargetKey(documentId, sheetDefId, periodKey),
    queryFn: () => fetchSubmissionTarget(documentId, sheetDefId, periodKey),
    placeholderData: keepPreviousData,
  });
}
