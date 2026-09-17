import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider, Select } from '@mantine/core';
import { renderWithMantine } from '@/test/render';

/**
 * Сторож спільної обгортки (`src/test/render.tsx`).
 *
 * ⛔ Перевіряє, чи справді тема тестів знімає `keepMounted` у `Combobox`. Тест
 * на час був би недетермінованим (саме такі й доводили гейт `client` до
 * випадкових падінь), а цей — бінарний: випадний блок або в DOM, або ні.
 *
 * ⚠ Від ШВИДКОСТІ це вже не залежить, хоч раніше тут так і було написано.
 * Справжній корінь повільності — рекурсія jsdom ↔ `nwsapi`, обірвана в
 * `src/test/setup.ts`; `keepMounted` був її тригером, а не ціною. Через це
 * другий випадок нижче колись ішов 9.4 с наодинці й до 94 с під повним
 * набором, тобто сам був джерелом «плаваючих» падінь гейта `client`. Тепер
 * обидва випадки — десятки мілісекунд.
 *
 * ⚠ Другий випадок обов'язковий: без нього тест лишився б зеленим і тоді,
 * коли тема перестане діяти (наприклад, після оновлення Mantine, де
 * `comboboxProps` перестане приймати `defaultProps`). Він фіксує, що
 * різницю робить САМЕ тема, а не поведінка Mantine за замовчуванням.
 */
describe('спільна обгортка рендеру', () => {
  it('тема тестів НЕ тримає випадний блок змонтованим', () => {
    renderWithMantine(<Select label="поле" data={['Альфа', 'Бета']} />);

    expect(screen.queryByText('Альфа')).toBeNull();
  });

  it('без теми тестів Mantine тримає його змонтованим — різницю робить тема', () => {
    render(
      <MantineProvider>
        <Select label="поле" data={['Альфа', 'Бета']} />
      </MantineProvider>,
    );

    expect(screen.queryByText('Альфа')).not.toBeNull();
  });
});
