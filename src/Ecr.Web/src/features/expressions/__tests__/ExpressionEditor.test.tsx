import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ExpressionEditor } from '../ExpressionEditor';
import type { ExpressionValidationDto } from '@/api/types';

/**
 * Застаріла перевірка при швидкому наборі (`Q-253`).
 *
 * ⛔ Ефект «Перевірка при введенні» (`ExpressionEditor.tsx`, ~161-185) не мав
 * жодного захисту від відповіді, що приходить ПІСЛЯ новішої: немає ані
 * `AbortController`, ані прапорця `disposed`, ані звірки з поточним `value`.
 * Двоє інших асинхронних ефектів у тому самому файлі (створення редактора,
 * склад мови) вже мають прапорець `disposed` — цей був єдиним винятком, і
 * саме тим, що перезапускається найчастіше (кожна пауза в наборі). Наслідок:
 * повільніший запит для СТАРОГО тексту, що завершується ПІЗНІШЕ за запит для
 * НОВОГО, мовчки переписував і підкреслення в редакторі, і `onValidated`, який
 * `ExpressionsPage.tsx` рендерить напряму як перелік знахідок.
 *
 * Тест мокає `validateExpression` і навмисно резолвить ПЕРШИЙ виклик (для
 * старішого тексту) ПІСЛЯ другого (для новішого) — точнісінько та гонитва, що
 * й ламала екран. Мутаційний доказ (RED → GREEN) — у самому файлі: без
 * `AbortController` (`Q-253`, коментар нижче) цей тест падає, бо застарілий
 * результат перезаписує свіжий.
 */

const validateExpressionMock = vi.fn();

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>();
  return {
    ...actual,
    validateExpression: (...args: Parameters<typeof actual.validateExpression>) =>
      validateExpressionMock(...args) as ReturnType<typeof actual.validateExpression>,
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [] }),
  };
});

/** Мінімальна фейкова Monaco — без реального редактора Monaco в jsdom (`D1-12`). */
vi.mock('../monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: () => {
      let value = '';
      let changeHandler: (() => void) | null = null;

      return {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          changeHandler?.();
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          changeHandler = handler;
        },
        dispose: () => {},
      };
    },
  };
});

const { showDiagnostics } = await import('../monaco');

/** Контрольована обіцянка — тест сам вирішує, коли й у якому порядку резолвити. */
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

function resultFor(marker: string): ExpressionValidationDto {
  return {
    diagnostics: [],
    resultType: marker,
    skippedChecks: [],
  } as unknown as ExpressionValidationDto;
}

afterEach(() => {
  vi.mocked(validateExpressionMock).mockReset();
  vi.mocked(showDiagnostics).mockClear();
});

describe('ExpressionEditor: перевірка при введенні не показує застарілий результат', () => {
  it('запит для СТАРОГО тексту, що завершується ПІЗНІШЕ за новий — не переписує результат', async () => {
    const first = deferred<ExpressionValidationDto>();
    const second = deferred<ExpressionValidationDto>();

    validateExpressionMock.mockImplementationOnce(() => first.promise);
    validateExpressionMock.mockImplementationOnce(() => second.promise);

    const onValidated = vi.fn();

    const { rerender } = render(
      <MantineProvider>
        <ExpressionEditor
          value="A"
          onChange={() => {}}
          dialect="Methodology"
          ariaLabel="вираз"
          onValidated={onValidated}
        />
      </MantineProvider>,
    );

    // Даємо редактору змонтуватися (`await import('./monaco')`, мокнутий) і
    // ефекту перевірки спрацювати для початкового значення "A".
    await act(async () => {
      await new Promise((r) => setTimeout(r, 450));
    });

    await waitFor(() => expect(validateExpressionMock).toHaveBeenCalledTimes(1));
    // ⚠ Перевіряємо лише перші три аргументи: `signal` (четвертий) — саме те,
    // чого немає на невиправленому коді (`Q-253`), тож сувора перевірка його
    // наявності зробила б цей тест червоним із НЕПРАВИЛЬНОЇ причини раніше,
    // ніж дійде до головного твердження нижче.
    expect(validateExpressionMock.mock.calls[0]?.slice(0, 3)).toEqual(['A', 'Methodology', {}]);

    // Користувач дописує текст — рівно те, що й раніше, лише швидше за
    // `ValidateDelay` (тому попередній запит ще НЕ пішов би в реальному
    // наборі; тут він уже пішов, і саме тому цей сценарій — найгірший:
    // запит для "A" вже в польоті, коли текст став "AB").
    rerender(
      <MantineProvider>
        <ExpressionEditor
          value="AB"
          onChange={() => {}}
          dialect="Methodology"
          ariaLabel="вираз"
          onValidated={onValidated}
        />
      </MantineProvider>,
    );

    await act(async () => {
      await new Promise((r) => setTimeout(r, 450));
    });

    await waitFor(() => expect(validateExpressionMock).toHaveBeenCalledTimes(2));
    expect(validateExpressionMock.mock.calls[1]?.slice(0, 3)).toEqual(['AB', 'Methodology', {}]);

    // Гонитва: НОВИЙ запит ("AB") завершується ПЕРШИМ, старий ("A") — другим.
    await act(async () => {
      second.resolve(resultFor('AB'));
      await Promise.resolve();
    });

    await act(async () => {
      first.resolve(resultFor('A'));
      await Promise.resolve();
      await Promise.resolve();
    });

    // ⛔ Головне твердження: останній виклик `onValidated` має відповідати
    // ОСТАННЬОМУ введеному тексту ("AB"), а не тому, що прилетів останнім
    // фізично ("A"). До фіксу (`Q-253`) застарілий результат переписував
    // свіжий БЕЗУМОВНО — цей тест падає на невиправленому коді.
    const lastCall = onValidated.mock.calls.at(-1)?.[0] as ExpressionValidationDto | undefined;
    expect(lastCall?.resultType).toBe('AB');
    expect(lastCall?.resultType).not.toBe('A');
  });
});
