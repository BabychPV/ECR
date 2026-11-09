import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { projectDocumentReopensKey } from '../api';
import { DocumentPeriodReopenDialog } from '../DocumentPeriodReopenDialog';

/**
 * ✎ debt-1109 i4-P3: «Reopen for this document» зі сторінки документа не скидав кеш вкладки Periods — повернувшись
 * туди, людина бачила місяць просто «Locked» без «1 document reopened until …» до перезавантаження.
 * ⛔ Червоне без фіксу: календар і перелік винятків (`['periods', id, 'document-reopens']`) лишались валідними.
 */
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPeriodReopenDialog: після відкриття — перечитати Periods', () => {
  it('інвалідує календар і перелік винятків проєкту', async () => {
    const until = new Date(Date.now() + 86_400_000).toISOString();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ id: 1, documentId: 2, periodKey: 202601, openUntil: until }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['periods', 7], { periods: [] });
    client.setQueryData(projectDocumentReopensKey(7), { items: [], canClose: true });
    const onClose = vi.fn();

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <DocumentPeriodReopenDialog documentId={2} periodKey={202601} defaultOpenUntil={until} onClose={onClose} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.change(await screen.findByTestId('document-period-reopen-reason'), { target: { value: 'late invoice' } });
    fireEvent.click(screen.getByTestId('document-period-reopen-confirm'));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(client.getQueryState(['periods', 7])?.isInvalidated).toBe(true);
    expect(client.getQueryState(projectDocumentReopensKey(7))?.isInvalidated).toBe(true);
  });
});
