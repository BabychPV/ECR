import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * ✎ UAT-p3 №1 (людський прохід W15k): «Submit: January 2026» при відкритому жовтні подавав січень ОДНИМ кліком,
 * тоді як Approve і Return for edits питають. Тепер клік відкриває підтвердження з назвою місяця; POST іде лише
 * після «Submit» у діалозі, «Cancel»/Esc нічого не подають і повертають фокус на кнопку.
 */

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Submit' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: 1,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

let submitBodies: Record<string, unknown>[] = [];

function mockFetch(): void {
  submitBodies = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/api/v1/documents/1/submit') && init?.method === 'POST') {
        submitBodies.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        return new Response(null, { status: 204 });
      }
      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], Summary);
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('Submit asks for confirmation naming the month', () => {
  it('клік відкриває діалог із місяцем і НІЧОГО не подає; «Submit» у діалозі подає той місяць', async () => {
    mockFetch();
    show();

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.submitPeriod/ }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toMatch(/workflow\.submitConfirmTitle \(period=[^)]*2026/);
    expect(dialog.textContent).toMatch(/workflow\.submitConfirmHint \(period=[^)]*2026/);
    expect(submitBodies).toHaveLength(0);

    fireEvent.click(await screen.findByRole('button', { name: '⟦document.submit⟧' }));

    await waitFor(() => expect(submitBodies).toHaveLength(1));
    expect(submitBodies[0]).toMatchObject({ sheetDefId: 42, periodKey: 202601, acknowledgeWarnings: false });
  });

  it('«Cancel» закриває діалог, нічого не подає і повертає фокус на кнопку подання', async () => {
    mockFetch();
    show();

    const trigger = await screen.findByRole('button', { name: /workflow\.submitPeriod/ });
    trigger.focus();
    fireEvent.click(trigger);
    await screen.findByRole('dialog');

    fireEvent.click(await screen.findByRole('button', { name: '⟦common.cancel⟧' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
    expect(submitBodies).toHaveLength(0);
  });
});
