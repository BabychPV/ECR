import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WorkflowHistory } from '@/features/workflow/WorkflowHistory';
import type { WorkflowEvent } from '@/features/workflow/api';
import { testTheme } from '@/test/render';

/** Споживач `GET /api/v1/documents/{id}/workflow/history` (`BE-11b`). */
function respond(events: WorkflowEvent[]): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString();

    if (!url.includes('/api/v1/documents/7/workflow/history?periodKey=202601')) {
      throw new Error(`Немає мока для ${url}`);
    }

    return new Response(JSON.stringify(events), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <WorkflowHistory documentId={7} periodKey={202601} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const Rejected: WorkflowEvent = {
  action: 'Reject',
  at: '2026-04-01T06:00:00Z',
  byDisplayName: 'Olena Koval',
  fromState: 'Submitted',
  reason: 'total mismatch',
  sheetCode: 'S1',
  stepOrdinal: null,
  toState: 'Rejected',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('WorkflowHistory', () => {
  it('згорнутий блок не робить запиту; розгорнутий показує подію', async () => {
    const fetchMock = respond([Rejected]);
    show();

    const toggle = screen.getByRole('button', { name: '⟦workflow.history⟧' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(fetchMock).not.toHaveBeenCalled();

    fireEvent.click(toggle);

    const row = await screen.findByTestId('workflow-history-event');
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Стани — бейджами, момент — `<time>` із точним значенням, причина — текстом.
    expect([...row.querySelectorAll('[data-status-state]')].map((b) => b.getAttribute('data-status-state')))
      .toEqual(['Submitted', 'Rejected']);
    expect(row.querySelector('time')?.getAttribute('dateTime')).toBe(Rejected.at);
    expect(row.textContent).toContain('Olena Koval');
    expect(row.textContent).toContain('total mismatch');
  });

  it('порожня історія прибирає блок зовсім, разом із кнопкою', async () => {
    respond([]);
    show();

    fireEvent.click(screen.getByRole('button', { name: '⟦workflow.history⟧' }));

    await waitFor(() => expect(screen.queryByTestId('workflow-history')).toBeNull());
    expect(screen.queryByRole('button', { name: '⟦workflow.history⟧' })).toBeNull();
  });
});
