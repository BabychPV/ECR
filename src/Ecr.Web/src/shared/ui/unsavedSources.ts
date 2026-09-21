/**
 * Реєстр «джерел незбережених змін» для `UnsavedGuard`.
 *
 * ⚠ Навіщо: `shared` — нижній шар і не має знати про фічі. Раніше
 * `UnsavedGuard` імпортував сховище правок сітки напряму; тепер фіча сама
 * оголошує себе джерелом (`features/grid/autosave.ts`), а сторож питає реєстр.
 *
 * ⚠ Модульний реєстр, а не контекст React: сторож і так читав модульне сховище
 * в момент виклику (`useBlocker` з порожніми залежностями), тож реєстр того ж
 * виду не потребує провайдера ні в `AppLayout`, ні в тестах.
 */

export interface UnsavedSource {
  /** Чи є зараз що зберігати. Читається в момент виклику. */
  hasUnsaved(): boolean;
  /** Скільки правок незбережено — для тексту діалогу. */
  unsavedCount?(): number;
  /**
   * Зберегти все й дочекатися результату.
   * @returns `true` — незбереженого не лишилось; `false` — лишилось.
   */
  flush?(timeoutMs: number): Promise<boolean>;
}

/** Типовий час відстоювання збереження при виході (`D14-12`, крок 3). */
export const UnsavedSettleMs = 3_000;

const sources = new Map<string, UnsavedSource>();

/** Реєструє джерело під `id` (повторна реєстрація замінює). Повертає відписку. */
export function registerUnsavedSource(id: string, source: UnsavedSource): () => void {
  sources.set(id, source);

  return () => {
    if (sources.get(id) === source) sources.delete(id);
  };
}

export function unregisterUnsavedSource(id: string): void {
  sources.delete(id);
}

export function hasUnsavedChanges(): boolean {
  for (const source of sources.values()) if (source.hasUnsaved()) return true;

  return false;
}

export function unsavedCount(): number {
  let total = 0;
  for (const source of sources.values()) {
    if (source.hasUnsaved()) total += source.unsavedCount?.() ?? 1;
  }

  return total;
}

/**
 * Зберігає всі джерела з незбереженим і чекає на них.
 *
 * ⚠ Джерело без `flush` зберегти нема чим — це невдача, а не успіх: безпечний
 * бік помилки той самий, що й у таймауту (користувач лишається з даними).
 */
export async function flushUnsaved(timeoutMs: number = UnsavedSettleMs): Promise<boolean> {
  const dirty = [...sources.values()].filter((source) => source.hasUnsaved());
  const results = await Promise.all(
    dirty.map(async (source) => (source.flush ? await source.flush(timeoutMs) : false)),
  );

  return results.every(Boolean);
}
