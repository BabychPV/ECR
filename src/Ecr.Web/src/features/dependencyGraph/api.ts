import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { DependencyGraph } from './graphView';

/** Право екрана: те саме, що вимагають ендпоінти графа й зв'язок на сервері (`ManageSourceBinding`). */
export const DependencyGraphPermission = 'Calculation.ManageSourceBinding';

/** Зв'язка документа-джерела зі станом-воротами й бітами (`GET /api/v1/document-source-bindings`). */
export type SourceBinding = components['schemas']['DocumentSourceBindingDto'];

/** Відповіді перемикачів. */
type SwitchStateResponse = components['schemas']['SwitchDocumentSourceStateResponse'];
type SwitchFlagsResponse = components['schemas']['SwitchDocumentSourceFlagsResponse'];

/** Ключ графа в кеші запитів. */
export const DependencyGraphKey = ['dependency-graph'] as const;

/** Ключ зв'язок у кеші запитів. */
export const SourceBindingsKey = ['document-source-bindings'] as const;

/** Граф залежностей документів. */
export function useDependencyGraph() {
  return useQuery({
    queryKey: DependencyGraphKey,
    queryFn: () => apiFetch<DependencyGraph>('/api/v1/dependency-graph'),
  });
}

/** Зв'язки документів-джерел (для перемикачів на ребрах `fills`). */
export function useSourceBindings() {
  return useQuery({
    queryKey: SourceBindingsKey,
    queryFn: () => apiFetch<SourceBinding[]>('/api/v1/document-source-bindings'),
  });
}

/** Зміна, яку робить перемикач. */
export type BindingChange =
  | { readonly kind: 'state'; readonly bindingId: number; readonly sourceState: 'any' | 'approvedOnly' }
  | {
      readonly kind: 'flags';
      readonly bindingId: number;
      readonly readFromDocument?: boolean;
      readonly recalculateDependents?: boolean;
    };

/**
 * Перемикання стану-воріт чи бітів зв'язки.
 *
 * ⚠ Після успіху перечитуються і зв'язки, і граф: стан зв'язки граф не змінює, але ревізія графа несе час зміни зв'язки,
 * і екран не має показувати вчорашній стан поруч із сьогоднішнім.
 */
export function useSwitchBinding() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (change: BindingChange) =>
      change.kind === 'state'
        ? apiFetch<SwitchStateResponse>(`/api/v1/document-source-bindings/${String(change.bindingId)}/source-state`, {
            method: 'PUT',
            body: JSON.stringify({ sourceState: change.sourceState }),
          })
        : apiFetch<SwitchFlagsResponse>(`/api/v1/document-source-bindings/${String(change.bindingId)}/flags`, {
            method: 'PUT',
            body: JSON.stringify({
              ...(change.readFromDocument === undefined ? {} : { readFromDocument: change.readFromDocument }),
              ...(change.recalculateDependents === undefined ? {} : { recalculateDependents: change.recalculateDependents }),
            }),
          }),
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: SourceBindingsKey }),
        client.invalidateQueries({ queryKey: DependencyGraphKey }),
      ]);
    },
  });
}
