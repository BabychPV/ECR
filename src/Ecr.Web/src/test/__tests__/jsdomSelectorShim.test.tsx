import { afterEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider, Select } from '@mantine/core';

/**
 * Сторож заглушки станових псевдокласів (`src/test/setup.ts`).
 *
 * ⛔ Що саме він тримає закритим: взаємну рекурсію jsdom ↔ `nwsapi`. Без
 * заглушки один рендер випадного блоку `Combobox` коштував ~35 с чистого CPU,
 * бо `tabbable` питав `:modal`, `nwsapi` перепитував «нативну» реалізацію, а
 * нею в jsdom є сам `nwsapi`. Механізм і числа — у коментарі в `setup.ts`.
 *
 * ⛔ Перевірка НЕ на час. Тест на тривалість був би недетермінованим саме на
 * навантаженій машині, тобто падав би там, де найпотрібніший. Тут перевірка
 * рахункова й бінарна: скільки разів `nwsapi` дійшов до перевірки повного
 * екрана. Із заглушкою — ЖОДНОГО, бо `:modal` до `nwsapi` не доходить; без
 * неї це десятки мільйонів.
 */

/*
 * ⚠ Лічильник живе НЕ в обгортці `Element.matches`, і це не стиль. Обгортка
 * додає власний кадр стека на КОЖЕН рівень рекурсії (їх тут 2 910), і без
 * заглушки замість зрозумілого падіння перевірки воркер просто гине з
 * переповненням стека — сторож повідомляв би про поламану інфраструктуру, а
 * не про зняту заглушку. `nwsapi.isFullscreen` читає
 * `document.fullscreenElement` рівно тоді, коли рекурсія дійшла до нього,
 * тож геттер рахує ті самі події, не подовжуючи стека.
 */
let fullscreenReads = 0;

Object.defineProperty(window.Document.prototype, 'fullscreenElement', {
  configurable: true,
  get() {
    fullscreenReads += 1;

    return null;
  },
});

afterEach(() => {
  fullscreenReads = 0;
});

describe('заглушка станових псевдокласів jsdom', () => {
  it('змонтований випадний блок не заганяє nwsapi в рекурсію', async () => {
    render(
      <MantineProvider>
        <Select label="поле" data={['Альфа', 'Бета']} comboboxProps={{ keepMounted: true }} />
      </MantineProvider>,
    );

    // Блок справді в DOM — інакше пастка фокуса не мала б чого перевіряти, і
    // сторож охороняв би порожнечу.
    expect(screen.queryByText('Альфа')).not.toBeNull();

    // ⚠ Пастка фокуса питає про стан НЕ під час рендеру, а вже після нього —
    // саме тому без заглушки ціна діставалася не цьому тестові, а НАСТУПНОМУ
    // (звідти й репутація «плаваючого» падіння). Тут один оберт циклу подій.
    await new Promise((resolve) => {
      setTimeout(resolve, 0);
    });

    expect(fullscreenReads).toBe(0);
  });

  it('станові псевдокласи відповідають false, а звичайні селектори — як завжди', () => {
    const div = document.createElement('div');
    div.className = 'зразок';
    document.body.append(div);

    expect(div.matches(':modal')).toBe(false);
    expect(div.matches(':fullscreen')).toBe(false);
    expect(div.matches(':popover-open')).toBe(false);
    expect(fullscreenReads).toBe(0);

    // ⚠ Заглушка не має права відповідати за складені селектори: там працює
    // звичайний розбір `nwsapi`, і обидві відповіді мусять лишитися чесними.
    expect(div.matches('.зразок')).toBe(true);
    expect(div.matches('span')).toBe(false);

    div.remove();
  });
});
