import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';

/**
 * PM-UI-IMPORT-REJECT-BANNER: відмова за станом періоду колонки місяця відхиляє весь пакет (p53-monthcol) —
 * перегляд має це сказати банером, а не лише переліком комірок.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

async function openPreview(preview: ImportPreview): Promise<void> {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/import/preview')) return json(preview);
      throw new Error(`неочікуваний запит у тесті: ${String(input)}`);
    }),
  );

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

function rejection(messageKey: string, columnCode: string): ImportPreview['rejected'][number] {
  return { rowKey: 'R1', columnCode, reasonCode: 'ECR-ACCS-0403', message: 'diag', messageKey, tableCode: 'T1', excelCell: 'D5' };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('ImportPanel: банер «пакет відхилено» (PM-UI-IMPORT-REJECT-BANNER)', () => {
  it('відмова за станом періоду колонки місяця — банер із назвою причини й колонками', async () => {
    await openPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [rejection('deny.PeriodClosed', 'M03'), rejection('deny.DocumentSubmitted', 'M04'), rejection('deny.PeriodClosed', 'M03')],
      conflicts: [],
      overwritable: [],
    });

    const banner = screen.getByTestId('import-package-rejected');
    expect(banner.textContent).toContain('⟦import.preview.packageRejected⟧');
    expect(banner.getAttribute('role')).toBe('alert');
  });

  it('інша відмова (тип значення) — банера немає, лише загальне блокування', async () => {
    await openPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [rejection('err.ECR-CELL-0422.importExpectsNumber', 'M03')],
      conflicts: [],
      overwritable: [],
    });

    expect(screen.queryByTestId('import-package-rejected')).toBeNull();
    expect(screen.getByText('⟦import.blockedTitle⟧')).toBeTruthy();
  });

  it('без відмов — банера немає', async () => {
    await openPreview({
      previewToken: 'tok',
      changes: [{ rowKey: 'R1', columnCode: 'IN', oldValue: 1, newValue: 2, tableCode: 'T1' }],
      rejected: [],
      conflicts: [],
      overwritable: [],
    });

    expect(screen.queryByTestId('import-package-rejected')).toBeNull();
  });
});
