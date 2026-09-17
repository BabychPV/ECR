import { useEffect, useState, type JSX } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ExpressionEditor } from '../ExpressionEditor';

/**
 * Аудит 2026-09-16, §10.3: гонка між асинхронним завантаженням Monaco і
 * приходом тексту формули з сервера.
 *
 * ⛔ Ефект створення редактора має ПОРОЖНІЙ перелік залежностей (і має бути
 * таким — перестворення скидало б курсор на кожній літері), тож `value`,
 * прочитаний у його тілі, — це `value` НА МОМЕНТ МОНТУВАННЯ. Ефект
 * синхронізації зовнішньої зміни (`[value]`) при `editor.current === null`
 * (Monaco ще не завантажено) тихо не робить нічого і більше не
 * перезапускається: `ready` не входив у його залежності.
 *
 * **Сценарій:** форма відкриває редактор із `value=""`, поки асинхронно
 * вантажить текст формули. Чанк Monaco важить ~818 КБ gzip — через
 * корпоративний канал він легко приходить ПІЗНІШЕ за короткий запит формули.
 * Тоді `value` встигає змінитися з `""` на реальний текст ДО того, як Monaco
 * завантажився, редактор створюється з застарілим `""`, і користувач бачить
 * порожнє поле під заповненою формулою. Перше ж натискання клавіші тихо
 * перезаписує правильний стан батька майже порожнім змістом редактора.
 *
 * ⚠ Ключ тесту — ФАБРИКА мока `../monaco` затримана обіцянкою, яку відкриває
 * сам тест. Це і є «чанк іде довше за запит»: `await import('./monaco')`
 * усередині ефекту лишається нерозв'язаним рівно доти, доки тест не скаже
 * інакше, — так само, як його тримав би повільний канал.
 *
 * ⛔ «Канал» і стан фальшивого редактора — ПО ОДНОМУ НА ТЕСТ, і це умова
 * доказу, а не охайність. Обидва тести пишуть у `state.value` (другий
 * дописує туди пробіл) і обидва відкривають канал; одна спільна обіцянка на
 * весь файл означала б, що доказ лишається доказом лише в тому порядку, у
 * якому тести написані. На зворотному порядку перший тест бачив у
 * `state.value` хвіст другого (`AssertionError: expected 'SUM(C1:C12) * 1000 '
 * to be ''`), а канал був уже відкритий ДО монтування — тобто гонки, яку тест
 * називає своєю темою, не відбувалося взагалі. `vi.resetModules()` тут
 * обов'язковий разом зі скиданням: без нього `../monaco` лишається в реєстрі
 * з першого тесту, фабрика більше не виконується, і новий «канал» ніхто не
 * чекає.
 */
const hoisted = vi.hoisted(() => {
  const state = { value: '', changeHandler: null as (() => void) | null };

  let open = (): void => {};
  let chunk = Promise.resolve();

  const reset = (): void => {
    state.value = '';
    state.changeHandler = null;
    chunk = new Promise<void>((resolve) => {
      open = resolve;
    });
  };

  reset();

  return { chunk: () => chunk, openChunk: () => open(), state, reset };
});

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>();

  return {
    ...actual,
    validateExpression: vi
      .fn()
      .mockResolvedValue({ diagnostics: [], resultType: 'Number', skippedChecks: [] }),
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [] }),
  };
});

vi.mock('../monaco', async () => {
  // ⛔ Саме тут живе гонка: доки тест не відкриє «канал», динамічний імпорт
  // редактора не завершується.
  await hoisted.chunk();

  const model = { getValue: () => hoisted.state.value, dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: (_host: HTMLElement, options: { value?: string }) => {
      hoisted.state.value = options.value ?? '';

      return {
        getValue: () => hoisted.state.value,
        setValue: (next: string) => {
          hoisted.state.value = next;
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          hoisted.state.changeHandler = handler;
        },
        dispose: () => {},
      };
    },
  };
});

/** Текст формули, який «повертає сервер» уже після монтування редактора. */
const Formula = 'SUM(C1:C12) * 1000';

/**
 * Той самий рисунок, що й справжня форма формули: редактор монтується з
 * порожнім значенням, текст приходить із сервера окремим запитом.
 */
function FormulaDialog(): JSX.Element {
  const [expression, setExpression] = useState('');

  useEffect(() => {
    // Короткий запит формули — завершується РАНІШЕ за чанк Monaco.
    setExpression(Formula);
  }, []);

  return (
    <>
      <ExpressionEditor
        value={expression}
        onChange={setExpression}
        dialect="Methodology"
        ariaLabel="вираз"
      />
      <output aria-label="parent-value">{expression}</output>
    </>
  );
}

describe('ExpressionEditor: текст, що прийшов до завантаження Monaco, не губиться (§10.3)', () => {
  beforeEach(() => {
    // Спершу реєстр, потім стан: `resetModules` лише викидає `../monaco` з
    // кешу, а чекати новий тест буде ту обіцянку, яку заведе `reset`.
    vi.resetModules();
    hoisted.reset();
  });

  it('редактор показує формулу, а не порожнечу, з якою його змонтували', async () => {
    render(
      <MantineProvider>
        <FormulaDialog />
      </MantineProvider>,
    );

    // Формула вже в стані батька — редактора ще НЕМА.
    expect(screen.getByLabelText('parent-value').textContent).toBe(Formula);
    expect(hoisted.state.value).toBe('');

    // І лише тепер доїжджає чанк Monaco.
    await act(async () => {
      hoisted.openChunk();
      await Promise.resolve();
      await Promise.resolve();
    });

    // ⛔ Мутаційний доказ (RED до фіксу): редактор створювався з `value`,
    // захопленим на монтуванні, тобто з `''`, а ефект `[value]` уже
    // відпрацював на `null`-редакторі й не повторювався — поле лишалося
    // порожнім назавжди.
    expect(hoisted.state.value).toBe(Formula);
  });

  it('перше натискання клавіші не стирає формулу батька', async () => {
    render(
      <MantineProvider>
        <FormulaDialog />
      </MantineProvider>,
    );

    await act(async () => {
      hoisted.openChunk();
      await Promise.resolve();
      await Promise.resolve();
    });

    // Користувач дописує символ у редактор — той самий шлях, яким Monaco
    // повідомляє про зміну змісту.
    await act(async () => {
      hoisted.state.value = `${hoisted.state.value} `;
      hoisted.state.changeHandler?.();
      await Promise.resolve();
    });

    // ⛔ До фіксу редактор містив `''`, тож перший же натиск віддавав батькові
    // майже порожній рядок — і правильна формула зникала без слідів.
    expect(screen.getByLabelText('parent-value').textContent).toBe(`${Formula} `);
  });
});
