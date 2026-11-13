import { useInfiniteQuery, type InfiniteData, type UseInfiniteQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CellChangePage } from '@/api/types';
import { nextCursorOf } from '@/features/common/infiniteList/mergePages';
import {
  cellChangesQuery,
  securityEventsQuery,
  structureChangesQuery,
  workflowEventsQuery,
  type SecurityEventPage,
  type StructureChangePage,
  type WorkflowEventPage,
} from './api';

/*
 * ✎ W-3 (рішення PO 11.11): журнали аудиту — нескінченною прокруткою. Окремий модуль, а не `api.ts`: `api.ts` статично
 * в графі `DocumentPage` (`DocumentInspector` → `useCellChanges`), і `useInfiniteQuery` важив би на бюджеті D-132
 * сторінки, якій журналу не треба (прийом `fileName.ts`, `api.ts:8-10`).
 * ⛔ Адреса — літералом поруч із `apiFetch` (сторож `EndpointCoverageTests`, `api.ts:12-17`).
 * ⚠ Ключі — `[…, 'pages', запит]`: `DocumentInspector` читає `['audit-cells', запит]` звичайним `useQuery`, і спільний
 * ключ дав би запис кешу двох форм.
 * ⚠ `refetchOnWindowFocus: false`: журнал — історія; повернення у вкладку перечитувало б ВСІ довантажені сторінки підряд.
 */

type CellChangeFilter = Omit<Parameters<typeof cellChangesQuery>[0], 'cursor'>;

/** Журнал змін комірок сторінками (W-3b). */
export function useCellChangePages(
  filter: CellChangeFilter,
  enabled = true,
): UseInfiniteQueryResult<InfiniteData<CellChangePage, string | null>> {
  const query = cellChangesQuery(filter);

  return useInfiniteQuery({
    enabled,
    queryKey: ['audit-cells', 'pages', query],
    queryFn: ({ pageParam }) => {
      const params = cellChangesQuery({ ...filter, cursor: pageParam });
      return apiFetch<CellChangePage>(`/api/v1/audit/cells?${params}`);
    },
    initialPageParam: null as string | null,
    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
    refetchOnWindowFocus: false,
  });
}

type SecurityEventFilter = Omit<Parameters<typeof securityEventsQuery>[0], 'cursor'>;

/** Журнал подій безпеки сторінками (W-3d). */
export function useSecurityEventPages(
  filter: SecurityEventFilter,
): UseInfiniteQueryResult<InfiniteData<SecurityEventPage, string | null>> {
  const query = securityEventsQuery(filter);

  return useInfiniteQuery({
    queryKey: ['audit-security', 'pages', query],
    queryFn: ({ pageParam }) => {
      const params = securityEventsQuery({ ...filter, cursor: pageParam });
      return apiFetch<SecurityEventPage>(`/api/v1/audit/security?${params}`);
    },
    initialPageParam: null as string | null,
    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
    refetchOnWindowFocus: false,
  });
}

type StructureChangeFilter = Omit<Parameters<typeof structureChangesQuery>[0], 'cursor'>;

/** Журнал структурних змін сторінками (W-3e). */
export function useStructureChangePages(
  filter: StructureChangeFilter,
): UseInfiniteQueryResult<InfiniteData<StructureChangePage, string | null>> {
  const query = structureChangesQuery(filter);

  return useInfiniteQuery({
    queryKey: ['audit-structure', 'pages', query],
    queryFn: ({ pageParam }) => {
      const params = structureChangesQuery({ ...filter, cursor: pageParam });
      return apiFetch<StructureChangePage>(`/api/v1/audit/structure?${params}`);
    },
    initialPageParam: null as string | null,
    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
    refetchOnWindowFocus: false,
  });
}

type WorkflowEventFilter = Omit<Parameters<typeof workflowEventsQuery>[0], 'cursor'>;

/** Журнал подій погодження сторінками (W-3f). */
export function useWorkflowEventPages(
  filter: WorkflowEventFilter,
): UseInfiniteQueryResult<InfiniteData<WorkflowEventPage, string | null>> {
  const query = workflowEventsQuery(filter);

  return useInfiniteQuery({
    queryKey: ['audit-workflow', 'pages', query],
    queryFn: ({ pageParam }) => {
      const params = workflowEventsQuery({ ...filter, cursor: pageParam });
      return apiFetch<WorkflowEventPage>(`/api/v1/audit/workflow?${params}`);
    },
    initialPageParam: null as string | null,
    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
    refetchOnWindowFocus: false,
  });
}
