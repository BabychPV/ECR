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
