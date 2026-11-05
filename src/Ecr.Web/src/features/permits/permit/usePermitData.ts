import { useMemo } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RegistryEntryDto } from '@/api/types';
import { getRegistryRows } from '@/features/registries/rows/api';
import type { RegistryRow } from '@/features/registries/rows/api';
import { getRegistryTree } from '@/features/registries/tree/treeApi';
import { todayDateOnly } from '@/shared/format/dateOnly';
import type { Permit, PermitType } from '../api/types';
import { permitKeys } from './permitQueries';
import { createLimiter, PickerConcurrency } from './readLimiter';
import { fieldsOf, loadRow, pickerKey, pickerLevel, type LoadedRow, type PermitField, type PickerData } from './model';

// Ключі кешу й запити шапки — у `permitQueries.ts` (бюджет маршруту, D-132); реекспорт для наявних імпортів.
export { permitKeys, usePermit, usePermitTypeDefinition, useUnitLabels } from './permitQueries';

/** Сторінка читання рядків за раз: межа сервера — 500. */
const PageSize = 500;

/** Спільний пул читань пікерів: усі `usePickerData` разом тримають у польоті не більше {@link PickerConcurrency} запитів. */
export const pickerReads = createLimiter(PickerConcurrency);

/** Запис пікера: id та підпис (для списку з `/rows` — `display`, для NAE — «номер · назва»). */
export type PickRow = Pick<RegistryRow, 'id' | 'display'>;

/**
 * Записи довідника ОДНОГО рівня (`GET …/entries?asOf=&level=`): перелік NAE у формі рядка — лише вузли джерела (`SRC`),
 * без регіонів, районів і майданчиків (людина 05.11: «має бути тільки nae»). Підпис — «<код> · <назва>».
 *
 * ⚠ Адреса без рядка запиту — окремим літералом: `EndpointCoverageTests` читає весь вміст між лапками як адресу.
 */
async function readEntriesOfLevel(
  code: string,
  level: string,
  run: <T>(task: () => Promise<T>) => Promise<T>,
): Promise<PickRow[]> {
  const baseUrl = `/api/v1/registries/${encodeURIComponent(code)}/entries`;
  // ⛔ `asOf` обов'язковий для темпорального довідника (як і в `readAll`).
  const params = new URLSearchParams({ asOf: todayDateOnly(), level });
  const entries = await run(() => apiFetch<RegistryEntryDto[]>(`${baseUrl}?${params.toString()}`));

  // ⚠ Код запису довідника з автокодом технічний; бізнес-номер NAE — поле `NaeCode`, яке віддає лише дерево (`naeCode`).
  // Дерево потрібне лише для підпису: збій його читання не ховає варіанти — підпис падає назад на код запису.
  const naeCodes = new Map<number, string>();
  try {
    const tree = await run(() => getRegistryTree(code, false, ''));
    for (const node of tree.nodes) {
      if (node.naeCode != null && node.naeCode !== '') naeCodes.set(node.id, node.naeCode);
    }
  } catch {
    // підпис із коду запису
  }

  return entries
    .filter((entry) => entry.levelCode?.toLowerCase() === level.toLowerCase())
    .map((entry) => {
      const prefix = naeCodes.get(entry.id) ?? entry.code;

      return { id: entry.id, display: prefix === '' ? entry.display : `${prefix} · ${entry.display}` };
    });
}

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
    const found = new Map<string, { key: string; code: string; parent: number | undefined; level: string | null }>();
    for (const { column, source } of fields) {
      const key = pickerKey(column, source);
      if (key === null || found.has(key)) continue;
      if (key.startsWith('permit:')) {
        const table = permit.tables.find((candidate) => candidate.code === key.slice('permit:'.length));
        if (table !== undefined) found.set(key, { key, code: table.registryCode, parent: permit.id, level: null });
      } else {
        found.set(key, { key, code: key, parent: undefined, level: pickerLevel(source) });
      }
    }
    return [...found.values()];
  }, [fields, permit]);

  const results = useQueries({
    queries: targets.map((target) =>
      target.level === null
        ? {
            queryKey: permitKeys.list(target.code, String(target.parent ?? 'shared')),
            queryFn: (): Promise<PickRow[]> => readAll(target.code, target.parent, pickerReads),
            enabled,
          }
        : {
            // Окремий ключ: `/entries?level=` не ділить кеш із повним `/rows` того самого реєстру.
            queryKey: permitKeys.levelList(target.code, target.level),
            queryFn: (): Promise<PickRow[]> => readEntriesOfLevel(target.code, target.level ?? '', pickerReads),
            enabled,
          },
    ),
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
