import { describe, it, expect, afterEach } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { mantineProviderProps } from '../provider';
import { Shell } from '@/test/__tests__/a11yFixtures';

/**
 * Гейт `a11y` має перевіряти ТОЙ САМИЙ застосунок, який їде користувачеві.
 *
 * ⛔ Що сталося без цього. `UI-01` додав `cssVariablesResolver` — і додав його
 * лише в `App.tsx`. Приладдя набору a11y будувало власний
 * `<MantineProvider theme={theme}>`, тож **два з семи гейтів** проганяли
 * застосунок на дефолтних змінних Mantine: жодного `--ecr-*`, інші тло, текст
 * і межа поля. Гейти лишалися зеленими, бо перевіряли те, чого на екрані
 * немає.
 *
 * ⚠ Сам по собі спільний об'єкт (`mantineProviderProps`) уже робить
 * розбіжність неможливою за побудовою. Цей тест стереже інше: що об'єкт
 * справді ДІЄ — що резолвер доходить до DOM, а не просто лежить у пропах.
 * Різниця та сама, що між «правило є в конфігу» і «правило спрацьовує».
 */

afterEach(() => {
  cleanup();
});

/** Усі змінні, які Mantine вписала в документ поточного рендера. */
function injectedCss(): string {
  return [...document.querySelectorAll('style')].map((s) => s.textContent ?? '').join('\n');
}

describe('приладдя a11y і застосунок підіймають однаковий провайдер', () => {
  it('об_єкт провайдера несе і тему, і резолвер', () => {
    expect(mantineProviderProps.theme).toBeDefined();

    /*
     * ⛔ Не `toBeDefined()` на всьому об'єкті: саме резолвер і був тим пропом,
     * який дійшов лише в одне з двох місць.
     */
    expect(mantineProviderProps.cssVariablesResolver).toBeTypeOf('function');
  });

  it('Shell набору a11y доносить --ecr-* до документа', () => {
    render(
      <Shell colorScheme="light">
        <p>зонд</p>
      </Shell>,
    );

    const css = injectedCss();

    /*
     * ⚠ Перевіряються КОНКРЕТНІ змінні, а не факт «щось вписалося»: Mantine
     * вписує свої змінні завжди, і `css.length > 0` лишився б зеленим і без
     * резолвера — тобто доводив би рівно нічого.
     */
    expect(css).toContain('--ecr-surface');
    expect(css).toContain('--ecr-ground');
    expect(css).toContain('--ecr-text');
  });

  it('у документі є ОБИДВА значення --ecr-surface, а не одне на дві схеми', () => {
    render(
      <Shell colorScheme="dark">
        <p>зонд</p>
      </Shell>,
    );

    /*
     * ⚠ Перша редакція цього тесту рендерила Shell двічі й вимагала, щоб
     * вписаний CSS відрізнявся. Він НЕ відрізняється, і це не дефект: Mantine
     * вписує змінні обох схем одним блоком (`:root` + селектор темної), а
     * перемикає їх атрибутом на елементі. Тобто твердження перевіряло
     * механізм, якого немає.
     *
     * ⛔ Перевіряти треба інше й важливіше: що резолвер віддав ДВА різні
     * значення. Якби він віддавав однакові, набір a11y ганяв би обидві схеми
     * на одному кольорі й однаково зеленів — а перевірка темної схеми
     * (`W4.3`) і є половина того, заради чого гейт розділено надвоє.
     */
    const values = [...injectedCss().matchAll(/--ecr-surface:\s*([^;]+);/g)].map((m) =>
      m[1]?.trim(),
    );

    expect(values.length).toBeGreaterThanOrEqual(2);
    expect(new Set(values).size).toBeGreaterThanOrEqual(2);
  });

  it('провайдер БЕЗ спільного об_єкта не дає --ecr-* — тобто тест ловить саме це', () => {
    /*
     * ⚠ Самоперевірка: відтворює ту редакцію приладдя, що була до правки.
     * Якби `--ecr-*` вписувала не резолвер, а щось інше (глобальний CSS,
     * `tokens.css`), твердження вище проходили б і без нього — і ця вимога
     * впала б, показавши, що доказ порожній.
     */
    render(
      <MantineProvider theme={mantineProviderProps.theme} forceColorScheme="light">
        <p>зонд</p>
      </MantineProvider>,
    );

    expect(injectedCss()).not.toContain('--ecr-surface');
  });
});
