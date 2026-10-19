/**
 * Обмежувач паралелізму: не більше `max` завдань одночасно, решта чекає в черзі (FIFO). Звільнене місце передається
 * наступному в черзі без проміжку, тож ліміт не перевищується навіть між завершенням одного завдання й стартом іншого.
 */
export function createLimiter(max: number): <T>(task: () => Promise<T>) => Promise<T> {
  let active = 0;
  const waiting: (() => void)[] = [];

  return async function run<T>(task: () => Promise<T>): Promise<T> {
    if (active < max) active += 1;
    else await new Promise<void>((resolve) => waiting.push(resolve));

    try {
      return await task();
    } finally {
      const next = waiting.shift();
      if (next === undefined) active -= 1;
      else next();
    }
  };
}

/**
 * Скільки запитів читання рядків реєстрів одночасно (пікери форми й вставки): стільки ж «робітників», як у скані
 * прив'язок (`useBindingsScan`, 4), - сторінка не тримає в польоті десятки запитів.
 */
export const PickerConcurrency = 4;
