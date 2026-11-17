import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { DocumentLogDialog } from '../DocumentLogDialog';

/**
 * ✎ LAND-YEAR L7b (п. 8 плану): «More → History» документа «рік» передає `year` до журналу переходів — запит з
 * `year=true` і групи за місяцем; без `year` — один період, як доти.
 *
 * ⛔ Мутаційний доказ (READY): `year={year}` у `DocumentLogDialog` прибрано — червоний перший тест.
 */
const urls: string[] = [];

function mount(year: boolean | undefined): void {
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return new Response(
        JSON.stringify([
          {
            action: 'Submit', approvedBySubmitter: false, at: '2099-10-02T10:00:00Z', byDisplayName: 'f13-operator',
            fromState: 'Draft', periodKey: 209910, reason: null, sheetCode: 'LAND', sheetCopyId: null, stepOrdinal: null,
            toState: 'Submitted',
          },
        ]),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentLogDialog kind="history" documentId={1} periodKey={209901} onClose={() => undefined} {...(year === undefined ? {} : { year })} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('DocumentLogDialog: документ «рік»', () => {
  it('рік: журнал переходів за всі місяці, групою місяця', async () => {
    mount(true);

    await screen.findByText('LAND');
    expect(urls).toContain('/api/v1/documents/1/workflow/history?periodKey=209901&year=true');
    expect(document.querySelector('[data-workflow-history-month="209910"]')).not.toBeNull();
  });

  it('копія на місяць: один період, без груп', async () => {
    mount(undefined);

    await screen.findByText('LAND');
    expect(urls).toContain('/api/v1/documents/1/workflow/history?periodKey=209901');
    expect(document.querySelector('[data-workflow-history-month]')).toBeNull();
  });
});
