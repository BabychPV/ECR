import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';

/**
 * ✎ D-445: імпорт Excel поданого аркуша відкрито погоджувачу поточного кроку — як правку в сітці (D-442).
 *
 * ⛔ Доти пункт «Import…» ховався на будь-якому аркуші «лише для читання» (`canImport && !readOnly`), тож
 * погоджувач, якому сервер уже дозволяє писати поданий аркуш, не мав входу в імпорт. Тепер пункт є, коли аркуш
 * поданий і правки в ньому вирішує сервер (`approverImport`) і людина може погоджувати; хто саме з погоджувачів
 * на поточному кроці — каже перегляд імпорту на сервері. Мутація: повернути `canImport && !readOnly` — червоніє
 * перший тест; прибрати умову `actions.approve !== null` — червоніє другий.
 */

const Feb = 202602;

const user = (grant: 'Approve' | 'Write') => ({
  denies: [],
  grants: { 'Project:7': grant },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View', 'Document.Import'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'approver',
});

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function mockFetch(grant: 'Approve' | 'Write'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(user(grant));
      if (url.includes('/submission-target')) {
        return json({
          targetPeriodKey: null,
          nextPeriodKey: null,
          periods: [{ periodKey: Feb, state: 'Open', sheetStatus: 'Submitted', submittable: false }],
          monthColumns: [],
        });
      }
      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) return json([]);

      return json(null);
    }),
  );
}

function show(approverImport: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, Feb], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: { S1: 'Submitted' },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentActionBar
          documentId={1}
          periodKey={Feb}
          sheetDefId={42}
          sheetCode="S1"
          sheetName="Water"
          state="Submitted"
          lock={null}
          readOnly
          approverImport={approverImport}
          language="en"
          canImport
          canExport={false}
          validate={{ loading: false, run: () => undefined }}
          // Сталий пункт, щоб «More» малювалась і без імпорту (меню без пунктів не малюється).
          documentItems={[<Menu.Item key="probe">probe</Menu.Item>]}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Відкриває «More», коли рядок дій уже остаточний (профіль і ціль подання доїхали). */
async function openMore(): Promise<void> {
  const more = await screen.findByTestId('document-more');
  await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/v1/me'))).toBe(true));
  await userEvent.setup().click(more);
  await screen.findByRole('menuitem', { name: 'probe' });
}

const importItem = (): HTMLElement | null => screen.queryByRole('menuitem', { name: /import\.pick/ });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentActionBar: імпорт поданого аркуша погоджувачем (D-445)', () => {
  it('погоджувач на поданому аркуші бачить «Import…» у «More»', async () => {
    mockFetch('Approve');
    show(true);

    // Право погоджувати доїхало: на панелі — рішення погоджувача.
    await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    await openMore();

    expect(await screen.findByRole('menuitem', { name: /import\.pick/ })).toBeTruthy();
  });

  it('без права погоджувати (Write) — на поданому аркуші пункту немає', async () => {
    mockFetch('Write');
    show(true);
    await openMore();

    expect(importItem()).toBeNull();
  });

  it('поданий аркуш під замком чи на вузькому екрані (approverImport = false) — пункту немає навіть погоджувачу', async () => {
    mockFetch('Approve');
    show(false);

    await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    await openMore();

    expect(importItem()).toBeNull();
  });
});
