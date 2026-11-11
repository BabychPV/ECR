import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { showDone } from '@/shared/ui/notify';
import { DocumentPeriodReopenDialog } from '../DocumentPeriodReopenDialog';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * ✎ AN-178 (клас AN-169): тост «місяць відкрито до …» показував строк `formatDateTime` у поясі БРАУЗЕРА, а банер
 * документа і смуга місяців — у поясі майданчика з правилом кінця доби. Північ майданчика (19:00Z в Атирау, +05:00)
 * у тості читалася «Nov 11, 7:00 PM» (браузер у UTC), у банері — «Nov 11, 11:59 PM».
 * ⛔ Червоне без фіксу: `formatDateTime(view.openUntil)` → «Nov 11, 2099, 7:00 PM».
 */
afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockReset();
});

describe('DocumentPeriodReopenDialog: строк у тості — у поясі майданчика', () => {
  it('північ майданчика — кінець попередньої доби в поясі майданчика, не час браузера', async () => {
    const until = '2099-11-11T19:00:00Z';
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
    const onClose = vi.fn();

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <DocumentPeriodReopenDialog documentId={2} periodKey={202601} defaultOpenUntil={until} timeZoneId="Asia/Atyrau" onClose={onClose} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.change(await screen.findByTestId('document-period-reopen-reason'), { target: { value: 'late invoice' } });
    fireEvent.click(screen.getByTestId('document-period-reopen-confirm'));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const toast = String(vi.mocked(showDone).mock.calls[0]?.[0] ?? '').replace(/[  ]/gu, ' ');
    expect(toast).toContain('document.reopen.done');
    expect(toast).toContain('until=Nov 11, 2099, 11:59 PM');
  });
});
