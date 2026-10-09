import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview, ImportRejection } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * AN-114 (D-338, HU-13 Q1 «+ прапорець перезаписати»): рядок книги, змінений
 * кимось після експорту, більше не блокує Apply всієї книги — людина або
 * свідомо перезаписує його своїми значеннями, або лишає чуже й застосовує решту.
 *
 * ⚠ Сервер підмінено на рівні `fetch`: предмет — що діалог вирішує й надсилає.
 * Що сервер робить із `overwriteRows` — `ExcelImportOverwriteTests`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Тіла запитів застосування, у порядку надсилання. */
function mockServer(preview: ImportPreview): unknown[] {
  const applied: unknown[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/import/preview')) return json(preview);
      if (url.includes('/import/apply')) {
        applied.push(JSON.parse(String(init?.body)));
        return json({ appliedCells: 1, rowVersions: {}, validation: [], recalculationJobId: null });
      }
      if (url.includes('/calculation-results')) return json([]);

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  return applied;
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

const conflict: ImportRejection = {
  rowKey: 'R1',
  columnCode: 'A',
  reasonCode: 'ECR-CELL-0409',
  message: 'diag',
  messageKey: 'err.ECR-CELL-0409.importRowChangedSinceExport',
  tableCode: 'T1',
  excelCell: 'B3',
};

const conflictPreview: ImportPreview = {
  previewToken: 'tok',
  changes: [],
  rejected: [conflict],
  conflicts: [],
  overwritable: [{ rowKey: 'R1', columnCode: 'A', oldValue: 7, newValue: 5, tableCode: 'T1' }],
};

function applyButton(): HTMLElement {
  return screen.getByRole('button', { name: '⟦import.apply⟧' });
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('ImportPanel: рядки, змінені після експорту (AN-114)', () => {
  it('показує чуже і моє значення; Apply доступний після позначки «перезаписати» і несе рядок', async () => {
    const applied = mockServer(conflictPreview);

    await openPreview();

    const cells = within(screen.getByRole('table', { name: 'T1 · R1' }));
    expect(cells.getByRole('cell', { name: '7' })).toBeTruthy();
    expect(cells.getByRole('cell', { name: '5' })).toBeTruthy();

    // ⛔ Мутація: повернути `blocked` з усіма `rejected` — Apply не ввімкнеться ніколи.
    expect(applyButton().hasAttribute('disabled')).toBe(true);

    await userEvent.click(screen.getByRole('checkbox', { name: /⟦import\.overwriteRow⟧ — T1 · R1/ }));
    expect(applyButton().hasAttribute('disabled')).toBe(false);

    await userEvent.click(applyButton());

    await waitFor(() => expect(applied).toHaveLength(1));
    expect(applied[0]).toEqual({ previewToken: 'tok', overwriteRows: [{ tableCode: 'T1', rowKey: 'R1' }] });
  });

  it('непозначений рядок лишає чуже лише явною згодою, і решта застосовується без перезапису', async () => {
    const applied = mockServer({
      ...conflictPreview,
      changes: [{ rowKey: 'R2', columnCode: 'A', oldValue: 1, newValue: 2, tableCode: 'T1' }],
    });

    await openPreview();

    // ⛔ Мовчання — не рішення: без згоди Apply вимкнений, хоч зміни є.
    expect(applyButton().hasAttribute('disabled')).toBe(true);

    await userEvent.click(screen.getByRole('checkbox', { name: '⟦import.overwriteSkip⟧' }));
    expect(applyButton().hasAttribute('disabled')).toBe(false);

    await userEvent.click(applyButton());

    await waitFor(() => expect(applied).toHaveLength(1));
    expect(applied[0]).toEqual({ previewToken: 'tok' });
  });

  it('інша відмова (права) блокує Apply і після позначки «перезаписати»', async () => {
    mockServer({
      ...conflictPreview,
      rejected: [
        conflict,
        {
          rowKey: 'R9',
          columnCode: 'A',
          reasonCode: 'ECR-ACCS-0403',
          message: 'diag',
          messageKey: 'deny.RowReadOnly',
          tableCode: 'T1',
          excelCell: 'B11',
        },
      ],
    });

    await openPreview();

    await userEvent.click(screen.getByRole('checkbox', { name: /⟦import\.overwriteRow⟧/ }));

    expect(screen.getByText('⟦import.blockedTitle⟧')).toBeTruthy();
    expect(applyButton().hasAttribute('disabled')).toBe(true);
  });
});
