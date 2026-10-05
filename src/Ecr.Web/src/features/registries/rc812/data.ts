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

/** Скільки сторінок по 500 переглядає зіставлення `Lookup`, перш ніж здатися. */
const LookupResolvePages = 10;

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

  // ⚠ `q` — ПІДРЯДОК коду, назви й текстових полів, сервер сортує за Id: точний збіг короткого коду
  // (`N2`) міг не потрапити в першу двадцятку, і комірка лишалася «не зіставленою» (L9-08). Тому —
  // найбільша сторінка і прохід курсором (точного фільтра коду API не має).
  const items: RegistryRow[] = [];
  let cursor: string | null = null;
  for (let pages = 0; pages < LookupResolvePages; pages += 1) {
    const page = await getRegistryRows(targetCode, { ...(asOf ? { asOf } : {}), q: text.trim(), cursor, limit: 500 });
    items.push(...page.items);
    cursor = page.nextCursor ?? null;
    if (cursor === null) break;
  }
  const byCode = items.filter((row) => row.code.toLocaleLowerCase() === wanted);
  const matches = byCode.length > 0 ? byCode : items.filter((row) => row.display.toLocaleLowerCase() === wanted);
  const only = matches.length === 1 ? matches[0] : undefined;

  return only === undefined ? null : { id: String(only.id), display: lookupLabel(only) };
}

/** Скільки запитів зіставлення `Lookup` вставки йде одночасно. */
export const LookupPasteConcurrency = 4;

/**
 * Зіставлення всіх `Lookup` однієї вставки (§8.4): однакові пари «ціль + текст» резолвляться ОДИН
 * раз (без урахування регістру), до `LookupPasteConcurrency` запитів одночасно.
 *
 * ⚠ Відмова запиту (403 на ціль, мережа) — `null`, як і «не знайдено»: вставка не обривається на
 * півдорозі мовчки, а людина бачить лічильник незіставлених комірок.
 *
 * @returns Функція «ціль, текст → запис або `null`» над уже резолвленими парами.
 */
export async function resolveLookups(
  requests: readonly { readonly target: string; readonly text: string }[],
  asOf: string | null,
  concurrency: number = LookupPasteConcurrency,
): Promise<(target: string, text: string) => { id: string; display: string } | null> {
  const keyOf = (target: string, text: string): string => `${target}\u0000${text.trim().toLocaleLowerCase()}`;
  const pending = new Map<string, { target: string; text: string }>();
  for (const request of requests) pending.set(keyOf(request.target, request.text), request);

  const found = new Map<string, { id: string; display: string } | null>();
  const queue = [...pending.entries()];
  const worker = async (): Promise<void> => {
    for (let next = queue.shift(); next !== undefined; next = queue.shift()) {
      const [key, { target, text }] = next;
      try {
        found.set(key, await resolveLookup(target, text, asOf));
      } catch {
        found.set(key, null);
      }
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, queue.length)) }, worker));

  return (target, text) => found.get(keyOf(target, text)) ?? null;
}

