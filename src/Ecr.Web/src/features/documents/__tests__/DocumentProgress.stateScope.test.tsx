import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentProps } from 'react';
import { DocumentProgress } from '../DocumentProgress';

/**
 * ⛔ AC-2 (b14 documents-board-yearly, людина 25.11: «Sheet: Draft» без місяця — «це не зрозумів чому»): поруч зі станом
 * аркуша — ЧИЙ це стан (місяць, «Reopened for this document» або рік), а документ «рік» показує зведений стан аркуша
 * замість стану якоря. Без фіксу червоний: підпису періоду немає, а чип — стан вкладки («Draft»).
 * Сторінковий доказ (звідки береться підпис і стан) — `pages/__tests__/DocumentPage.sheetStateScope.test.tsx`.
 */
function show(extra: Partial<ComponentProps<typeof DocumentProgress>>): HTMLElement {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentProgress
          documentId={1}
          periodKey={202601}
          sheets={[{ code: 'S1', name: 'Emissions', state: 'Draft' }]}
          activeCode="S1"
          issues={null}
          onShowIssues={() => undefined}
          {...extra}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.getByTestId('document-progress');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentProgress: чий стан у «Sheet:» (AC-2, b14)', () => {
  it('AC-2: місяць і «перевідкрито» поруч зі станом — не голе «Draft»', () => {
    const progress = show({ stateScope: 'September 2026 · Reopened for this document' });

    expect(within(progress).getByTestId('document-sheet-state-scope').textContent).toBe(
      '· September 2026 · Reopened for this document',
    );
  });

  it('AC-2: документ «рік» — зведений стан аркуша замість стану якоря', () => {
    const progress = show({ stateScope: 'Year 2026', stateOverride: 'PartlySubmitted' });

    expect(progress.querySelector('[data-status-kind="sheet"][data-status-state="PartlySubmitted"]')).not.toBeNull();
    expect(progress.querySelector('[data-status-kind="sheet"][data-status-state="Draft"]')).toBeNull();
    expect(within(progress).getByTestId('document-sheet-state-scope').textContent).toBe('· Year 2026');
  });

  it('контроль: без підпису — як було (лише стан вкладки)', () => {
    const progress = show({});

    expect(progress.querySelector('[data-status-kind="sheet"][data-status-state="Draft"]')).not.toBeNull();
    expect(within(progress).queryByTestId('document-sheet-state-scope')).toBeNull();
  });
});
