import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

/**
 * Вибір користувача живе **в адресі сторінки** (`ФВ-14.29`).
 *
 * ⛔ Не в `useState`. Обраний період, довідник, проєкт, вкладка — це те, що
 * людина хоче надіслати колезі («подивись ось це») і те, до чого вона
 * повертається завтра. У локальному стані все це зникає при перезавантаженні,
 * а посилання веде на порожній екран із проханням обрати щось наново.
 *
 * ⚠ `replace: true` навмисно: зміна фільтра НЕ додає запис в історію.
 * Інакше «Назад» після десяти уточнень фільтра десять разів вертало б до
 * попереднього значення замість того, щоб піти зі сторінки.
 */
export function useUrlState(
  name: string,
): [string | null, (value: string | null) => void] {
  const [params, setParams] = useSearchParams();

  const set = useCallback(
    (value: string | null) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current);

          // ⚠ Порожнє значення ПРИБИРАЄ параметр, а не пише `?code=`: адреса
          // з порожніми хвостами накопичує їх і стає нечитабельною за три
          // зміни фільтра.
          if (value === null || value.length === 0) {
            next.delete(name);
          } else {
            next.set(name, value);
          }

          return next;
        },
        { replace: true },
      );
    },
    [name, setParams],
  );

  return [params.get(name), set];
}

/** Те саме для числового параметра. */
export function useUrlNumber(name: string): [number | null, (value: number | null) => void] {
  const [raw, setRaw] = useUrlState(name);

  const set = useCallback(
    (value: number | null) => {
      setRaw(value === null ? null : String(value));
    },
    [setRaw],
  );

  // ⚠ Нечислове значення в адресі трактується як відсутнє, а не як `NaN`:
  // адресу правлять руками, і `?periodKey=abc` не має ламати екран.
  const parsed = raw === null ? Number.NaN : Number(raw);

  return [Number.isFinite(parsed) ? parsed : null, set];
}

/**
 * Онова КІЛЬКОХ параметрів адреси ОДНИМ переходом.
 *
 * ⛔ UI-аудит, lane 3: два окремі виклики сеттера `useUrlState` в одному
 * обробнику (наприклад, `setPeriodKey(value)` одразу за ним `setCursor(null)`)
 * НЕ компонуються — react-router's `setSearchParams`, викликаний двічі
 * синхронно в тому самому тіку, губить ОБИДВІ зміни, а не лише другу
 * (підтверджено ізольованим тестом на голому `useSearchParams`, без жодної
 * обгортки цього файлу: другий виклик не «перемагає» перший — обидва
 * зникають). Наслідок у `DocumentsPage.tsx` — поле «Period» набирало
 * значення на екрані (некерований DOM встигав його показати), але жоден
 * запит ніколи не бачив `periodKey` в адресі. Коли треба змінити більш ніж
 * один параметр за одну дію — використовуй цей хук, а не два окремі
 * `useUrlState`.
 */
export function useUrlParamsSetter(): (updates: Record<string, string | number | null>) => void {
  const [, setParams] = useSearchParams();

  return useCallback(
    (updates: Record<string, string | number | null>) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current);

          for (const [name, value] of Object.entries(updates)) {
            if (value === null || value === '') {
              next.delete(name);
            } else {
              next.set(name, String(value));
            }
          }

          return next;
        },
        { replace: true },
      );
    },
    [setParams],
  );
}
