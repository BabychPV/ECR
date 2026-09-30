import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, RegistryDefinitionDto, UnitRef } from '@/api/types';
import { getRegistryRows, type RegistryRow } from '@/features/registries/rows/api';

/**
 * Читання для редактора даних довідника (`ФВ-8.12`).
 *
 * ⚠ Ключі кешу починаються з `'registries'`: збереження пакета скидає весь домен одним
 * `invalidateQueries({ queryKey: queryKeys.registries.all() })`, і перелік записів на сторінці
 * довідників (`RegistriesPage`) не лишається старим.
 */

/** Ключ сторінок рядків довідника. */
export const rowsKey = (code: string, asOf: string | null, q: string, asOfUtc: string | null = null) =>
  ['registries', 'rows', code, asOf ?? '', q, asOfUtc ?? ''] as const;

/** Опис довідника — той самий ключ і запит, що в конструкторі: кеш спільний. */
export function useRegistryDefinition(code: string) {
  return useQuery({
    queryKey: queryKeys.registries.definition(code),
    queryFn: () => apiFetch<RegistryDefinitionDto>(`/api/v1/registries/${encodeURIComponent(code)}/definition`),
    refetchOnWindowFocus: false,
  });
}

/** Перелік довідників: із нього — код цілі `Lookup` за `lookupRegistryDefId`. */
export function useRegistryList() {
  return useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });
}

/** Одиниці для полів типу `Unit`; ключ той самий, що в панелях методологій. */
export function useUnits(enabled: boolean) {
  return useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    enabled,
  });
}

/**
 * Рядки довідника сторінками за курсором.
 *
 * ⛔ Фонового перечитування немає: сітка тримає незбережені правки поверх цих рядків, і
 * перечитування при поверненні фокуса підміняло б `version`, до якого прив'язана правка —
 * конфлікт `entryChanged` зник би непоміченим.
 */
export function useRegistryRows(code: string, asOf: string | null, q: string, enabled: boolean) {
  return useInfiniteQuery({
    queryKey: rowsKey(code, asOf, q),
    queryFn: ({ pageParam }) =>
      getRegistryRows(code, { ...(asOf ? { asOf } : {}), q, cursor: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled,
    refetchOnWindowFocus: false,
  });
}

/** Варіанти пікера `Lookup`: до 50 записів цілі за підрядком (§8.4). */
export function useLookupOptions(targetCode: string | null, search: string, asOf: string | null) {
  return useQuery({
    queryKey: ['registries', 'rows', targetCode ?? '', asOf ?? '', search, 'pick'],
    queryFn: () =>
      getRegistryRows(targetCode ?? '', { ...(asOf ? { asOf } : {}), q: search, limit: 50 }),
    enabled: targetCode !== null,
    staleTime: 30_000,
  });
}

/** Назва цілі `Lookup` для пікера й підпису комірки. */
export function lookupLabel(row: Pick<RegistryRow, 'code' | 'display'>): string {
  return row.display === row.code ? row.code : `${row.display} (${row.code})`;
}

/**
 * Зіставлення вставленого тексту з записом цілі `Lookup` — за кодом або назвою, без урахування
 * регістру (§8.4 «Вставка з Excel»). Кілька збігів або жодного — `null`: вгадувати ціль не можна,
 * бо в комірці зберігається id, і вгадане читалося б як введене людиною.
 */
export async function resolveLookup(
  targetCode: string,
  text: string,
  asOf: string | null,
): Promise<{ id: string; display: string } | null> {
  const wanted = text.trim().toLocaleLowerCase();
  if (wanted === '') return null;

  const page = await getRegistryRows(targetCode, { ...(asOf ? { asOf } : {}), q: text.trim(), limit: 20 });
  const byCode = page.items.filter((row) => row.code.toLocaleLowerCase() === wanted);
  const matches = byCode.length > 0 ? byCode : page.items.filter((row) => row.display.toLocaleLowerCase() === wanted);
  const only = matches.length === 1 ? matches[0] : undefined;

  return only === undefined ? null : { id: String(only.id), display: lookupLabel(only) };
}

/**
 * Сьогоднішня дата клієнта як `yyyy-MM-dd` — `asOf` темпорального довідника за замовчуванням.
 *
 * ⛔ Не `toISOString().slice(0, 10)`: той читає північ як UTC і зсуває день на добу.
 */
export function todayIso(now: Date = new Date()): string {
  const pad = (n: number): string => String(n).padStart(2, '0');
  return `${String(now.getFullYear())}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** `Date` з поля дати → `yyyy-MM-dd` за місцевим календарем (та сама пастка, що вище). */
export function isoOfDate(value: Date | null): string | null {
  return value === null ? null : todayIso(value);
}

/** `yyyy-MM-dd` → місцева північ для поля дати. */
export function dateOfIso(value: string | null): Date | null {
  const match = value === null ? null : /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return match === null ? null : new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
}
