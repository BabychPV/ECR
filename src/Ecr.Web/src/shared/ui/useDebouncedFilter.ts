import { useCallback, useState } from 'react';
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

/**
 * Курсор сторінки («More»), прив'язаний до ЗАСТОСОВАНОГО фільтра.
 *
 * ⛔ Курсор позначає позицію в конкретній видачі, тож зі зміною фільтра він
 * мусить зникнути — але разом із ВІДКЛАДЕНИМ значенням, а не з сирим. Скидання
 * з `onChange` поля (сире значення) давало після гортання зайвий запит
 * «старий фільтр, перша сторінка» ще до того, як минала пауза.
 *
 * Тому курсор не скидається руками взагалі: він дійсний, лише поки
 * `filterKey` той самий, для якого його отримано. Змінився ключ — курсор
 * `null`, у тому самому рендері, що й новий фільтр.
 *
 * @param filterKey рядок, що однозначно описує застосований фільтр (без курсора).
 */
export function useFilterCursor(
  filterKey: string,
): readonly [string | null, (cursor: string | null) => void] {
  const [state, setState] = useState<{ readonly cursor: string | null; readonly forKey: string }>({
    cursor: null,
    forKey: filterKey,
  });

  const setCursor = useCallback(
    (cursor: string | null) => setState({ cursor, forKey: filterKey }),
    [filterKey],
  );

  return [state.forKey === filterKey ? state.cursor : null, setCursor];
}
