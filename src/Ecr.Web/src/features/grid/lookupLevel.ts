/**
 * HSE401: фільтр Lookup-колонки `ColumnDef.lookupFilter` - формат `level=<КОД РІВНЯ>` (сервер: `LookupFilterSpec`).
 * Невідомий формат дає `null` (без звуження): сервер усе одно відхилить запис не того рівня.
 */
export function lookupLevelOf(filter: string | null | undefined): string | null {
  if (filter === null || filter === undefined) return null;

  const match = /^level=([A-Za-z0-9_.-]{1,64})$/.exec(filter.trim());

  return match?.[1] ?? null;
}

/**
 * Рівень, спільний для ВСІХ Lookup-колонок довідника `registryId`: тоді перелік записів можна звузити запитом `?level=`
 * (для великих ієрархічних довідників це і менше даних). Колонки з різними рівнями (чи частина без фільтра) -
 * `null`: перелік довідника спільний, а колонка звужує його сама (`entriesOfLevel`).
 */
export function sharedLookupLevel(
  columns: readonly { dataType: string; lookupRegistryDefId: number | null; lookupFilter?: string | null }[],
  registryId: number,
): string | null {
  const levels = columns
    .filter((column) => column.dataType === 'Lookup' && column.lookupRegistryDefId === registryId)
    .map((column) => lookupLevelOf(column.lookupFilter));

  if (levels.length === 0 || levels.some((level) => level === null)) return null;

  return levels.every((level) => level?.toLowerCase() === levels[0]?.toLowerCase()) ? (levels[0] ?? null) : null;
}

/** Записи, придатні колонці з фільтром `level=`: без фільтра - усі; записи без `levelCode` відсікаються. */
export function entriesOfLevel<T extends { levelCode?: string | null }>(
  entries: readonly T[],
  filter: string | null | undefined,
): readonly T[] {
  const level = lookupLevelOf(filter);
  if (level === null) return entries;

  const wanted = level.toLowerCase();

  return entries.filter((entry) => entry.levelCode?.toLowerCase() === wanted);
}