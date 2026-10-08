import { useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { DocumentSummary, PeriodCalendarDto } from '@/api/types';
import { listDocuments } from '@/features/documents/api';
import { openPeriodKeys } from '@/features/documents/newDocumentPeriod';
import { fetchAllProjects } from '@/features/projects/allProjects';

/** Скільки проєктів опитувати за календарями: далі - десятки запитів заради блоку «My tasks» не виправдані. */
const MaxProjects = 20;

/** Скільки НАЙНОВІШИХ відкритих періодів перевіряти: у закритому періоді перерахувати вже не можна. */
const MaxPeriods = 3;

export interface StaleDocument {
  readonly document: DocumentSummary;
  readonly periodKey: number;
}

export interface StaleDocumentsState {
  readonly items: readonly StaleDocument[];
  readonly isPending: boolean;
  readonly error: unknown;
  readonly refetch: () => void;
}

/**
 * Документи користувача, де результати методологій застаріли після ЙОГО правок (`resultsStale=true&staleBy=me`).
 *
 * ⛔ Сервер вимагає період (`resultsStaleNeedsPeriod`: застарілість належить періоду), а «поточного періоду
 * користувача» немає - тому береться до `MaxPeriods` найновіших ВІДКРИТИХ періодів з календарів проєктів
 * (ключі спільні для всіх проєктів, як у `DocumentsPage`). Правки в закритому періоді не перераховуються.
 *
 * ⚠ `enabled` - ззовні (шухляда відкрита): у закритому стані жодного запиту, а при відкритті - свіжі дані
 * (`staleTime: 0`), бо стан змінюється перерахунком в іншій вкладці.
 */
export function useStaleDocuments(enabled: boolean): StaleDocumentsState {
  const projects = useQuery({ queryKey: ['projects'], queryFn: fetchAllProjects, enabled });
  const projectList = (projects.data?.items ?? []).slice(0, MaxProjects);

  const calendars = useQueries({
    queries: projectList.map((project) => ({
      queryKey: ['periods', project.id],
      queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(project.id)}/periods`),
      enabled,
    })),
  });
  const calendarsReady =
    projects.data !== undefined && calendars.every((calendar) => calendar.data !== undefined);

  const periodKeys = calendarsReady
    ? [...new Set(calendars.flatMap((calendar) => openPeriodKeys(calendar.data?.periods)))]
        .sort((a, b) => b - a)
        .slice(0, MaxPeriods)
    : [];

  const lists = useQueries({
    queries: periodKeys.map((periodKey) => ({
      queryKey: ['documents-stale-mine', periodKey],
      queryFn: () => listDocuments({ periodKey, resultsStale: true, staleBy: 'me' }),
      enabled,
      staleTime: 0,
    })),
  });

  const error =
    projects.error ?? calendars.find((c) => c.error !== null)?.error ?? lists.find((l) => l.error !== null)?.error ?? null;
  const isPending = enabled && error === null && (!calendarsReady || lists.some((l) => l.data === undefined));

  const items: StaleDocument[] = lists.flatMap((list, index) =>
    (list.data?.items ?? []).map((document) => ({ document, periodKey: periodKeys[index] ?? 0 })),
  );

  return {
    items,
    isPending,
    error,
    refetch: () => {
      void projects.refetch();
      calendars.forEach((calendar) => void calendar.refetch());
      lists.forEach((list) => void list.refetch());
    },
  };
}
