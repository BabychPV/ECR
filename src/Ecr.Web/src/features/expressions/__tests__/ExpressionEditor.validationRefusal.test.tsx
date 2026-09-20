import type { JSX } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ExpressionEditor } from '../ExpressionEditor';
import { EcrApiError } from '@/api/client';
import { testTheme } from '@/test/render';

/**
 * Директива №15 §0, правило `L10`: **відмова ≠ порожньо** — тепер для другого
 * запиту редактора.
 *
 * ⛔ Сценарій дефекту. Ефект «Перевірка при введенні» (`ExpressionEditor.tsx`)
 * ловив помилку `validateExpression` і робив із нею `throw error` — усередині
 * `void (async () => { … })()`. Кинуте звідти не доходить до ЖОДНОЇ межі
 * помилок React: async-IIFE нікому не віддає свій проміс, тож це неопрацьоване
 * відхилення (`unhandledrejection` у браузері; у тестах — «шум», який нічого
 * не валить). На екрані — рівно ніщо.
 *
 * ⚠ І це «ніщо» гірше за порожній екран: `POST /expressions/validate` —
 * ЄДИНЕ джерело підкреслень. Відповіді немає → `showDiagnostics` не
 * викликано → поле виглядає точнісінько так, як на бездоганній формулі. Автор
 * читає чисте поле як «помилок немає», хоча насправді ніхто нічого не
 * перевіряв, і дізнається правду аж на збереженні версії.
 *
 * ⚠ Дзеркало (випадок Б) обов'язкове: «полагодити» це можна було б банером на
 * будь-яку відмову промісу — а скасування (`AbortError`) тут не виняткова
 * подія, а НОРМА швидкого набору (кожна зміна тексту скасовує попередній
 * запит). Такий «фікс» давав би банер на кожне натискання клавіші, і випадок Б
 * його валить.
 */

/**
 * Відмова сервера на `POST /api/v1/expressions/validate`.
 *
 * ⚠ `messageKey` обов'язковий: без нього `problemText` ховає `detail`
 * (`shared/ui/problemText.ts`). Прив'язуємось усе одно до КОДУ — він
 * показується завжди і не залежить від зібраного каталогу рядків.
 */
const RefusalCode = 'ECR-SYS-0500';

function validationRefusal(): EcrApiError {
  return new EcrApiError({
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail: 'перевірити вираз не вдалося',
    errorCode: RefusalCode,
    correlationId: 'cid-expr-validate-1',
    extensions2: { messageKey: 'err.ECR-SYS-0500.contactAdmin' },
  });
}

/**
 * Скасування запиту — те, що `fetch` кидає на `AbortController.abort()`.
 *
 * ⛔ Код відмови тут — НАВМИСНА принада, а не реалізм. Сам продукт відрізняє
 * скасування від відмови ЛИШЕ за `error.name === 'AbortError'`
 * (`isAbortError`), тож для предиката ця помилка невідрізненна від справжнього
 * `DOMException`. А код дає дзеркалу прив'язатися до банера точно: `role`
 * `alert` у Mantine носить не лише потрібний банер, і шукати за роллю означало
 * б твердження, яке не відрізняє один банер від іншого.
 *
 * ⚠ Код мусить бути з КАТАЛОГУ (`ErrorCodes.cs`), хоч він тут і принада:
 * сторож `ClientErrorCodeTests.Клієнт_не_згадує_кодів_яких_немає_в_каталозі`
 * читає ВЕСЬ `src/Ecr.Web/src`, не розрізняючи продукт і тест. Вигаданий
 * `ECR-SYS-0499` червонив гейти `test` і `server` на спільній гілці. Беремо
 * `ECR-SYS-0503` — чинний код, і саме тому, що він НЕ той, що в `refusal()`:
 * дзеркало й далі відрізняє скасування від відмови.
 */
const AbortDecoyCode = 'ECR-SYS-0503';

function abortRefusal(): EcrApiError {
  const error = new EcrApiError({
    type: 'about:blank',
    title: 'Aborted',
    status: 499,
    detail: 'запит скасовано',
    errorCode: AbortDecoyCode,
    correlationId: 'cid-expr-validate-abort',
    extensions2: { messageKey: 'err.ECR-SYS-0503.contactAdmin' },
  });

  error.name = 'AbortError';
  return error;
}

const validateMock = vi.fn();

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>();
  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({
      functions: [],
      constants: [],
      formulas: [],
      arguments: [],
      headers: [],
    }),
    validateExpression: (...args: Parameters<typeof actual.validateExpression>) =>
      validateMock(...args) as ReturnType<typeof actual.validateExpression>,
  };
});

