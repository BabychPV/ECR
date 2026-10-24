import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';

/**
 * D-441 «миготіння скелета» (HANDOFF-2026-10-24 §4).
 *
 * 1. Зміна аркуша чи періоду міняє ключ `submission-target`, і на старому коді ВЕСЬ рядок дій на один запит
 *    підмінявся скелетом (`document-actions-pending`): «More» зникала й поверталась. Тепер лишається відповідь
 *    попереднього ключа (`keepPreviousData`): кнопки на місці (ті самі вузли, той самий текст — ширина та сама),
 *    але «Подати» недоступна (`aria-disabled`, опис «Loading…») і клік НЕ шле ціль попереднього аркуша/періоду.
 *    Після відповіді — кнопка доступна й подає НОВУ ціль.
 * 2. Людина без права подання (грант нижче `Write`, симуляція) скелета не бачить узагалі: «Подати» їй не буде
 *    за будь-якої відповіді — рядок малюється остаточним з першого кадру.
 *
 * ⛔ На старому коді червоніють «скелета немає під час перемикання» і «без права — без скелета».
 */

const Jan = 202601;
const Feb = 202602;

const Author = {
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

/** Два неподані: ціль — січень, ▾ і «Next to submit: лютий». */
const TwoOpen = {
  targetPeriodKey: Jan,
  nextPeriodKey: Feb,
  periods: [
    { periodKey: Jan, state: 'Grace', sheetStatus: 'Draft', submittable: true },
    { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true },
  ],
  monthColumns: [],
};

/** Січень уже поданий: ціль — лютий, без вибору. */
const OnlyFeb = {
  targetPeriodKey: Feb,
  nextPeriodKey: null,
  periods: [
    { periodKey: Jan, state: 'Grace', sheetStatus: 'Submitted', submittable: false },
    { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true },
  ],
  monthColumns: [],
};

type Body = { sheetDefId?: number; periodKey?: number };

/**
 * Кожна відповідь `submission-target` (ключ `аркуш:період`) тримається, доки тест не викличе `release`.
 * Подання (`POST …/submit`) записуються в `submits`.
 */
function mockFetch(me: unknown = Author): {
  readonly release: (sheetDefId: number, periodKey: number, body: unknown) => void;
  readonly asked: (sheetDefId: number, periodKey: number) => boolean;
  readonly submits: Body[];
} {
  const gates = new Map<string, { promise: Promise<unknown>; resolve: (body: unknown) => void }>();
  const gate = (key: string): { promise: Promise<unknown>; resolve: (body: unknown) => void } => {
    let entry = gates.get(key);
    if (entry === undefined) {
      let resolve: (body: unknown) => void = () => undefined;
      const promise = new Promise<unknown>((done) => {
        resolve = done;
      });
      entry = { promise, resolve };
      gates.set(key, entry);
    }
    return entry;
  };
  const askedKeys = new Set<string>();
  const submits: Body[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      // ⚠ Той самий профіль, що в кеші: `/me` перечитується (staleTime 0), і інший профіль змінив би право.
      if (url.includes('/api/v1/me')) return json(me);

      // ⚠ Раніше за `/submit`: `/submission-target` містить і його.
      if (url.includes('/submission-target')) {
        const query = new URL(url, 'http://localhost').searchParams;
        const key = `${query.get('sheetDefId') ?? ''}:${query.get('periodKey') ?? ''}`;
        askedKeys.add(key);

        return json(await gate(key).promise);
      }

      if (url.includes('/submit') && init?.method === 'POST') {
        submits.push(JSON.parse(String(init.body)) as Body);

        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) return json([]);

      return json(null);
    }),
  );

  return {
    release: (sheetDefId, periodKey, body) => gate(`${String(sheetDefId)}:${String(periodKey)}`).resolve(body),
    asked: (sheetDefId, periodKey) => askedKeys.has(`${String(sheetDefId)}:${String(periodKey)}`),
    submits,
  };
}

function newClient(me: unknown = Author): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // Профіль уже є (як на живій сторінці: оболонка застосунку читає `/me` раніше за документ).
  client.setQueryData(['me'], me);
  for (const period of [Jan, Feb]) {
    client.setQueryData(['document', 1, period], {
      businessKey: 'DOC-1',
      createdAt: '2026-01-01T00:00:00Z',
      id: 1,
      nameL10n: null,
      projectId: 7,
      sheetCount: 2,
      sheetStates: { S1: 'Draft', S2: 'Draft' },
    });
  }
  return client;
}

function bar(client: QueryClient, sheetDefId: number, periodKey: number): JSX.Element {
  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentActionBar
          documentId={1}
          periodKey={periodKey}
          sheetDefId={sheetDefId}
          sheetCode={sheetDefId === 42 ? 'S1' : 'S2'}
          sheetName={sheetDefId === 42 ? 'Water' : 'Air'}
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
    </MantineProvider>
  );
}

/** Ідентичність рядка дій: порядок кнопок за `data-testid` чи текстом. */
function rowShape(): (string | null)[] {
  return within(screen.getByTestId('document-actions'))
    .getAllByRole('button')
    .map((button) => button.getAttribute('data-testid') ?? button.textContent);
}

