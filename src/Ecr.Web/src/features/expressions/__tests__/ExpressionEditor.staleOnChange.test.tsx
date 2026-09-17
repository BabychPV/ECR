import { useState, type JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider, TextInput } from '@mantine/core';
import { ExpressionEditor } from '../ExpressionEditor';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 5: «Formula dialog silently wipes the "Code" field the
 * moment you edit the expression» (`docs/build/audit-drafts/lane5-formula-
 * code-field-cleared-on-expression-edit.md`).
 *
 * ⛔ Корінь — ефект створення редактора (`ExpressionEditor.tsx`, useEffect із
 * порожнім переліком залежностей) реєструє `onDidChangeModelContent`
 * ОДИН раз. Замикання цього слухача захоплювало САМ `onChange`, переданий
 * пропом, — а виклики форми типово передають інлайн-стрілку
 * (`(value) => setEditing({ ...editing, expression: value })`), що читає
 * ЗІ СВОГО замикання поточний `editing` НА МОМЕНТ монтування редактора
 * (порожній чернетковий код). Кожен наступний символ, набраний у виразі,
 * викликав ЦЮ застарілу стрілку — і вона переписувала актуальний стан
 * (з уже введеним кодом) застарілим (без коду), стираючи код мовчки.
 *
 * ⚠ Monaco мокнуто — jsdom не рендерить його canvas/contentEditable
 * (`D1-12`, той самий прийом, що й `ExpressionEditor.test.tsx`). Тестовий
 * гачок `__typeIntoEditor`, доданий у мокнутий модуль, викликає САМЕ ТОЙ
 * `onDidChangeModelContent`-слухач, що його реєструє компонент, — тобто ту
 * саму гілку коду, що й реальне натискання клавіші в Monaco.
 */
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

vi.mock('../monaco', () => {
  let internalValue = '';
  let changeHandler: (() => void) | null = null;
  const model = { getValue: () => internalValue, dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: () => ({
      getValue: () => internalValue,
      setValue: (next: string) => {
        internalValue = next;
      },
      getModel: () => model,
      onDidChangeModelContent: (handler: () => void) => {
        changeHandler = handler;
      },
      dispose: () => {},
    }),
    // Тестовий гачок: імітує фізичне натискання клавіші в редакторі.
    __typeIntoEditor: (text: string) => {
      internalValue = text;
      changeHandler?.();
    },
  };
});

interface Draft {
  code: string;
  expression: string;
}

/**
 * Той самий рисунок, що й діалог формули методології
 * (`MethodologyVersionsPage.tsx`): поле "Code" над `ExpressionEditor`,
 * обидва — частини одного об'єкта стану `editing`, `onChange` кожного
 * поля — інлайн-стрілка, що спредить поточний `editing`.
 */
function FormulaDialog(): JSX.Element {
  const [editing, setEditing] = useState<Draft>({ code: '', expression: '' });

  return (
    <>
      <TextInput
        aria-label="Code"
        value={editing.code}
        onChange={(event) => setEditing({ ...editing, code: event.currentTarget.value })}
      />
      <ExpressionEditor
        value={editing.expression}
        onChange={(value) => setEditing({ ...editing, expression: value })}
        dialect="Methodology"
        ariaLabel="вираз"
      />
      <output aria-label="code-echo">{editing.code}</output>
    </>
  );
}

describe('ExpressionEditor: правка виразу не стирає інші поля форми (lane5)', () => {
  it('редагування виразу ПІСЛЯ введення коду лишає код на місці', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <FormulaDialog />
      </MantineProvider>,
    );

    // Дати редактору змонтуватися (мокнутий `await import('./monaco')`).
    await act(async () => {
      await Promise.resolve();
    });

    fireEvent.change(screen.getByLabelText('Code'), { target: { value: 'LANE5_F2' } });
    expect(screen.getByLabelText('code-echo').textContent).toBe('LANE5_F2');

    // Користувач клікає в редактор виразу й тисне один символ.
    const monaco = (await import('../monaco')) as unknown as {
      __typeIntoEditor: (text: string) => void;
    };
    await act(async () => {
      monaco.__typeIntoEditor(')');
    });

    // ⛔ Мутаційний доказ (RED на невиправленому коді): без `onChangeRef`
    // тут повернулося б '' — застаріле замикання, зафіксоване на монтуванні
    // редактора (коли `editing.code` ще був порожній), переписало б код.
    expect(screen.getByLabelText('code-echo').textContent).toBe('LANE5_F2');
  });
});
