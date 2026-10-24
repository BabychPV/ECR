import { useMemo } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { getRegistryRows } from '@/features/registries/rows/api';
import type { RegistryRow } from '@/features/registries/rows/api';
import { todayDateOnly } from '@/shared/format/dateOnly';
import type { Permit, PermitType } from '../api/types';
import { permitKeys } from './permitQueries';
import { createLimiter, PickerConcurrency } from './readLimiter';
import { fieldsOf, loadRow, pickerKey, type LoadedRow, type PermitField, type PickerData } from './model';

// Ключі кешу й запити шапки — у `permitQueries.ts` (бюджет маршруту, D-132); реекспорт для наявних імпортів.
export { permitKeys, usePermit, usePermitTypeDefinition, useUnitLabels } from './permitQueries';

/** Сторінка читання рядків за раз: межа сервера — 500. */
const PageSize = 500;

/** Спільний пул читань пікерів: усі `usePickerData` разом тримають у польоті не більше {@link PickerConcurrency} запитів. */
export const pickerReads = createLimiter(PickerConcurrency);

async function readAll(
  code: string,
  parentEntryId?: number,
  run: <T>(task: () => Promise<T>) => Promise<T> = (task) => task(),
): Promise<RegistryRow[]> {
  const rows: RegistryRow[] = [];
  let cursor: string | null = null;
  do {
    // ⛔ `asOf` обов'язковий: реєстри темпоральні, без нього сервер відповідає `422 ECR-REQ-0422` (e2e-стенд 19.10).
    const after: string | null = cursor;
    const page: Awaited<ReturnType<typeof getRegistryRows>> = await run(() =>
      getRegistryRows(code, {
        asOf: todayDateOnly(),
        ...(parentEntryId === undefined ? {} : { parentEntryId }),
        cursor: after,
        limit: PageSize,
      }),
    );
    rows.push(...page.items);
    cursor = page.nextCursor ?? null;
  } while (cursor !== null);
  return rows;
}

/** Усі рядки таблиці дозволу (прихований реєстр `PRM_…`, батько — дозвіл), перетворені на рядки таблиці. */
export function usePermitRows(registryCode: string, permitId: number, fields: readonly PermitField[], enabled: boolean) {
  const query = useQuery<RegistryRow[]>({
    queryKey: permitKeys.rows(registryCode, permitId),
    queryFn: () => readAll(registryCode, permitId),
    enabled,
  });
  const rows = useMemo<LoadedRow[] | undefined>(
    () => query.data?.map((row) => loadRow(fields, row)),
    [query.data, fields],
  );
  return { query, rows };
}

/**
 * Значення пікерів форми рядка й вставки: спільні реєстри та списки типу — рядки їхніх реєстрів, «таблиця цього
 * дозволу» — рядки сестринської таблиці. Читається лише коли форма відкрита (`enabled`).
 */
export function usePickerData(permit: Permit, fields: readonly PermitField[], enabled: boolean) {
  const targets = useMemo(() => {
    const found = new Map<string, { key: string; code: string; parent: number | undefined }>();
    for (const { column, source } of fields) {
      const key = pickerKey(column, source);
      if (key === null || found.has(key)) continue;
      if (key.startsWith('permit:')) {
        const table = permit.tables.find((candidate) => candidate.code === key.slice('permit:'.length));
        if (table !== undefined) found.set(key, { key, code: table.registryCode, parent: permit.id });
      } else {
        found.set(key, { key, code: key, parent: undefined });
      }
    }
    return [...found.values()];
  }, [fields, permit]);

  const results = useQueries({
    queries: targets.map((target) => ({
      queryKey: permitKeys.list(target.code, String(target.parent ?? 'shared')),
      queryFn: () => readAll(target.code, target.parent, pickerReads),
      enabled,
    })),
  });

  const pending = enabled && results.some((result) => result.isPending);
  const failed = results.find((result) => result.isError)?.error ?? null;

  const data = useMemo<PickerData>(() => {
    const shared: Record<string, string[]> = {};
    const tables: Record<string, string[]> = {};
    const ids: Record<string, Map<string, string>> = {};
    targets.forEach((target, index) => {
      const rows = results[index]?.data ?? [];
      const labels = rows.map((row) => row.display);
      const map = new Map(rows.map((row) => [row.display, String(row.id)] as const));
      ids[target.key] = map;
      if (target.key.startsWith('permit:')) tables[target.key.slice('permit:'.length)] = labels;
      else shared[target.key] = labels;
    });
    return { sources: { shared, permitTables: tables }, ids };
    // `results` — новий масив на кожен рендер; залежність — самі дані запитів.
  }, [targets, ...results.map((result) => result.data)]);

  return { data, pending, failed };
}

/** Стовпці таблиці за кодом з визначення типу (порожньо, поки визначення не прочитано). */
export function useTableFields(type: PermitType | undefined, tableCode: string, units: ReadonlyMap<number, string>) {
  return useMemo<PermitField[]>(() => {
    const table = type?.tables.find((candidate) => candidate.code === tableCode);
    return table === undefined ? [] : fieldsOf(table, units);
  }, [type, tableCode, units]);
}
