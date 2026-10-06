import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * `UI-41`: F9 — та сама дія, що й кнопка «Recalculate» (макет `screen-document.js`: F9 →
 * `doRecalc`). Не з поля вводу/редактора/діалогу, не з модифікаторами, не там, де кнопки немає.
 */
const CurrentUser = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

let recalculateBodies: unknown[] = [];

function mockFetch(): void {
  recalculateBodies = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/recalculate')) {
        recalculateBodies.push(init?.body ? JSON.parse(String(init.body)) : null);

        return json({ jobId: 'job-1' }, 202);
      }
      if (url.includes('/api/v1/jobs/')) return json({ jobId: 'job-1', state: 'Running', percent: 10, message: null, error: null });

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(state = 'Draft'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <input aria-label="outside field" />
        <div role="dialog" aria-label="some dialog">
          <button type="button">inside dialog</button>
        </div>
        <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state={state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: F9 = Recalculate (UI-41)', () => {
  it('кнопка несе aria-keyshortcuts="F9"; F9 на сторінці шле той самий запит, що й кнопка', async () => {
    mockFetch();
    show();
    const button = await screen.findByRole('button', { name: /recalculate/i });
    expect(button.getAttribute('aria-keyshortcuts')).toBe('F9');

    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9' });

    await waitFor(() => expect(recalculateBodies).toHaveLength(1));
    expect(recalculateBodies[0]).toMatchObject({ periodKey: 202601, sheetDefId: 42 });
  });

  it('F9 у полі вводу, у діалозі чи з модифікатором — нічого', async () => {
    mockFetch();
    show();
    await screen.findByRole('button', { name: /recalculate/i });

    fireEvent.keyDown(screen.getByLabelText('outside field'), { key: 'F9', code: 'F9' });
    fireEvent.keyDown(screen.getByRole('button', { name: 'inside dialog' }), { key: 'F9', code: 'F9' });
    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9', ctrlKey: true });
    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9', shiftKey: true });

    // Даємо мікрозадачам відпрацювати: запит, якби пішов, уже був би зафіксований.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(recalculateBodies).toHaveLength(0);
  });

  it('кнопки немає (аркуш поданий) — F9 нічого не шле', async () => {
    mockFetch();
    show('Submitted');
    await waitFor(() => expect(screen.queryByRole('button', { name: /recalculate/i })).toBeNull());

    fireEvent.keyDown(document.body, { key: 'F9', code: 'F9' });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(recalculateBodies).toHaveLength(0);
  });
});
