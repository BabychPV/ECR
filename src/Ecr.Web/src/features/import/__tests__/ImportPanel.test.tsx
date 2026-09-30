import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';
import { showDone } from '@/shared/ui/notify';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * Діалог перегляду імпорту (`V-10`).
 *
 * ⚠ Відповідь прев'ю підмінено на рівні `fetch`: предмет — те, що діалог
 * ПОКАЗУЄ з готової відповіді сервера, а не те, як сервер її будує (це —
 * `ImportRoundTripScenarios` на справжньому SQL Server).
 */

function mockPreview(preview: ImportPreview): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/import/preview')) {
        return new Response(JSON.stringify(preview), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

async function openPreview(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={1} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));
  await screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ImportPanel: перелік змін і відмов', () => {
  it('рядок зміни називає ТАБЛИЦЮ — ключі R1/C1 однакові в десятках таблиць', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [
        {
          rowKey: 'R1',
          columnCode: 'C1',
          oldValue: '5',
          newValue: '6',
          tableCode: 'T2',
          tableNameL10n: { values: { en: 'Water intake' } },
        },
      ],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByRole('columnheader', { name: '⟦import.table⟧' })).toBeTruthy();
    expect(within(table).getByRole('cell', { name: 'Water intake' })).toBeTruthy();
  });

  it('без назви таблиці показує її код', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [{ rowKey: 'R1', columnCode: 'C1', oldValue: null, newValue: '6', tableCode: 'T2' }],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    expect(within(screen.getByRole('table')).getByRole('cell', { name: 'T2' })).toBeTruthy();
  });

  it('V-10: причина відмови — з каталогу за messageKey, а не серверне речення', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'RTOT',
          columnCode: 'CRO',
          reasonCode: 'ECR-CELL-4221',
          message: 'Комірка обчислюється системою: значення з файлу не застосовується.',
          messageKey: 'err.ECR-CELL-4221.importCalculated',
          tableCode: 'FT1',
        },
        {
          rowKey: 'RTOT',
          columnCode: 'CDEC',
          reasonCode: 'ECR-ACCS-0403',
          message: 'Правило доступу: лише читання.',
          messageKey: 'deny.RowReadOnly',
          tableCode: 'FT1',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByText('⟦err.ECR-CELL-4221.importCalculated⟧ (ECR-CELL-4221)')).toBeTruthy();
    expect(within(table).getByText('⟦deny.RowReadOnly⟧ (ECR-ACCS-0403)')).toBeTruthy();
    expect(within(table).queryByText(/Правило доступу|обчислюється системою/)).toBeNull();
  });

  it('V-10: значення поза рядками таблиці показано адресою комірки книги', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: '—',
          columnCode: 'C2',
          reasonCode: 'ECR-ROW-0404',
          message: 'Значення B3 стоїть поза рядками таблиці: імпорт рядків не створює.',
          messageKey: 'err.ECR-ROW-0404.importOutsideRows',
          tableCode: 'T1',
          excelCell: 'B3',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByRole('cell', { name: 'B3' })).toBeTruthy();
    expect(within(table).getByText('⟦err.ECR-ROW-0404.importOutsideRows⟧ (ECR-ROW-0404)')).toBeTruthy();

    // Відмова блокує застосування — і діалог НЕ каже «файл збігається з аркушем».
    expect(screen.queryByText('⟦import.noChanges⟧')).toBeNull();
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(true);
  });
});

describe('ImportPanel: індикація за тривалістю розбору книги (ФВ-14.26)', () => {
  /**
   * Прев'ю, відповідь якого тест віддає тоді, коли сам вирішить.
   *
   * ⚠ `fireEvent`, а не `userEvent`: під фейковими таймерами `userEvent` чекає
   * власних затримок і завис би.
   */
  function slowPreview(): { finish: () => void; pick: () => void } {
    let finish = (): void => undefined;

    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            finish = () =>
              resolve(
                new Response(
                  JSON.stringify({ previewToken: 'tok', changes: [], rejected: [], conflicts: [] } satisfies ImportPreview),
                  { status: 200, headers: { 'Content-Type': 'application/json' } },
                ),
              );
          }),
      ),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { container } = render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <ImportPanel documentId={1} periodKey={202609} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const input = container.querySelector('input[type="file"]');
    if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

    return {
      finish: () => finish(),
      pick: () => fireEvent.change(input, { target: { files: [new File(['x'], 'book.xlsx')] } }),
    };
  }

  const pickButton = (): HTMLElement => screen.getByRole('button', { name: '⟦import.pick⟧' });

  async function advance(ms: number): Promise<void> {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('ФВ-14.26: повільний розбір — 99 мс нічого, 150 мс стан кнопки, 1.5 с текст', async () => {
    const preview = slowPreview();
    preview.pick();

    await advance(99);
    expect(pickButton().hasAttribute('data-loading')).toBe(false);
    expect(screen.queryByTestId('duration-progress')).toBeNull();

    await advance(51);
    expect(pickButton().hasAttribute('data-loading')).toBe(true);
    expect(screen.queryByTestId('duration-progress')).toBeNull();

    await advance(1350);
    expect(screen.getByTestId('duration-progress').textContent).toBe('⟦common.loading⟧');

    preview.finish();
    await advance(10);

    // Відповідь прийшла — індикація згасла, перегляд відкрито.
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.queryByTestId('duration-progress')).toBeNull();
    expect(pickButton().hasAttribute('data-loading')).toBe(false);
  });

  it('ФВ-14.26: швидкий розбір (50 мс) не блимає спінером на кнопці', async () => {
    const preview = slowPreview();
    preview.pick();

    await advance(50);
    // ⛔ Мутація: `loading={load.isPending}` (як було доти) — тут `true`.
    expect(pickButton().hasAttribute('data-loading')).toBe(false);

    preview.finish();
    await advance(2000);

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(pickButton().hasAttribute('data-loading')).toBe(false);
    expect(screen.queryByTestId('duration-progress')).toBeNull();
  });
});

