import type { JSX } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ExpressionEditor } from '../ExpressionEditor';
import { completionAt, completionsFor } from '../completion';
import { EcrApiError } from '@/api/client';
import type { ExpressionMetadataDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * Директива №15 §0, правило `L10`: **відмова ≠ порожньо**.
 *
 * ⛔ Сценарій дефекту. Ефект «Склад мови» (`ExpressionEditor.tsx`, ~183-206 до
 * фіксу) читав `expressionMetadata(dialect, placement)` через `await` БЕЗ
 * `catch`. Відмова давала три наслідки одразу, і жоден із них не видно:
 *
 * 1. неопрацьоване відхилення проміса (`unhandledrejection` у браузері);
 * 2. `metadata.current` лишався `undefined`, а `completionsFor` на `undefined`
 *    повертає ПОРОЖНІЙ перелік — тобто автодоповнення виглядало точнісінько
 *    так, як у діалекті, де функцій справді немає;
 * 3. `applyGrammar` не викликався, тож невідомі імена не підсвічувалися —
 *    навіть друкарська помилка в імені функції лишалася без ознаки.
 *
 * ⚠ Це не «не показали довідник», а «повідомили, що довідник порожній»: автор
 * формули тисне Ctrl+Space, не бачить нічого, робить висновок «підказок тут не
 * буває» і далі пише імена напам'ять — наосліп, при тому що сервер відхилить
 * версію за першим же невідомим ім'ям.
 *
 * ⚠ Дзеркало (випадок Б) обов'язкове: «полагодити» це можна було б банером,
 * що висить ЗАВЖДИ, і випадок Б такий фікс валить.
 */

/**
 * Відмова сервера на `GET /api/v1/expressions/metadata`.
 *
 * ⚠ `messageKey` обов'язковий: без нього `problemText` ховає `detail`
 * (`shared/ui/problemText.ts` — подробиця без ключа каталогу написана чужою
 * мовою), і твердження про текст відмови було б зеленим на будь-якому коді.
 * Прив'язуємось усе одно до КОДУ: він показується завжди й не залежить від
 * того, чи зібрано каталог рядків у тестовому середовищі.
 */
const RefusalCode = 'ECR-SYS-0500';

function metadataRefusal(): EcrApiError {
  return new EcrApiError({
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail: 'склад мови зібрати не вдалося',
    errorCode: RefusalCode,
    correlationId: 'cid-expr-metadata-1',
    extensions2: { messageKey: 'err.ECR-SYS-0500.contactAdmin' },
  });
}

/** Склад мови з однією функцією — рівно стільки, скільки треба дзеркалу. */
function metadataWithRound(): ExpressionMetadataDto {
  return {
    functions: [
      {
        name: 'Round',
        minArgs: 1,
        maxArgs: 2,
        resultType: 'Number',
        acceptsRange: false,
        tier: 'Core',
      },
    ],
    constants: [],
    formulas: [],
    arguments: [],
    headers: [],
  };
}

const metadataMock = vi.fn();

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>();
  return {
    ...actual,
    validateExpression: vi
      .fn()
      .mockResolvedValue({ diagnostics: [], resultType: 'Number', skippedChecks: [] }),
    expressionMetadata: (...args: Parameters<typeof actual.expressionMetadata>) =>
      metadataMock(...args) as ReturnType<typeof actual.expressionMetadata>,
  };
});

/**
 * Мінімальна фейкова Monaco — та сама форма, що в `ExpressionEditor.test.tsx`
 * і `ExpressionEditor.staleOnChange.test.tsx` (`D1-12`: справжній Monaco у
 * jsdom рендериться хвилинами).
 *
 * ⚠ Гачок `__source` віддає ту саму функцію-джерело, яку компонент передає в
 * `prepare`/`applyGrammar`, — тобто РІВНО те замикання, з якого справжній
 * провайдер автодоповнення бере перелік (`monaco.ts`,
 * `registerCompletion`). Через нього перелік підказок перевіряється тим самим
 * шляхом, що й у браузері, без самого Monaco.
 */
vi.mock('../monaco', () => {
  let internalValue = '';
  let changeHandler: (() => void) | null = null;
  let source: (() => ExpressionMetadataDto | undefined) | null = null;
  const model = { getValue: () => internalValue, dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: (_dialect: string, metadataSource: () => ExpressionMetadataDto | undefined) => {
      source = metadataSource;
      return 'ecr-methodology';
    },
    applyGrammar: vi.fn(
      (
        _languageId: string,
        _dialect: string,
        metadataSource: () => ExpressionMetadataDto | undefined,
      ) => {
        source = metadataSource;
      },
    ),
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    /*
     * ⚠ Фейк САДИТЬ у вузол справжню `<textarea>` з тією ж доступною назвою,
     * що й Monaco (`monaco.ts`, `create`: `ariaLabel`). Без цього поле
     * існувало б лише як замикання мока, і твердження «редактор лишився на
     * екрані» було б зеленим навіть тоді, коли вузол-господар не змонтовано
     * ЗОВСІМ: мутація «сховати редактор за банером» не червоніла (перевірено).
     */
    create: (host: HTMLElement, options: { value: string; ariaLabel: string }) => {
      internalValue = options.value;

      const area = host.ownerDocument.createElement('textarea');
      area.setAttribute('aria-label', options.ariaLabel);
      area.value = options.value;
      host.appendChild(area);

      return {
        getValue: () => internalValue,
        setValue: (next: string) => {
          internalValue = next;
          area.value = next;
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          changeHandler = handler;
        },
        dispose: () => area.remove(),
      };
    },
    /** Імітує фізичне натискання клавіші в редакторі. */
    __typeIntoEditor: (text: string) => {
      internalValue = text;
      changeHandler?.();
    },
    /** Джерело складу мови, яким користуються провайдери підказок. */
    __source: () => source?.(),
    __reset: () => {
      internalValue = '';
      changeHandler = null;
      source = null;
    },
  };
});

