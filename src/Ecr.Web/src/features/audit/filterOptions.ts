import { useMemo, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CellChangePage, DocumentPage } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/*
 * audit-filters-live D2/D3/D4: фільтри «Document», «Column» і «By user» журналу змін — ВИБОРОМ за тим, що людина бачить
 * на екрані (назва/код документа, підпис колонки, ім'я з логіном), а не внутрішнім номером.
 * ⛔ Живий прохід 25.11: «P1-V9-0001» у полі Document мовчки ставав `190001` (→ «Document not found»), «M10» у Column —
 * `10` (→ хибне «No changes in this window»). Тут довільний текст лише ШУКАЄ серед варіантів; значення — завжди Id
 * обраного варіанта, а в адресі лишається Id (посилання колезі не змінюються).
 * ⚠ Без імпорту `features/documents/api.ts`: маршрут `AuditPage` біля межі бюджету D-132.
 */

type CellChange = CellChangePage['items'][number];

/** Варіант вибору Mantine `Select`. */
export interface FilterOption {
  readonly value: string;
  readonly label: string;
}

/** Підпис документа так, як його видно в «Documents»: назва · код; без назви — код. */
export function documentOptionLabel(name: string, businessKey: string | null | undefined): string {
  const key = businessKey ?? '';
  if (name.length === 0) return key;
  return key.length === 0 || key === name ? name : `${name} · ${key}`;
}

/**
 * Варіанти «Document»: пошук сервером за назвою/кодом (той самий `q`, що в «Documents»), плюс документи, які вже видно
 * в журналі, плюс обраний (щоб поле з `?documentId=` показувало назву, а не порожнечу).
 */
export function useDocumentOptions(
  search: string,
  seen: readonly CellChange[] | undefined,
  selected: number | null,
): FilterOption[] {
  // Підписи, які вже траплялися: обраний документ лишається з назвою, навіть коли пошук (текст поля = підпис) його
  // більше не повертає.
  const known = useRef(new Map<number, string>());
  const typed = search.trim();
  const q = [...known.current.values()].includes(typed) ? '' : typed;
  const found = useQuery({
    queryKey: ['audit-document-options', q],
    queryFn: () => {
      const query = new URLSearchParams({ limit: '20' });
      if (q.length > 0) query.set('q', q);
      return apiFetch<DocumentPage>(`/api/v1/documents?${query.toString()}`);
    },
    staleTime: 60_000,
    retry: false,
  });

  return useMemo(() => {
    const names = known.current;

    for (const item of seen ?? []) {
      if (!names.has(item.documentId) && (item.documentBusinessKey ?? null) !== null) {
        names.set(item.documentId, documentOptionLabel(localized(item.documentNameL10n), item.documentBusinessKey));
      }
    }

    for (const doc of found.data?.items ?? []) {
      names.set(doc.id, documentOptionLabel(localized(doc.nameL10n ?? null), doc.businessKey));
    }

    const shown = new Map(names);
    if (selected !== null && !shown.has(selected)) shown.set(selected, `#${String(selected)}`);

    return [...shown.entries()]
      .sort((a, b) => a[1].localeCompare(b[1]))
      .map(([id, label]) => ({ value: String(id), label }));
  }, [seen, found.data, selected]);
}

/** Підпис колонки: аркуш / таблиця / заголовок (код) — код колонки (`M10`) повторюється в кожній таблиці. */
export function columnOptionLabel(change: CellChange): string {
  const header = localized(change.columnHeaderL10n);
  const code = change.columnCode ?? '';
  const column = header.length > 0 ? (code.length > 0 ? `${header} (${code})` : header) : code;
  const sheet = localized(change.sheetNameL10n) || (change.sheetCode ?? '');
  const table = localized(change.tableNameL10n) || (change.tableCode ?? '');

  return [sheet, table, column].filter((part) => part.length > 0).join(' / ') || `#${String(change.columnDefId)}`;
}

/**
 * Варіанти «Column»: колонки ОБРАНОГО документа, що є в журналі вікна (рядки журналу несуть підпис колонки), і обрана.
 * ⚠ Колонка без змін у вікні дала б «No changes» — у переліку її й немає; рядки інших документів не беруться.
 */
export function columnOptions(
  items: readonly CellChange[] | undefined,
  documentId: number | null,
  selected: number | null,
): FilterOption[] {
  const names = new Map<number, string>();

  for (const item of items ?? []) {
    if (documentId !== null && item.documentId === documentId && !names.has(item.columnDefId)) {
      names.set(item.columnDefId, columnOptionLabel(item));
    }
  }

  if (selected !== null && !names.has(selected)) names.set(selected, `#${String(selected)}`);

  return [...names.entries()]
    .sort((a, b) => a[1].localeCompare(b[1]))
    .map(([id, label]) => ({ value: String(id), label }));
}

/**
 * D4: однакові імена в «By user» розрізняються логіном у дужках (коли логін відомий з переліку користувачів), інакше —
 * номером `#id`. Унікальні імена — як були.
 */
export function disambiguateNames(
  options: readonly FilterOption[],
  logins: ReadonlyMap<number, string>,
): FilterOption[] {
  const counts = new Map<string, number>();
  for (const option of options) counts.set(option.label, (counts.get(option.label) ?? 0) + 1);

  return options.map((option) => {
    if ((counts.get(option.label) ?? 0) < 2) return option;
    const login = logins.get(Number(option.value));
    return { ...option, label: `${option.label} (${login ?? `#${option.value}`})` };
  });
}
