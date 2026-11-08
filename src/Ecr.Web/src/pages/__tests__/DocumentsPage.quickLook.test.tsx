import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * `UI-29`: швидкий перегляд документа зі списку (макет `screens-work.js` →
 * `ctx.panel('*')`, `13-docs-quicklook.png`).
 *
 * Приймання картки: 1) відкривається з «ока» і з `?panel=`; 2) «Open document» веде на документ (✎ 08.11: без
 * місяця); 3) Esc закриває (і фокус вертається на «око»).
 * ✎ 08.11: перелік лише за рік — помісячні блоки шторки (аркуші й заповненість, «Can approve now», «Latest changes»)
 * прибрано як недосяжні разом із їхніми тестами; шторка не робить запитів `tables/status` і `workflow/history`.
 */
const Period = 202609;
const SlowEnvTimeout = 20_000;
const TestTimeout = 60_000;

const doc = {
  id: 11,
  businessKey: 'DOC-000011',
  nameL10n: { values: { en: 'Atyrau refinery' } },
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 2,
  sheetStates: { GEN: 'Approved', AIR: 'Draft' },
  sheets: [
    { code: 'GEN', nameL10n: { values: { en: 'General info' } }, state: 'Approved' },
    { code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, state: 'Draft' },
  ],
  modifiedAt: '2026-10-05T11:42:00Z',
  modifiedByDisplayName: 'Aigerim Sadykova',
  errorCount: 3,
  warningCount: 0,
  hasLateEdits: false,
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 1 });
      if (url.includes('/api/v1/documents/11')) return json(doc);
      if (url.includes('/api/v1/documents')) return json({ items: [doc], nextCursor: null, totalCount: 1 });
      if (url.includes('/api/v1/projects')) {
        return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.pathname + location.search}</span>;
}

function show(url = '/?year=2026'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** `findByRole` по всій таблиці в jsdom повільний — шукаємо «око» за атрибутом, а ім'я звіряємо окремо. */
async function findEye(): Promise<HTMLButtonElement> {
  const eye = await waitFor(
    () => {
      const found = document.querySelector<HTMLButtonElement>('[data-quick-look="11"]');
      if (found === null) throw new Error('«око» ще не з\'явилося');
      return found;
    },
    { timeout: SlowEnvTimeout },
  );
  expect(eye.getAttribute('aria-label')).toBe('⟦documents.quickLook (key=DOC-000011)⟧');

  return eye;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: швидкий перегляд (UI-29)', () => {
  it(
    // ✎ Projects/Documents 08.11: перелік лише за рік — шторка зі сторінки без місяця: пояснення замість аркушів, без запитів таблиць і історії.
    '«око» відкриває шторку під ?panel=<id>: назва, відповідальні; станів аркушів за рік немає — пояснення, а не порожнеча',
    async () => {
      mockFetch();
      show();
      const user = userEvent.setup();

      const eye = await findEye();
      await user.click(eye);

      expect(screen.getByTestId('location').textContent).toContain('panel=11');

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      expect(within(drawer).getByText('Atyrau refinery')).toBeTruthy();
      expect(drawer.querySelector('[data-quick-look-no-period]')?.textContent).toBe('⟦documents.quickLookNoPeriod⟧');
      expect(within(drawer).getByText('Aigerim Sadykova')).toBeTruthy();
      expect(drawer.querySelector('[data-quick-look-fill]')).toBeNull();

      const calls = vi.mocked(fetch).mock.calls.map(([input]) => String(input));
      expect(calls.some((url) => url.includes('/tables/status') || url.includes('/workflow/history'))).toBe(false);
      // Документ уже на сторінці — картку окремо не дочитуємо (без місяця вона нічого понад рядок не несе).
      expect(calls.some((url) => url.includes('/api/v1/documents/11'))).toBe(false);
    },
    TestTimeout,
  );

  it(
    // ✎ Projects/Documents 08.11: перелік лише за рік — «Open document» веде на документ без місяця (навіть зі старої адреси з periodKey).
    '«Open document» веде на документ (без місяця — його обирають у документі)',
    async () => {
      mockFetch();
      show(`/?periodKey=${String(Period)}&panel=11`);
      const user = userEvent.setup();

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await user.click(await within(drawer).findByRole('button', { name: '⟦documents.openDocument⟧' }, { timeout: SlowEnvTimeout }));

      expect(screen.getByTestId('location').textContent).toBe('/documents/11');
    },
    TestTimeout,
  );

  it(
    'Esc закриває шторку, прибирає ?panel= і вертає фокус на «око»',
    async () => {
      mockFetch();
      show();
      const user = userEvent.setup();

      const eye = await findEye();
      await user.click(eye);
      await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });

      await user.keyboard('{Escape}');

      await waitFor(() => expect(screen.getByTestId('location').textContent).not.toContain('panel='), {
        timeout: SlowEnvTimeout,
      });
      await waitFor(() => expect(document.activeElement).toBe(eye), { timeout: SlowEnvTimeout });
    },
    TestTimeout,
  );
});
