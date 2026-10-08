import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { SheetActions } from '../SheetActions';
import { testTheme } from '@/test/render';

/**
 * AN-39 / L8-12 + RC14-B: коли в періоді є поданий аркуш, сервер (`RecalculateDocumentHandler`:
 * хоч один аркуш Submitted/Approved) відмовляє, тож «Recalculate» вимкнена з ПРИЧИНОЮ, а не зникає.
 */
function summary(sheetStates: Record<string, string>): DocumentSummary {
  return {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: Object.keys(sheetStates).length,
    sheetStates,
    hasLateEdits: false,
  };
}

function render1(sheetStates: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: ['Document.View'], simulatedForUserId: null, userId: 9, userName: 'tester' }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error('unexpected request');
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202401], summary(sheetStates));

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={2} periodKey={202401} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: Recalculate і поданий аркуш (L8-12)', () => {
  it('усі аркуші чернетки - кнопка є (контроль)', async () => {
    render1({ S1: 'Draft', S2: 'Draft' });

    expect(await screen.findByRole('button', { name: /recalculate/i })).toBeTruthy();
  });

  it.each(['Submitted', 'Approved'])(
    'інший аркуш %s - кнопка лишається, але aria-disabled, причина в описі, клік не шле запит',
    async (state) => {
      render1({ S1: 'Draft', S2: state });

      const button = await screen.findByRole('button', { name: /recalculate/i });
      await waitFor(() => expect(button.getAttribute('aria-disabled')).toBe('true'));
      expect(button.getAttribute('aria-describedby')).toBeTruthy();

      const calls = vi.mocked(fetch).mock.calls.length;
      await userEvent.setup().click(button);
      await userEvent.setup().keyboard('{F9}');
      await new Promise((resolve) => setTimeout(resolve, 100));

      expect(vi.mocked(fetch).mock.calls.filter((c) => String(c[0]).includes('/recalculate'))).toHaveLength(0);
      expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
    },
  );
});
