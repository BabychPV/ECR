import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { DocumentCalcStatus } from './calcStatusModel';

/** Поки задача в черзі чи рахується — кожні 3 с (стан сервера: переживає перезавантаження, видно чужі й нічні задачі). */
export const CalcStatusPollMs = 3_000;

/**
 * Поки результати застарілі — раз на 15 с: тихий автоперерахунок після правки (Q2) ставить задачу із затримкою ~20 с,
 * і чип має показати «Queued / Recalculating» без дії людини.
 */
export const CalcStatusStalePollMs = 15_000;

/**
 * Ключ — ПІД префіксом картки `['document', id, період]`: перерахунок на завершенні вже скидає цей префікс
 * (`SheetActions`), тож чип оновлюється без окремої проводки.
 */
export const calcStatusKey = (documentId: number, periodKey: number) =>
  ['document', documentId, periodKey, 'calc-status'] as const;

/** `GET /documents/{id}/calc-status` — без кешу браузера (`no-store`, як і сервер). */
export function getCalcStatus(documentId: number, periodKey: number): Promise<DocumentCalcStatus> {
  const query = new URLSearchParams({ periodKey: String(periodKey) });

  return apiFetch<DocumentCalcStatus>(`/api/v1/documents/${String(documentId)}/calc-status?${query.toString()}`, { cache: 'no-store' });
}

/** ✎ DOCUX L4: стан розрахунку документа із сервера; опитується, поки є задача або результати застарілі. */
export function useDocumentCalcStatus(documentId: number, periodKey: number, localActive: boolean): DocumentCalcStatus | undefined {
  const query = useQuery({
    queryKey: calcStatusKey(documentId, periodKey),
    queryFn: () => getCalcStatus(documentId, periodKey),
    retry: false,
    refetchInterval: (state) => {
      const data = validCalcStatus(state.state.data);
      if (localActive || (data?.activeJobs.length ?? 0) > 0) return CalcStatusPollMs;
      return data?.resultsStale === true ? CalcStatusStalePollMs : false;
    },
  });

  return validCalcStatus(query.data);
}

/** Відповідь без очікуваної форми (старший сервер, проксі, порожнє тіло) — «не знаємо»: працює запас із картки. */
function validCalcStatus(data: DocumentCalcStatus | null | undefined): DocumentCalcStatus | undefined {
  return data !== null && data !== undefined && Array.isArray(data.activeJobs) && Array.isArray(data.staleMonths) ? data : undefined;
}
