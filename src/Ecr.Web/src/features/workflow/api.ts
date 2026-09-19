import { useQuery, type UseQueryResult } from '@tanstack/react-query';
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
