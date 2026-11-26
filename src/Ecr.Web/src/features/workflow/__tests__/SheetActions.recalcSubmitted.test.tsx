import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { SheetActions } from '../SheetActions';
import { testTheme } from '@/test/render';

/**
 * âœŽ DOCUX L1 (BUG-1, Ð¿Ñ€Ð°Ð²Ð¸Ð»Ð¾ Ð»ÑŽÐ´Ð¸Ð½Ð¸ 07.11/16.11): ÑÐµÑ€Ð²ÐµÑ€ (`RecalculateDocumentHandler`) ÐŸÐ•Ð Ð•Ð ÐÐ¥ÐžÐ’Ð£Ð„ Ð¿Ð¾Ð´Ð°Ð½Ð¸Ð¹ Ð°Ñ€ÐºÑƒÑˆ Ñ–
 * Ð¿Ñ€Ð¾Ð¿ÑƒÑÐºÐ°Ñ” Ð»Ð¸ÑˆÐµ Ð—ÐÐ¢Ð’Ð•Ð Ð”Ð–Ð•ÐÐ˜Ð™. Ð¢Ð¾Ð¶ Â«RecalculateÂ» Ð²Ð¸Ð¼ÐºÐ½ÐµÐ½Ð° Ð· Ð¿Ñ€Ð¸Ñ‡Ð¸Ð½Ð¾ÑŽ Ñ‚Ñ–Ð»ÑŒÐºÐ¸ Ð½Ð°Ð´ Ð·Ð°Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð¸Ð¼ Ð°Ñ€ÐºÑƒÑˆÐµÐ¼ (Ð°Ð±Ð¾ ÐºÐ¾Ð»Ð¸
 * Ð·Ð°Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð¾ Ð²ÑÐµ); Ð¿Ð¾Ð´Ð°Ð½Ð¸Ð¹ Ð°Ñ€ÐºÑƒÑˆ â€” ÐºÐ½Ð¾Ð¿ÐºÐ° Ð°ÐºÑ‚Ð¸Ð²Ð½Ð°, Ð·Ð°Ð¿Ð¸Ñ‚ Ñ–Ð´Ðµ. Ð Ñ–Ñ‡Ð½Ð° Ñ€Ð¾Ð·ÐºÐ»Ð°Ð´ÐºÐ° Ð½Ðµ Ð´Ð¸Ð²Ð¸Ñ‚ÑŒÑÑ Ð½Ð° ÑÑ‚Ð°Ð½ ÑÐºÐ¾Ñ€Ñ.
 * Ð‘ÐµÐ· Ñ„Ñ–ÐºÑÑƒ Ñ‡ÐµÑ€Ð²Ð¾Ð½Ñ–: Ð¿Ð¾Ð´Ð°Ð½Ð¸Ð¹ Ð°Ñ€ÐºÑƒÑˆ Ð´Ð°Ð²Ð°Ð² `aria-disabled` Ñ– Â«Return it for edits firstÂ».
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

describe('SheetActions: Recalculate Ñ– ÑÑ‚Ð°Ð½ Ð°Ñ€ÐºÑƒÑˆÐ° (DOCUX L1, BUG-1)', () => {
  it('ÑƒÑÑ– Ð°Ñ€ÐºÑƒÑˆÑ– Ñ‡ÐµÑ€Ð½ÐµÑ‚ÐºÐ¸ - ÐºÐ½Ð¾Ð¿ÐºÐ° Ñ” (ÐºÐ¾Ð½Ñ‚Ñ€Ð¾Ð»ÑŒ)', async () => {
    render1({ S1: 'Draft', S2: 'Draft' });

    expect(await screen.findByRole('button', { name: /recalculate/i })).toBeTruthy();
  });

  it('L1-1: Ñ†ÐµÐ¹ Ð°Ñ€ÐºÑƒÑˆ Submitted - ÐºÐ½Ð¾Ð¿ÐºÐ° Ð°ÐºÑ‚Ð¸Ð²Ð½Ð°, ÐºÐ»Ñ–Ðº ÑˆÐ»Ðµ Ð·Ð°Ð¿Ð¸Ñ‚ (ÑÐµÑ€Ð²ÐµÑ€ Ð¿Ð¾Ð´Ð°Ð½Ñ– Ð¿ÐµÑ€ÐµÑ€Ð°Ñ…Ð¾Ð²ÑƒÑ”)', async () => {
    render1({ S1: 'Draft', S2: 'Submitted' }, 'Submitted');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');

    await userEvent.setup().click(button);
    await waitFor(() => expect(recalcPosts()).toBe(1));
  });

  it('ÑƒÑÑ– Ð°Ñ€ÐºÑƒÑˆÑ– Submitted - ÐºÐ½Ð¾Ð¿ÐºÐ° Ð°ÐºÑ‚Ð¸Ð²Ð½Ð° (Ð¿Ð¾Ð´Ð°Ð½Ñ– Ð½Ðµ Ð±Ð»Ð¾ÐºÑƒÑŽÑ‚ÑŒ)', async () => {
    render1({ S1: 'Submitted', S2: 'Submitted' }, 'Submitted');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('Ñ†ÐµÐ¹ Ð°Ñ€ÐºÑƒÑˆ Approved - ÐºÐ½Ð¾Ð¿ÐºÐ° aria-disabled Ð· Ð¿Ñ€Ð¸Ñ‡Ð¸Ð½Ð¾ÑŽ Ð¿Ñ€Ð¾ Ð—ÐÐ¢Ð’Ð•Ð Ð”Ð–Ð•ÐÐ˜Ð™, ÐºÐ»Ñ–Ðº Ñ– F9 Ð½Ðµ ÑˆÐ»ÑŽÑ‚ÑŒ Ð·Ð°Ð¿Ð¸Ñ‚', async () => {
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

  it('ÑƒÑÑ– Ð°Ñ€ÐºÑƒÑˆÑ– Approved (ÑÑ‚Ð°Ð½ Ñ†ÑŒÐ¾Ð³Ð¾ Ð½ÐµÐ²Ñ–Ð´Ð¾Ð¼Ð¸Ð¹) - ÐºÐ½Ð¾Ð¿ÐºÐ° aria-disabled', async () => {
    render1({ S1: 'Approved', S2: 'Approved' }, '');

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await waitFor(() => expect(button.getAttribute('aria-disabled')).toBe('true'));
  });

  it('L1-2: Ñ€Ñ–Ñ‡Ð½Ð° Ñ€Ð¾Ð·ÐºÐ»Ð°Ð´ÐºÐ°, ÑÐºÑ–Ñ€ Approved - ÐºÐ½Ð¾Ð¿ÐºÐ° Ñ– F9 Ð°ÐºÑ‚Ð¸Ð²Ð½Ñ– (Ñ€Ñ–Ðº Ñ€Ð°Ñ…ÑƒÑ” Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ñ– Ð¼Ñ–ÑÑÑ†Ñ–)', async () => {
    render1({ S1: 'Approved' }, 'Approved', true);

    const button = await screen.findByRole('button', { name: /recalculate/i });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(button.getAttribute('aria-disabled')).not.toBe('true');

    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9' });
    await waitFor(() => expect(recalcPosts()).toBe(1));
  });
});