const monacoMock = (await import('../monaco')) as unknown as {
  applyGrammar: ReturnType<typeof vi.fn>;
  __typeIntoEditor: (text: string) => void;
  __source: () => ExpressionMetadataDto | undefined;
  __reset: () => void;
};

/**
 * Перелік підказок функцій — тим самим шляхом, що й у браузері:
 * `completionAt` розбирає позицію курсора, `completionsFor` фільтрує склад
 * мови, взятий із замикання, зареєстрованого компонентом.
 */
function functionHints(typed: string): readonly string[] {
  const context = completionAt(typed, typed.length);
  if (context === null) return [];

  return completionsFor(context, monacoMock.__source()).map((item) => item.label);
}

function editorFor(onChange: (value: string) => void): JSX.Element {
  return (
    <MantineProvider theme={testTheme}>
      <ExpressionEditor value="" onChange={onChange} dialect="Methodology" ariaLabel="вираз" />
    </MantineProvider>
  );
}

/** Дає змонтуватися мокнутому `await import('./monaco')` і ефекту складу мови. */
async function settle(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

beforeEach(() => {
  metadataMock.mockReset();
  monacoMock.applyGrammar.mockClear();
  monacoMock.__reset();
});

describe('ExpressionEditor: відмова складу мови ≠ «функцій немає» (L10)', () => {
  it('А: запит складу мови відмовив — на екрані причина з кодом, редактор працює', async () => {
    metadataMock.mockRejectedValue(metadataRefusal());

    const onChange = vi.fn();
    render(editorFor(onChange));
    await settle();

    /*
     * ⛔ Банер — ПЕРШЕ твердження, і саме `await`. Якби спершу перевірявся
     * редактор, тест був би зеленим на невиправленому коді: редактор там
     * теж працює — мовчки і без підказок.
     *
     * ⚠ Пошук за КОДОМ, а не за `role="alert"`: Mantine `Alert` ставить цю
     * роль і в інших банерах, тож роль не відрізняє потрібний.
     */
    // ⚠ Стеля 2 с, а не типова: на робочому коді банер з'являється за
    // мікрозадачі, і довга стеля лише сповільнювала б ЧЕРВОНИЙ прогін.
    const code = await screen.findByText(RefusalCode, undefined, { timeout: 2000 });
    expect(code).not.toBeNull();
    expect(screen.getByText('cid-expr-metadata-1')).not.toBeNull();

    // ⛔ Редактор лишається робочим: писати вираз без підказок законно —
    // перевіряє все одно сервер. Ховати поле через недоступний довідник
    // означало б відібрати саму можливість роботи.
    //
    // ⚠ Спершу поле МАЄ БУТИ в документі, і лише потім — що воно приймає
    // введення: слухач `onDidChangeModelContent` живе в замиканні й
    // спрацьовує навіть тоді, коли вузол уже прибрано з DOM.
    expect(screen.getByLabelText('вираз')).not.toBeNull();

    await act(async () => {
      monacoMock.__typeIntoEditor('Round(1)');
    });

    expect(onChange).toHaveBeenCalledWith('Round(1)');

    // Довідник справді порожній — але тепер сказано ЧОМУ, і це вся різниця.
    expect(functionHints('Ro')).toEqual([]);
  });

  it('Б: склад мови приїхав — банера немає, підказки є', async () => {
    metadataMock.mockResolvedValue(metadataWithRound());

    render(editorFor(vi.fn()));
    await settle();

    await waitFor(() => expect(monacoMock.applyGrammar).toHaveBeenCalled(), { timeout: 2000 });

    // ⛔ Дзеркало: банер, що висить завжди, — не фікс, а друга неправда.
    expect(screen.queryByText(RefusalCode)).toBeNull();

    // Підказки беруться з того самого замикання, що й у справжньому провайдері.
    expect(functionHints('Ro')).toEqual(['Round']);
  });

  it('В: «повторити» після відмови знімає банер і повертає підказки', async () => {
    metadataMock.mockRejectedValueOnce(metadataRefusal());
    metadataMock.mockResolvedValue(metadataWithRound());

    render(editorFor(vi.fn()));
    await settle();

    await screen.findByText(RefusalCode, undefined, { timeout: 2000 });

    // ⛔ Тупикових екранів не буває (`ФВ-14.24`): дія «повторити» має
    // перезапускати САМЕ цей запит, а не бути написом на банері.
    await act(async () => {
      fireEvent.click(screen.getByRole('button'));
    });

    await waitFor(() => expect(screen.queryByText(RefusalCode)).toBeNull(), { timeout: 2000 });
    expect(functionHints('Ro')).toEqual(['Round']);
  });
});
