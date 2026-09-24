import { useDebouncedValue } from '@mantine/hooks';

/**
 * Пауза після останньої клавіші, після якої текстовий/числовий фільтр
 * застосовується до запиту.
 */
export const FilterDebounceMs = 300;

/**
 * Значення фільтра, відкладене до паузи в наборі.
 *
 * ⛔ Поле й адреса оновлюються одразу — відкладається лише те, що йде в
 * `queryKey`. Без цього набір `R12345` у «Row key» журналу змін давав шість
 * запитів `GET /api/v1/audit/cells?…&rowKey=R`, `R1`, … — кожен по
 * партиціонованій таблиці (живий стенд, 2026-09-24).
 *
 * ⚠ Лише для ПРИМІТИВІВ (`string | number | null`): `useDebouncedValue`
 * порівнює значення через `!==`, і новий об'єкт на кожному рендері
 * перезапускав би таймер без кінця.
 *
 * ⚠ Перше значення (з адреси при відкритті сторінки) застосовується одразу,
 * без паузи: чекати тут нічого — людина ще нічого не набирала.
 */
export function useDebouncedFilter<T extends string | number | boolean | null>(value: T): T {
  const [debounced] = useDebouncedValue(value, FilterDebounceMs);

  return debounced;
}
