import { createContext, useContext, type JSX, type ReactNode } from 'react';
import { Group, rem, type GroupProps, type MantineSize } from '@mantine/core';

/**
 * Будова ОДНОГО ряду фільтрів — спільна для всіх панелей фільтрів застосунку
 * (Documents, журнал змін, «Consistency issues», задачі, рядки інтерфейсу,
 * події джерел тощо).
 *
 * ⛔ Чому окремий компонент, а не `<Group align="end">` на кожній сторінці.
 * Поле з підписом вище за прапорець, перемикач і кнопку «Clear filters»
 * без підпису. Вирівняні за НИЖНЬОЮ межею, вони стоять «на підлозі» поля, а
 * не на його середній лінії: прапорець і перемикач нижчі за поле, тож їхній
 * текст опинявся нижче за текст у полі (живий стенд, 2026-10-06: «Late edits
 * only», «Clear filters», «Unresolved only» — «не в строці»). Кожна сторінка
 * латала це по-своєму — `mb="xs"`, `mt="lg"`, `align="flex-start"` — і жодна
 * латка не збігалася з іншою.
 *
 * Правило тут одне:
 *  1. ряд — `align="flex-end"`: нижні межі полів на одній лінії, хоч би який
 *     підпис над ними;
 *  2. усе, що НЕ є полем із підписом (прапорець, перемикач, кнопка скидання,
 *     лічильник, значок), іде в `<FilterInline>` — коробку рівно ВИСОТОЮ ПОЛЯ
 *     цього ряду з вмістом по центру. Тоді середня лінія прапорця збігається з
 *     середньою лінією тексту в полі за будь-якого розміру шрифту.
 *
 * ⚠ Видимого `description` під полем у ряду бути не повинно: воно опускає
 * нижню межу саме цього поля, і ряд знову розсувається. Пояснення — під рядом
 * (`FilterHints`), а в полі — приховане для ока (`readerOnlyDescription`).
 */

/** Розмір полів ряду — від нього залежить висота `FilterInline`. */
type FilterRowSize = Extract<MantineSize, 'xs' | 'sm' | 'md'>;

/**
 * Висота поля Mantine за розміром, у пікселях макета (`--input-height-*` у
 * `@mantine/core/styles.css`: 30 / 36 / 42).
 *
 * ⚠ Числом, а не `var(--input-height-xs)`: ця змінна визначена лише на самому
 * полі, а не на `:root`, і поза полем дорівнювала б нулю.
 */
const InputHeights: Readonly<Record<FilterRowSize, number>> = { xs: 30, sm: 36, md: 42 };

const FilterRowSizeContext = createContext<FilterRowSize>('xs');

export interface FilterRowProps extends Omit<GroupProps, 'align' | 'children'> {
  /** Розмір полів ряду; за замовчуванням — `xs`, як у більшості панелей. */
  readonly size?: FilterRowSize | undefined;

  readonly children?: ReactNode;
}

/** Ряд фільтрів: поля, прапорці, кнопка скидання — в одну лінію. */
export function FilterRow({ size = 'xs', gap = 'xs', wrap = 'wrap', children, ...rest }: FilterRowProps): JSX.Element {
  return (
    <FilterRowSizeContext.Provider value={size}>
      <Group gap={gap} wrap={wrap} align="flex-end" data-filter-row={size} {...rest}>
        {children}
      </Group>
    </FilterRowSizeContext.Provider>
  );
}

/**
 * Елемент ряду без підпису над собою (прапорець, перемикач, кнопка, лічильник):
 * висотою поля ряду, вміст — по центру.
 */
export function FilterInline({
  children,
  gap = 'xs',
  ...rest
}: Omit<GroupProps, 'h' | 'align'> & { readonly children?: ReactNode }): JSX.Element {
  const size = useContext(FilterRowSizeContext);

  return (
    <Group h={rem(InputHeights[size])} gap={gap} wrap="nowrap" align="center" data-filter-inline="" {...rest}>
      {children}
    </Group>
  );
}
