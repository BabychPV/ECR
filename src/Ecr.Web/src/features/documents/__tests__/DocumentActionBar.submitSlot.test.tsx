import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';

/**
 * D-441 CLS: рядок дій не перебудовується, коли `GET …/submission-target` відповідає ПІСЛЯ першого малюнка.
 *
 * ⛔ На старому коді «Validate» і «More» малювалися одразу, а «Подати: <період>» (і ▾, «Next to submit: …») —
 * лише з відповіддю: група дій, притиснута праворуч, розширювалась, «More» з'їжджала ліворуч і вниз (~200 px)
 * між натиском і відпуском миші, клік губився (e2e-stand 39994116932, 40023308269). Тепер до відповіді в тому
 * самому контейнері дій — скелет (не кнопки), а остаточні дії з'являються разом, одним комітом.
 */

const Jan = 202601;
const Feb = 202602;

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Approve' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const TwoOpen = {
  targetPeriodKey: Jan,
  nextPeriodKey: Feb,
  periods: [
    { periodKey: Jan, state: 'Grace', sheetStatus: 'Draft', submittable: true },
    { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true },
  ],
  monthColumns: [],
};

const NothingToSubmit = {
  targetPeriodKey: null,
  nextPeriodKey: null,
  periods: [
    { periodKey: Jan, state: 'Grace', sheetStatus: 'Submitted', submittable: false },
    { periodKey: Feb, state: 'Open', sheetStatus: 'Submitted', submittable: false },
  ],
  monthColumns: [],
};

/** Відповідь `submission-target` віддається лише за `release()`: так видно рядок дій ДО неї. */
function mockFetch(): { release: (reply: () => Response) => void } {
  let release: (reply: () => Response) => void = () => undefined;
  const held = new Promise<() => Response>((resolve) => {
    release = resolve;
  });

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/submission-target')) return (await held)();
      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) return json([]);

      return json(null);
    }),
  );

  return { release: (reply) => release(reply) };
}

function show(client = new QueryClient({ defaultOptions: { queries: { retry: false } } })): void {
  client.setQueryData(['document', 1, Feb], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: { S1: 'Draft' },
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
          state="Draft"
          lock={null}
          readOnly={false}
          language="en"
          canImport={false}
          canExport={false}
          validate={{ loading: false, run: () => undefined }}
          documentItems={[]}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Профіль (`/me`) доїхав: без нього «Подати» немає взагалі, і скелет нічого б не доводив. */
async function sessionLoaded(): Promise<void> {
  await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/v1/me'))).toBe(true));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentActionBar: місце «Подати» зарезервоване з першого малюнка (D-441 CLS)', () => {
  it('до відповіді — скелет у контейнері дій (не кнопки); після — у ТОМУ Ж контейнері Validate · More · Подати', async () => {
    const server = mockFetch();
    show();
    await sessionLoaded();

    const actions = screen.getByTestId('document-actions');
    const pending = within(actions).getByTestId('document-actions-pending');
    // ⚠ Для читалки — «завантаження», а не порожнеча; жодної дії, яку можна натиснути до відповіді.
    expect(pending.getAttribute('role')).toBe('status');
    expect(pending.getAttribute('aria-busy')).toBe('true');
    expect(pending.textContent).toContain('common.loading');
    expect(within(actions).getByTestId('document-submit-placeholder')).toBeTruthy();
    expect(within(actions).queryAllByRole('button')).toHaveLength(0);
    // ⛔ «More» до відповіді немає: інакше вона стрибнула б, коли праворуч від неї з'явиться «Подати».
    expect(screen.queryByTestId('document-more')).toBeNull();

    server.release(() => json(TwoOpen));

    const submit = await within(actions).findByTestId('document-submit');
    // Той самий вузол контейнера: замінено лише вміст.
    expect(screen.getByTestId('document-actions')).toBe(actions);
    expect(within(actions).queryByTestId('document-actions-pending')).toBeNull();
    expect(screen.queryByTestId('document-submit-placeholder')).toBeNull();
    expect(submit.getAttribute('data-period-key')).toBe(String(Jan));

    // ▾ і «Next to submit» — у тому ж коміті, що й кнопка: остаточна форма одразу.
    expect(within(actions).getByTestId('submit-period-pick')).toBeTruthy();
    expect(within(actions).getByTestId('submit-next-period')).toBeTruthy();

    // Порядок макета (UI-14): Validate · More · Подати (▾ поруч із нею).
    const buttons = within(actions).getAllByRole('button');
    expect(buttons.map((button) => button.getAttribute('data-testid') ?? button.textContent)).toEqual([
      '⟦document.validate⟧',
      'document-more',
      'document-submit',
      'submit-period-pick',
    ]);
  });

  it('подавати нічого (ціль порожня) — скелет зникає, «Подати» немає, решта дій на місці', async () => {
    const server = mockFetch();
    show();
    await sessionLoaded();

    expect(screen.getByTestId('document-submit-placeholder')).toBeTruthy();

    server.release(() => json(NothingToSubmit));

    const more = await screen.findByTestId('document-more');
    const actions = screen.getByTestId('document-actions');
    expect(within(actions).queryByTestId('document-actions-pending')).toBeNull();
    expect(screen.queryByTestId('document-submit-placeholder')).toBeNull();
    expect(screen.queryByTestId('document-submit')).toBeNull();
    expect(screen.queryByTestId('submit-next-period')).toBeNull();
    // «More» — остання: на місці «Подати» не лишилося порожнього проміжку.
    expect(actions.lastElementChild?.contains(more)).toBe(true);
    expect(within(actions).getByRole('button', { name: '⟦document.validate⟧' })).toBeTruthy();
  });

  it('один неподаний період — скелет замінює сама кнопка, без ▾ і «Next to submit»', async () => {
    const server = mockFetch();
    show();
    await sessionLoaded();

    server.release(() =>
      json({
        targetPeriodKey: Feb,
        nextPeriodKey: null,
        periods: [
          { periodKey: Jan, state: 'Grace', sheetStatus: 'Submitted', submittable: false },
          { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true },
        ],
        monthColumns: [],
      }),
    );

    const submit = await screen.findByTestId('document-submit');
    expect(submit.getAttribute('data-period-key')).toBe(String(Feb));
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
    expect(screen.queryByTestId('submit-period-pick')).toBeNull();
    expect(screen.queryByTestId('submit-next-period')).toBeNull();
  });

  it('збій сервера з повторами не тримає дії схованими: після ПЕРШОЇ відмови рядок малюється', async () => {
    const server = mockFetch();
    // Повтор є, але далекий: рядок мусить з'явитися, не чекаючи його.
    const client = new QueryClient({ defaultOptions: { queries: { retry: 1, retryDelay: 60_000 } } });
    show(client);
    await sessionLoaded();

    expect(screen.getByTestId('document-actions-pending')).toBeTruthy();

    server.release(() => json({ title: 'boom', status: 500 }, 500));

    expect(await screen.findByTestId('document-more')).toBeTruthy();
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
    expect(screen.getByRole('button', { name: '⟦document.validate⟧' })).toBeTruthy();

    // Далекий повтор не лишається висіти після тесту.
    await client.cancelQueries();
  });
});