/** Дати промісам і React відпрацювати: клік, якби він щось слав, уже дійшов би до `fetch`. */
async function flush(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe.each([
  { what: 'аркуш', next: { sheetDefId: 43, periodKey: Feb } },
  { what: 'період', next: { sheetDefId: 42, periodKey: Jan } },
])('DocumentActionBar: перемкнули $what — без скелета, «Подати» недоступна до нової цілі (D-441)', ({ next }) => {
  it('старі кнопки лишаються, «Подати» вимкнена й нічого не шле; після відповіді — подає нову ціль', async () => {
    const server = mockFetch();
    const client = newClient();
    const view = render(bar(client, 42, Feb));

    server.release(42, Feb, TwoOpen);
    const submit = await screen.findByTestId('document-submit');
    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Jan)));
    const more = screen.getByTestId('document-more');
    const label = submit.textContent;
    const shape = rowShape();

    // ── Перемикання: відповідь нового ключа притримана ─────────────────────────────────────────────
    view.rerender(bar(client, next.sheetDefId, next.periodKey));
    await waitFor(() => expect(server.asked(next.sheetDefId, next.periodKey)).toBe(true));

    // ⛔ Старий код: тут увесь рядок — скелет, «More» немає.
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
    // Ті самі вузли й той самий порядок: нічого не перемонтовано, ширина «Подати» (текст) та сама — «More» на місці.
    expect(screen.getByTestId('document-more')).toBe(more);
    expect(screen.getByTestId('document-submit')).toBe(submit);
    expect(submit.textContent).toBe(label);
    expect(rowShape()).toEqual(shape);

    // «Подати» недоступна, і це чути: `aria-disabled` + опис «Loading…»; ціль попереднього ключа не названа атрибутом.
    expect(submit.getAttribute('aria-disabled')).toBe('true');
    expect(submit.hasAttribute('data-disabled')).toBe(true);
    expect(submit.hasAttribute('data-period-key')).toBe(false);
    const describedBy = submit.getAttribute('aria-describedby') ?? '';
    expect(describedBy).not.toBe('');
    expect(document.getElementById(describedBy.split(' ').at(-1) ?? '')?.textContent).toContain('common.loading');
    // ▾ теж на місці, але вибирати серед періодів попередньої відповіді не дає.
    const pick = screen.getByTestId('submit-period-pick');
    expect(pick.getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(pick);
    await flush();
    expect(document.querySelector('[data-submit-period]')).toBeNull();

    // ⛔ Клік (і Enter/Space — той самий `click`) під час перевірки НЕ шле ціль попереднього аркуша/періоду.
    fireEvent.click(submit);
    fireEvent.keyDown(submit, { key: 'Enter' });
    await flush();
    expect(server.submits).toEqual([]);

    // ── Відповідь нового ключа: кнопка доступна і подає НОВУ ціль ────────────────────────────────────
    server.release(next.sheetDefId, next.periodKey, OnlyFeb);
    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Feb)));
    expect(submit.hasAttribute('aria-disabled')).toBe(false);
    expect(submit.hasAttribute('data-disabled')).toBe(false);
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();

    fireEvent.click(submit);
    await waitFor(() => expect(server.submits).toHaveLength(1));
    expect(server.submits[0]).toEqual({ sheetDefId: next.sheetDefId, periodKey: Feb, acknowledgeWarnings: false });
  });
});

describe('DocumentActionBar: без права подання — без скелета (D-441 CLS)', () => {
  it.each([
    { who: 'грант Read (нижче Write)', me: { ...Author, grants: { 'Project:7': 'Read' } } },
    { who: 'симуляція чужих прав', me: { ...Author, isSimulation: true, simulatedForUserId: 5 } },
  ])('$who: рядок остаточний з першого кадру, відповідь submission-target його не змінює', async ({ me }) => {
    const server = mockFetch(me);
    const client = newClient(me);
    render(bar(client, 42, Feb));

    // ⛔ Старий код: скелет, доки `submission-target` у дорозі, хоча «Подати» цій людині не буде ніколи.
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
    expect(screen.queryByTestId('document-submit-placeholder')).toBeNull();
    const more = screen.getByTestId('document-more');
    expect(screen.getByRole('button', { name: '⟦document.validate⟧' })).toBeTruthy();
    const shape = rowShape();

    // Запит однаково йде (статуси місячних колонок читають його відповідь), але рядок на нього не чекає.
    await waitFor(() => expect(server.asked(42, Feb)).toBe(true));
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();

    server.release(42, Feb, TwoOpen);
    await flush();

    expect(screen.queryByTestId('document-submit')).toBeNull();
    expect(screen.getByTestId('document-more')).toBe(more);
    expect(rowShape()).toEqual(shape);
  });

  it('з правом подання і без відповіді — скелет лишається (місце «Подати» ще невідоме)', async () => {
    const server = mockFetch();
    render(bar(newClient(), 42, Feb));

    expect(screen.getByTestId('document-actions-pending')).toBeTruthy();

    server.release(42, Feb, TwoOpen);
    expect(await screen.findByTestId('document-submit')).toBeTruthy();
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
  });
});
