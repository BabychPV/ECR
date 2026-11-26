import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { SheetActions } from '../SheetActions';
import { testTheme } from '@/test/render';

/**
 * ✎ DOCUX L1 (BUG-1, правило людини 07.11/16.11): сервер (`RecalculateDocumentHandler`) ПЕРЕРАХОВУЄ поданий аркуш і
 * пропускає лише ЗАТВЕРДЖЕНИЙ. Тож «Recalculate» вимкнена з причиною тільки над затвердженим аркушем (або коли
 * затверджено все); поданий аркуш — кнопка активна, запит іде. Річна розкладка не дивиться на стан якоря.
 * Без фіксу червоні: поданий аркуш давав `aria-disabled` і «Return it for edits first».
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
    stepApproverNamesTruncated: false,
  };
}

function render1(sheetStates: Record<string, string>, state = 'Draft', yearLayout = false): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: ['Document.View'], simulatedForUserId: null, userId: 9, userName: 'tester' }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (String(input).includes('/recalculate')) {
        return new Response(JSON.stringify({ jobId: null, skipped: [], skippedTruncated: false }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      throw new Error('unexpected request');
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202401], summary(sheetStates));

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={2} periodKey={202401} state={state} yearLayout={yearLayout} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const recalcPosts = (): number =>
  vi.mocked(fetch).mock.calls.filter((c) => String(c[0]).includes('/recalculate')).length;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: Recalculate і стан аркуша (DOCUX L1, BUG-1)', () => {
  it('усі аркуші чернетки - кнопка є (контроль)', async () => {
    render1({ S1: 'Draft', S2: 'Draft' });

    expect(await screen.findByRole('button', { name: /recalculate/i })).toBeTruthy();
  });

  it('L1-1: цей аркуш Submitted - кнопка активна, клік шле запит (сервер подані перераховує)', async () => {
    render1({ S1: 'Draft', S2: 'Submitted' }, 'Submitted');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');

    await userEvent.setup().click(button);
    await waitFor(() => expect(recalcPosts()).toBe(1));
  });

  it('усі аркуші Submitted - кнопка активна (подані не блокують)', async () => {
    render1({ S1: 'Submitted', S2: 'Submitted' }, 'Submitted');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('цей аркуш Approved - кнопка aria-disabled з причиною про ЗАТВЕРДЖЕНИЙ, клік і F9 не шлють запит', async () => {
    render1({ S1: 'Draft', S2: 'Approved' }, 'Approved');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await waitFor(() => expect(button.getAttribute('aria-disabled')).toBe('true'));
    const describedBy = button.getAttribute('aria-describedby') ?? '';
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy.split(' ')[0]!)?.textContent ?? '').toContain('workflow.recalculateSheetApproved');

    const user = userEvent.setup();
    await user.click(button);
    await user.keyboard('{F9}');
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(recalcPosts()).toBe(0);
  });

  it('усі аркуші Approved (стан цього невідомий) - кнопка aria-disabled', async () => {
    render1({ S1: 'Approved', S2: 'Approved' }, '');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await waitFor(() => expect(button.getAttribute('aria-disabled')).toBe('true'));
  });

  it('L1-2: річна розкладка, якір Approved - кнопка і F9 активні (рік рахує відкриті місяці)', async () => {
    render1({ S1: 'Approved' }, 'Approved', true);

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');

    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9' });
    await waitFor(() => expect(recalcPosts()).toBe(1));
  });
});
