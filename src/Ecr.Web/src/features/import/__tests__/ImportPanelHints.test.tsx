import { describe, it, expect, vi, afterEach, type MockInstance } from 'vitest';
import { render, screen, within } from '@testing-library/react';
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
 * Підказки імпорту (P3): адреса комірки книги в кожній відмові, дві причини
 * `ECR-CELL-4221`, підказка «Recalculate» після застосування.
 *
 * ⚠ Сервер підмінено на рівні `fetch`: предмет — що діалог робить із готовою
 * відповіддю. Як сервер відрізняє застарілу книгу — `ImportDiffBuilderStaleCalculatedTests`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(preview: ImportPreview, results?: () => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);

    if (url.includes('/import/preview')) return json(preview);
    if (url.includes('/import/apply')) {
      return json({ appliedCells: 1, rowVersions: {}, validation: [], recalculationJobId: null });
    }
    if (url.includes('/calculation-results') && results !== undefined) return results();

    throw new Error(`неочікуваний запит у тесті: ${url}`);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

async function openPreview(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={7} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));
  await screen.findByRole('dialog');
}

/** Шпигун тостів Mantine. */
type ShowSpy = MockInstance<typeof notifications.show>;

const oneChange: ImportPreview = {
  previewToken: 'tok',
  changes: [{ rowKey: 'R1', columnCode: 'IN', oldValue: 1, newValue: 2, tableCode: 'T1' }],
  rejected: [],
  conflicts: [],
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.mocked(showDone).mockClear();
});

describe('ImportPanel: відмови перегляду (P3)', () => {
  it('рядок відмови несе і ключ рядка, і адресу комірки книги окремою колонкою', async () => {
    mockServer({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'R17',
          columnCode: 'EMISSION',
          reasonCode: 'ECR-CELL-4221',
          message: 'diag',
          messageKey: 'err.ECR-CELL-4221.importCalculated',
          tableCode: 'T1',
          excelCell: 'F23',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByRole('columnheader', { name: '⟦import.excelCell⟧' })).toBeTruthy();
    // ⛔ Мутація: повернути `excelCell ?? rowKey` у колонку рядка — `R17` зникне.
    expect(within(table).getByRole('cell', { name: 'R17' })).toBeTruthy();
    expect(within(table).getByRole('cell', { name: 'F23' })).toBeTruthy();
  });

  it('ECR-CELL-4221: застаріла книга і змінена комірка — різні тексти каталогу', async () => {
    mockServer({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'R1',
          columnCode: 'EMISSION',
          reasonCode: 'ECR-CELL-4221',
          message: 'diag',
          messageKey: 'err.ECR-CELL-4221.importCalculatedStale',
          tableCode: 'T1',
          excelCell: 'C3',
        },
        {
          rowKey: 'R2',
          columnCode: 'EMISSION',
          reasonCode: 'ECR-CELL-4221',
          message: 'diag',
          messageKey: 'err.ECR-CELL-4221.importCalculated',
          tableCode: 'T1',
          excelCell: 'C4',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    // ⛔ Мутація: прибрати гілку `importCalculatedStale` у `rejectionText` — тут
    // буде загальне `⟦import.rejectedCell⟧`.
    expect(within(table).getByText('⟦err.ECR-CELL-4221.importCalculatedStale⟧ (ECR-CELL-4221)')).toBeTruthy();
    expect(within(table).getByText('⟦err.ECR-CELL-4221.importCalculated⟧ (ECR-CELL-4221)')).toBeTruthy();
  });

  it('відмова цілої таблиці без адреси — прочерк, а не порожня комірка', async () => {
    mockServer({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: '—',
          columnCode: 'T9',
          reasonCode: 'ECR-IMP-0422',
          message: 'diag',
          messageKey: 'err.ECR-IMP-0422.importTableMissing',
          tableCode: 'T9',
          excelCell: null,
        },
      ],
      conflicts: [],
    });

    await openPreview();

    expect(within(screen.getByRole('table')).getAllByRole('cell', { name: '—' }).length).toBe(2);
  });
});

describe('ImportPanel: підказка «Recalculate» після застосування (P3)', () => {
  async function applyWith(results?: () => Response): Promise<ShowSpy> {
    const show = vi.spyOn(notifications, 'show');
    mockServer(oneChange, results);

    await openPreview();
    await userEvent.click(screen.getByRole('button', { name: '⟦import.apply⟧' }));
    await vi.waitFor(() => expect(vi.mocked(showDone)).toHaveBeenCalledWith('⟦import.applied⟧'));

    return show;
  }

  function recalcHintShown(show: ShowSpy): boolean {
    return show.mock.calls.some(([data]) => data.title === '⟦import.recalculateTitle⟧');
  }

  it('числа методологій застаріли — підказка лишається на екрані, доки її не закриють', async () => {
    const show = await applyWith(() =>
      json([{ outputCode: 'CO2', value: 1, unitId: 1, sourceRowKey: 'R1', isStale: true }]),
    );

    // ⛔ Мутація: прибрати виклик `calculationResultsStale` в `onSuccess` — підказки не буде.
    await vi.waitFor(() => expect(recalcHintShown(show)).toBe(true));
    const call = show.mock.calls.find(([data]) => data.title === '⟦import.recalculateTitle⟧');
    expect(call?.[0].autoClose).toBe(false);
  });

  it('числа свіжі — підказки немає', async () => {
    const show = await applyWith(() =>
      json([{ outputCode: 'CO2', value: 1, unitId: 1, sourceRowKey: 'R1', isStale: false }]),
    );

    // Дати запиту чисел відбутися: перевірка «нема» без цього була б порожньою.
    await new Promise((resolve) => setTimeout(resolve, 20));
    // ⛔ Мутація: `results.some(... isStale)` → `true` — підказка з'явиться й тут.
    expect(recalcHintShown(show)).toBe(false);
  });

  it('читання чисел відмовлено (403) — імпорт успішний, підказки й помилки немає', async () => {
    const show = await applyWith(() =>
      json({ title: 'Forbidden', status: 403, code: 'ECR-AUTH-0403' }, 403),
    );

    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(recalcHintShown(show)).toBe(false);
    expect(show.mock.calls.some(([data]) => data.color === 'statusError')).toBe(false);
  });
});
