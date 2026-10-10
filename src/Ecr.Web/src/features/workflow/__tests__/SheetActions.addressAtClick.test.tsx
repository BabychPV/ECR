import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { SheetActions } from '../SheetActions';

/**
 * AN-77 / N3-01: адреса дії (`sheetDefId`, `periodKey`) фіксується в мить кліку.
 *
 * Дефект: `mutationFn` брав адресу з опцій ОСТАННЬОГО рендеру, а `settleAndFlushEdits` чекає збереження
 * набраного до 3 с. Перемкнули вкладку за цей час - подавався ІНШИЙ аркуш, і права на нього заново
 * не перевірялися.
 *
 * Мутація (локально): повернути в `submit.mutationFn` читання `sheetDefId`/`periodKey` з пропів - тест червоний.
 */

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Manage' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

const posts: { url: string; body: Record<string, unknown> }[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) as Record<string, unknown> });

        return url.includes('/recalculate') ? json({ jobId: 'job-1' }, 202) : json({});
      }

      return json({});
    }),
  );
}

/** Зупинка збереження: `release` з'являється, коли зберігач викликано. */
const gate: { release: (() => void) | null } = { release: null };
let unregister: (() => void) | null = null;

/** «Сітка» з незбереженою правкою, збереження якої висить, доки тест не відпустить. */
function holdingGrid(): void {
  unregister = registerUnsavedSource('an77-test', {
    hasUnsaved: () => true,
    flush: () =>
      new Promise<boolean>((resolve) => {
        gate.release = () => resolve(true);
      }),
  });
}

function tree(sheetDefId: number, periodKey: number, state = 'Draft'): JSX.Element {
  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={sheetDefId} periodKey={periodKey} state={state} />
      </QueryClientProvider>
    </MantineProvider>
  );
}

const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

function seed(): void {
  for (const period of [202601, 202602]) {
    client.setQueryData(['document', 1, period], {
      businessKey: 'DOC-1',
      createdAt: '2026-01-01T00:00:00Z',
      id: 1,
      nameL10n: null,
      projectId: 7,
      sheetCount: 2,
      sheetStates: {},
    });
  }
}

afterEach(() => {
  unregister?.();
  unregister = null;
  gate.release = null;
  posts.length = 0;
  client.clear();
  vi.unstubAllGlobals();
});

/** Клік уже запустив збереження (зберігач викликано), але воно ще не завершилось. */
async function saverIsHeld(): Promise<void> {
  await waitFor(() => expect(gate.release).not.toBeNull());
}

function releaseSaver(): void {
  gate.release?.();
}

describe('AN-77 N3-01: адреса дії знімається в мить кліку', () => {
  it('Submit: вкладку перемкнули, поки чекали збереження, - у POST старий sheetDefId', async () => {
    mockFetch();
    seed();
    holdingGrid();
    const view = render(tree(2, 202601));

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));
    await saverIsHeld();

    view.rerender(tree(3, 202601));
    releaseSaver();

    await waitFor(() => expect(posts.filter((p) => p.url.includes('/submit'))).toHaveLength(1));
    expect(posts.find((p) => p.url.includes('/submit'))?.body).toMatchObject({ sheetDefId: 2, periodKey: 202601 });
  });

  it('Submit: період змінили - у POST старий periodKey', async () => {
    mockFetch();
    seed();
    holdingGrid();
    const view = render(tree(2, 202601));

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));
    await saverIsHeld();

    view.rerender(tree(2, 202602));
    releaseSaver();

    await waitFor(() => expect(posts.filter((p) => p.url.includes('/submit'))).toHaveLength(1));
    expect(posts.find((p) => p.url.includes('/submit'))?.body).toMatchObject({ sheetDefId: 2, periodKey: 202601 });
  });

  it('Recalculate: вкладку перемкнули - у POST старий sheetDefId', async () => {
    mockFetch();
    seed();
    holdingGrid();
    const view = render(tree(2, 202601));

    fireEvent.click(await screen.findByRole('button', { name: /recalculate/i }));
    await saverIsHeld();

    view.rerender(tree(3, 202601));
    releaseSaver();

    await waitFor(() => expect(posts.filter((p) => p.url.includes('/recalculate'))).toHaveLength(1));
    expect(posts.find((p) => p.url.includes('/recalculate'))?.body).toMatchObject({ sheetDefId: 2, periodKey: 202601 });
  });

  it('Approve: діалог відкрито на одному аркуші, підтверджено на іншому - у POST аркуш, що питали', async () => {
    mockFetch();
    seed();
    const view = render(tree(2, 202601, 'Submitted'));

    fireEvent.click(await screen.findByRole('button', { name: /approve/i }));
    const dialog = await screen.findByRole('dialog');

    view.rerender(tree(3, 202601, 'Submitted'));

    const confirm = [...dialog.querySelectorAll('button')].find((button) => /approve/i.test(button.textContent ?? ''));
    if (confirm === undefined) throw new Error('немає кнопки підтвердження');
    fireEvent.click(confirm);

    await waitFor(() => expect(posts.filter((p) => p.url.includes('/approve'))).toHaveLength(1));
    expect(posts.find((p) => p.url.includes('/approve'))?.body).toMatchObject({ sheetDefId: 2, approved: true });
  });
});