describe('ImportPanel: четвертий раунд (F-01, F-06)', () => {
  it('F-06: відмова типу показана текстом каталогу, Apply вимкнено', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'R1',
          columnCode: 'A',
          reasonCode: 'ECR-CELL-0422',
          message: 'The value does not match the column type (err.ECR-CELL-0422.expectsNumber).',
          messageKey: 'err.ECR-CELL-0422.importExpectsNumber',
          tableCode: 'T1',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByText('⟦err.ECR-CELL-0422.importExpectsNumber⟧ (ECR-CELL-0422)')).toBeTruthy();
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(true);
  });

  it('F-01: 202 з jobId — «у фоні, дивись My tasks», а не «застосовано»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/import/preview')) {
          return new Response(
            JSON.stringify({
              previewToken: 'tok',
              changes: [{ rowKey: 'R1', columnCode: 'A', oldValue: null, newValue: 1, tableCode: 'T1' }],
              rejected: [],
              conflicts: [],
            } satisfies ImportPreview),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        if (url.includes('/import/apply')) {
          return new Response(JSON.stringify({ jobId: 'IExcelImportJob-1' }), {
            status: 202,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    await openPreview();
    await userEvent.click(screen.getByRole('button', { name: '⟦import.apply⟧' }));

    // ⛔ Мутація: прибрати гілку `isQueued` — тут буде `⟦import.applied⟧`.
    await vi.waitFor(() => expect(vi.mocked(showDone)).toHaveBeenCalledWith('⟦import.queued⟧'));
    expect(vi.mocked(showDone)).not.toHaveBeenCalledWith('⟦import.applied⟧');
  });
});

describe('ImportPanel: застосування впирається в блокування аркуша (ECR-DOC-4091)', () => {
  /*
   * ⚠ Застосування імпорту бере блокування аркуша (`SheetEditGate`); поки
   * аркуш подають (або зберігають/перераховують), сервер після очікування
   * відповідає `409 ECR-DOC-4091` з `messageKey` і `detail`, УЖЕ зібраним із
   * каталогу мовою користувача (`ExceptionHandlingMiddleware`,
   * `SheetEditGateTimeoutTests`). Людина має побачити саме цей текст —
   * «аркуш зараз подають, спробуйте за мить», — а не голе «Conflict».
   *
   * ⛔ Мутація: `onError: showApiError` застосування замінити загальною
   * помилкою (`() => showApiError(new Error('x'))`) — тут буде
   * `⟦state.errorTitle⟧` замість тексту каталогу.
   */
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it.each([
    [
      'err.ECR-DOC-4091.sheetBeingSubmitted',
      'This sheet is being submitted right now. Your changes were not saved; try again in a moment.',
    ],
    [
      'err.ECR-DOC-4091.sheetBeingEdited',
      'This sheet is being saved or recalculated right now. The sheet was not submitted; try again in a moment.',
    ],
  ])('409 з %s — у тості текст каталогу, не загальна помилка', async (messageKey, catalogText) => {
    const show = vi.spyOn(notifications, 'show');

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/import/preview')) {
          return new Response(
            JSON.stringify({
              previewToken: 'tok',
              changes: [{ rowKey: 'R1', columnCode: 'A', oldValue: null, newValue: 1, tableCode: 'T1' }],
              rejected: [],
              conflicts: [],
            } satisfies ImportPreview),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        if (url.includes('/import/apply')) {
          return new Response(
            JSON.stringify({
              type: 'https://ecr/errors/ECR-DOC-4091',
              title: 'Conflict',
              status: 409,
              detail: catalogText,
              errorCode: 'ECR-DOC-4091',
              correlationId: 'corr-1',
              messageKey,
            }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    await openPreview();
    await userEvent.click(screen.getByRole('button', { name: '⟦import.apply⟧' }));

    await vi.waitFor(() =>
      expect(show).toHaveBeenCalledWith(expect.objectContaining({ color: 'statusError', message: catalogText })),
    );
    expect(show).not.toHaveBeenCalledWith(expect.objectContaining({ message: '⟦state.errorTitle⟧' }));
    expect(vi.mocked(showDone)).not.toHaveBeenCalledWith('⟦import.applied⟧');

    // Перегляд лишається відкритим — людина може повторити застосування.
    expect(screen.getByRole('dialog')).toBeTruthy();
  });
});
