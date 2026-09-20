import { useState, type JSX } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { FormulaEditor } from '../FormulaEditor';
import type { FormulaDraft } from '../formula';
import { testTheme } from '@/test/render';

/**
 * Борг: `placement` у `FormulaEditor.tsx` будувався об'єктним літералом просто
 * в тілі компонента — НОВИМ посиланням на кожен перерендер.
 *
 * ⛔ Чому це коштує мережі, а не лише процесора. `ExpressionEditor` тримає
 * `placement` у переліку залежностей ОБОХ своїх асинхронних ефектів —
 * складу мови (`GET /expressions/metadata`) і перевірки при введенні
 * (`POST /expressions/validate`) — і звіряє їх за посиланням. Новий літерал
 * означає «розміщення змінилося», тобто перезапуск обох ефектів і два зайві
 * звернення до сервера на КОЖЕН перерендер форми — включно з тим, що його
 * спричинило сусіднє поле, яке до виразу не має стосунку. Два інші викликачі
 * (`ExpressionsPage.tsx`, `MethodologyVersionsPage.tsx`) мемоізують розміщення
 * саме тому.
 *
 * ⚠ Твердження тесту — про ЗАПИТИ, а не про рендери. «Менше рендерів» —
 * не та обіцянка: рендер тут безкоштовний, а звернення до сервера — ні.
 * Лічильники стоять на замоканих `expressionMetadata`/`validateExpression`,
 * тобто рівно на тих функціях, що ходять у мережу (`expressions/api.ts`).
 */

const metadataMock = vi.fn();
const validateMock = vi.fn();

vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();
  return {
    ...actual,
    expressionMetadata: (...args: Parameters<typeof actual.expressionMetadata>) =>
      metadataMock(...args) as ReturnType<typeof actual.expressionMetadata>,
    validateExpression: (...args: Parameters<typeof actual.validateExpression>) =>
      validateMock(...args) as ReturnType<typeof actual.validateExpression>,
  };
});

/** Мінімальна фейкова Monaco — та сама форма, що в сусідніх файлах (`D1-12`). */
vi.mock('@/features/expressions/monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-template',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: (host: HTMLElement, options: { value: string; ariaLabel: string }) => {
      let value = options.value;

      const area = host.ownerDocument.createElement('textarea');
      area.setAttribute('aria-label', options.ariaLabel);
      area.value = options.value;
      host.appendChild(area);

      return {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          area.value = next;
        },
        getModel: () => model,
        onDidChangeModelContent: () => {},
        dispose: () => {
          area.remove();
        },
      };
    },
  };
});

/**
 * ⛔ Чернетка створюється ОДИН раз і поза компонентом: якби її перестворював
 * кожен рендер, тест міряв би власну ваду, а не ваду `FormulaEditor`.
 */
const draft: FormulaDraft = {
  tableDefId: 12,
  scope: 'Column',
  target: '34',
  dialect: 'Template',
  expression: 'SUM([Jan])',
  isNew: true,
};

/**
 * Форма, у якій редактор формули стоїть ПОРУЧ із іншим полем — саме той
 * розклад, що й у справжньому екрані шаблону. Набір у сусідньому полі піднімає
 * стан батька, тобто перерендерює `FormulaEditor` із ТИМИ САМИМИ пропсами.
 */
function Harness(): JSX.Element {
  const [note, setNote] = useState('');

  return (
    <MantineProvider theme={testTheme}>
      <input
        aria-label="сусіднє поле"
        value={note}
        onChange={(event) => setNote(event.currentTarget.value)}
      />
      <FormulaEditor
        draft={draft}
        templateVersionId={7}
        disabled={false}
        saving={false}
        onChange={() => {}}
        onSubmit={() => {}}
        onCancel={() => {}}
      />
    </MantineProvider>
  );
}

/** Пауза, довша за `ValidateDelay` (400 мс) — щоб відкладена перевірка пішла. */
async function pastValidateDelay(): Promise<void> {
  await act(async () => {
    await new Promise((r) => setTimeout(r, 450));
  });
}

beforeEach(() => {
  metadataMock.mockReset();
  validateMock.mockReset();

  metadataMock.mockResolvedValue({
    functions: [],
    constants: [],
    formulas: [],
    arguments: [],
    headers: [],
  });
  validateMock.mockResolvedValue({ diagnostics: [], resultType: 'Number', skippedChecks: [] });
});

describe('FormulaEditor: набір у сусідньому полі не шле запитів за вираз', () => {
  it('три натискання клавіші поруч — склад мови і перевірка запитані по одному разу', async () => {
    render(<Harness />);

    // Монтування: `await import('./monaco')` (мокнутий) → `ready` → обидва
    // ефекти по одному разу. Це базовий рівень, а не зайві запити.
    await pastValidateDelay();
    await waitFor(() => expect(metadataMock).toHaveBeenCalledTimes(1), { timeout: 2000 });
    await waitFor(() => expect(validateMock).toHaveBeenCalledTimes(1), { timeout: 2000 });

    const neighbour = screen.getByLabelText('сусіднє поле');

    // ⚠ Пауза після КОЖНОГО натискання довша за `ValidateDelay`: інакше
    // перезапущений таймер поглинав би повтори й ховав половину дефекту.
    for (const text of ['а', 'аб', 'абв']) {
      await act(async () => {
        fireEvent.change(neighbour, { target: { value: text } });
      });
      await pastValidateDelay();
    }

    /*
     * ⛔ Головне твердження. Жодне з трьох натискань не стосується виразу —
     * текст формули не змінювався жодного разу, — тож серверу не було чого
     * сказати нового ані про склад мови, ані про перевірку.
     *
     * На невиправленому коді (літерал `placement` у тілі компонента) тут
     * 4 і 4: один запит за монтування плюс по одному на КОЖЕН перерендер,
     * тобто три зайві звернення до кожної з двох кінцевих точок.
     */
    expect(metadataMock).toHaveBeenCalledTimes(1);
    expect(validateMock).toHaveBeenCalledTimes(1);

    // ⚠ Дзеркало мемоізації: редактор не «завмер» — розміщення таки зібране
    // і передане, просто одним і тим самим об'єктом.
    expect(metadataMock.mock.calls[0]?.slice(0, 2)).toEqual([
      'Template',
      { templateVersionId: 7, tableDefId: 12, columnDefId: 34 },
    ]);

    // ⚠ Стеля тесту — 20 с при фактичних ~2.5 с. Чотири паузи по 450 мс тут
    // обов'язкові (інакше таймер перевірки поглинав би повтори), і дефолтні
    // 5 с vitest лишали б на монтування й прибирання надто малий запас.
  }, 20000);
});
