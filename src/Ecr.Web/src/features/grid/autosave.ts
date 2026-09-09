/**
 * Оркестрація автозбереження grid (`B-35`, `#38`).
 *
 * ⛔ `useCellPatch` довгий час ОБІЦЯВ коментарем дебаунс ~500 мс і збереження
 * при закритті вкладки — і жоден із двох механізмів не існував: єдиний
 * спосіб зберегти правку був явний `Ctrl+S` або кнопка «Зберегти». Оператор,
 * що закривав вкладку одразу після правки, втрачав її мовчки: `pending` жив
 * лише в пам'яті компонента.
 *
 * ⚠ Обидва механізми винесені сюди, а не в `DocumentGrid.tsx`, з тієї самої
 * причини, що й `clipboard.ts`/`undo.ts`: таймер і слухач `window` можна
 * перевірити напряму — фальшивими таймерами й подією `beforeunload` — без
 * монтування grid.
 */

/** Відкладений виклик: кожен новий `trigger()` скасовує попередній план. */
export interface Debouncer {
  /** Планує виклик через `delayMs` тиші; попередній план скасовується. */
  trigger(): void;
  /** Знімає запланований виклик, нічого не викликаючи. */
  cancel(): void;
}

/**
 * Дебаунс автозбереження.
 *
 * ⚠ Саме дебаунс, а не троттлінг: зберігати на КОЖЕН натиск клавіші —
 * сотні запитів на один рядок (див. `buildRequest` у `useCellPatch.ts`), а
 * зберігати за розкладом незалежно від того, чи скінчив оператор редагувати
 * комірку, — це збереження півправки.
 */
export function createDebouncer(callback: () => void, delayMs = 500): Debouncer {
  let handle: ReturnType<typeof setTimeout> | null = null;

  return {
    trigger(): void {
      if (handle !== null) clearTimeout(handle);
      handle = setTimeout(() => {
        handle = null;
        callback();
      }, delayMs);
    },
    cancel(): void {
      if (handle !== null) clearTimeout(handle);
      handle = null;
    },
  };
}

/**
 * Реєструє останній шанс зберегти незбережені правки перед закриттям вкладки.
 *
 * ⚠ `beforeunload` не чекає на `fetch`: сторінка вивантажується незалежно
 * від того, чи встиг запит дійти. Єдиний надійний спосіб донести дані —
 * запит із `keepalive` (`sendPatchBeacon` у `useCellPatch.ts`), не проміс,
 * на завершення якого тут ніхто не чекає й не може чекати.
 *
 * ⛔ `event.preventDefault()` тут НЕ викликається, і діалог «покинути
 * сторінку?» не з'являється. Мета автозбереження — прибрати саму потребу
 * питати оператора, а не підмінити явне збереження попередженням, яке за
 * звичкою закривають не читаючи.
 *
 * @returns Функція відписки — знімає слухача при розмонтуванні.
 */
export function registerUnloadFlush(hasPending: () => boolean, flush: () => void): () => void {
  const handler = (): void => {
    if (hasPending()) flush();
  };

  window.addEventListener('beforeunload', handler);

  return () => window.removeEventListener('beforeunload', handler);
}