/** Мінімальна фейкова Monaco — та сама форма, що в сусідніх файлах (`D1-12`). */
vi.mock('../monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    /*
     * ⚠ Фейк САДИТЬ у вузол справжню `<textarea>` з тією ж доступною назвою,
     * що й Monaco. Без цього твердження «редактор лишився на екрані» було б
     * зеленим навіть тоді, коли вузол-господар не змонтовано зовсім, і
     * мутація «сховати редактор за банером» не червоніла б.
     */
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

function editorFor(value: string): JSX.Element {
  return (
    <MantineProvider theme={testTheme}>
      <ExpressionEditor
        value={value}
        onChange={() => {}}
        dialect="Methodology"
        ariaLabel="вираз"
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
  validateMock.mockReset();
});

describe('ExpressionEditor: відмова перевірки ≠ «помилок немає» (L10)', () => {
  it('А: перевірка відмовила — на екрані причина з кодом, редактор лишається робочим', async () => {
    validateMock.mockRejectedValue(validationRefusal());

    render(editorFor('SUM(1)'));
    await pastValidateDelay();

    /*
     * ⛔ Банер — ПЕРШЕ твердження. Якби спершу перевірявся редактор, тест був
     * би зеленим і на невиправленому коді: поле там теж на місці — мовчки і
     * без підкреслень.
     *
     * ⚠ Пошук за КОДОМ, а не за `role="alert"`: цю роль носять й інші банери.
     */
    // ⚠ Стеля 2 с, а не типова: на робочому коді банер з'являється за
    // мікрозадачі після відповіді, і довга стеля лише сповільнювала б ЧЕРВОНИЙ
    // прогін — ба більше, перевищивши дефолтні 5 с тесту, вона перетворила б
    // зрозуміле «елемента з таким текстом немає» на глухе «Test timed out».
    const code = await screen.findByText(RefusalCode, undefined, { timeout: 2000 });
    expect(code).not.toBeNull();
    expect(screen.getByText('cid-expr-validate-1')).not.toBeNull();

    // ⛔ Писати вираз без перевірки законно: банер стоїть ПОРУЧ із полем, а не
    // замість нього. Сховати чи заблокувати редактор через недоступну
    // перевірку означало б відібрати роботу замість того, щоб назвати причину.
    expect(screen.getByLabelText('вираз')).not.toBeNull();
  });

  it('Б: запит скасовано (звичайний стан швидкого набору) — банера НЕМАЄ', async () => {
    /*
     * Мок поводиться як справжній `fetch` із сигналом: не завершується сам, а
     * відхиляється рівно тоді, коли редактор скасує запит зі свого прибирання.
     */
    validateMock.mockImplementation(
      (_expression: string, _dialect: string, _placement: unknown, signal: AbortSignal) =>
        new Promise((_resolve, reject) => {
          signal.addEventListener('abort', () => {
            reject(abortRefusal());
          });
        }),
    );

    const { rerender } = render(editorFor('A'));
    await pastValidateDelay();
    await waitFor(() => expect(validateMock).toHaveBeenCalledTimes(1), { timeout: 2000 });

    // Користувач допечатує — попередній запит скасовується прибиранням ефекту.
    rerender(editorFor('AB'));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await new Promise((r) => setTimeout(r, 50));
    });

    /*
     * ⛔ Дзеркало: банер на кожне натискання клавіші — не фікс, а друга
     * неправда замість першої. Скасування — норма, а не відмова.
     */
    expect(screen.queryByText(AbortDecoyCode)).toBeNull();
    expect(screen.getByLabelText('вираз')).not.toBeNull();
  });

  it('В: «повторити» після відмови знімає банер', async () => {
    validateMock.mockRejectedValueOnce(validationRefusal());
    validateMock.mockResolvedValue({ diagnostics: [], resultType: 'Number', skippedChecks: [] });

    render(editorFor('SUM(1)'));
    await pastValidateDelay();
    await screen.findByText(RefusalCode, undefined, { timeout: 2000 });

    // ⛔ Тупикових екранів не буває (`ФВ-14.24`): «повторити» має перезапускати
    // САМЕ цей запит, а не бути написом на банері.
    await act(async () => {
      fireEvent.click(screen.getByRole('button'));
    });

    await pastValidateDelay();
    await waitFor(() => expect(screen.queryByText(RefusalCode)).toBeNull(), { timeout: 2000 });
  });
});
