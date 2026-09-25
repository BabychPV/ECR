import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { TemplateCardPage } from '@/pages/admin/TemplateCardPage';
import { useSession } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';
import { t } from '@/shared/i18n';

/**
 * U-19 (UX-PASS 2026-09-23): сторінка шаблону знала менше, ніж рядок
 * переліку, з якого на неї прийшли.
 *
 * ⛔ Що доводиться:
 *  1. на картці є перелік версій, і кожна веде на СВОЮ структуру
 *     (`/admin/templates/:id/versions/:versionId`) — перехід перевіряється
 *     кліком, а не лише атрибутом `href`;
 *  2. окремого «← Templates» під шапкою немає — роль «назад» виконує хлібна
 *     крихта (`AppLayout`), і другий такий самий шлях поруч — дубль;
 *  3. «New version» — лише з правом `Template.Edit` (та сама перевірка
 *     `can(...)`, що в переліку): є з правом, немає без нього;
 *  4. нова версія клонується з ОСТАННЬОЇ — правило, яке раніше жило лише в
 *     `TemplatesPage` і тепер спільне (`NewTemplateVersionModal`).
 *
 * Мутаційно перевірено (кожна → червоний):
 *  - прибрати `<TemplateVersionsSection …/>` з картки → пункт 1;
 *  - повернути `back={{ label: …, href: '/admin/templates' }}` у `PageHeader` → пункт 2;
 *  - `editable={editable}` → `editable={true}` на картці → пункт 3 (без права);
 *  - `cloneFrom={latest}` → `cloneFrom={null}` у секції → пункт 4.
 */
const Card = {
  code: 'TPL-EMIS',
  createdAt: '2026-01-01T00:00:00Z',
  dependents: { documents: 5, projects: 2, publishedVersions: 1, versions: 2 },
  id: 7,
  isActive: true,
  nameL10n: { values: { en: 'Stationary sources' } },
};

/**
 * ⚠ Дві версії з РІЗНИМИ id, і остання — не перша: інакше мутація «клонувати
 * з першої» лишила б пункт 4 зеленим.
 */
const Versions = {
  items: [
    { id: 11, version: '1.0.0.0', presentationRevision: 0, status: 'Published', publishedAt: '2026-02-01T00:00:00Z', clonedFromVersionId: null },
    { id: 12, version: '1.1.0.0', presentationRevision: 3, status: 'Draft', publishedAt: null, clonedFromVersionId: 11 },
  ],
  nextCursor: null,
  totalCount: 2,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function respond(permissions: readonly string[]): { versionBodies: string[] } {
  const state = { versionBodies: [] as string[] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (/\/api\/v1\/templates\/7\/versions/.test(url)) {
        if (method === 'POST') {
          state.versionBodies.push(String(init?.body ?? ''));

          return json({ versionId: 13 }, 201);
        }

        return json(Versions);
      }

      if (/\/api\/v1\/templates\/7$/.test(url)) return json(Card);

      return json(null);
    }),
  );

  return state;
}

/** Зонд сесії: «кнопки немає» без нього зелене й тоді, коли `/me` ще не приїхав. */
function SessionProbe(): JSX.Element {
  const session = useSession();

  return <span data-testid="session">{session.data === undefined ? 'pending' : 'ready'}</span>;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/templates/7']}>
        <QueryClientProvider client={client}>
          <SessionProbe />
          <Routes>
            <Route path="/admin/templates/:id" element={<TemplateCardPage />} />
            <Route
              path="/admin/templates/:id/versions/:versionId"
              element={<p data-testid="version-page">version page</p>}
            />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Timeout = 60_000;

async function versionsTable(): Promise<HTMLElement> {
  await screen.findByRole('heading', { name: 'Stationary sources' }, { timeout: Timeout });

  return waitFor(
    () => {
      const section = document.querySelector<HTMLElement>('[data-template-versions]');
      expect(section).not.toBeNull();
      expect(within(section as HTMLElement).getByRole('link', { name: '1.1.0.0' })).toBeDefined();

      return section as HTMLElement;
    },
    { timeout: Timeout },
  );
}

describe('TemplateCardPage: версії шаблону і один «назад» (U-19)', () => {
  it(
    'показує кожну версію посиланням на її структуру, і клік туди переходить',
    async () => {
      respond(['Template.Edit']);
      const user = userEvent.setup();
      show();

      const section = await versionsTable();
      const first = within(section).getByRole('link', { name: '1.0.0.0' });

      expect(first.getAttribute('href')).toBe('/admin/templates/7/versions/11');
      expect(within(section).getByRole('link', { name: '1.1.0.0' }).getAttribute('href')).toBe(
        '/admin/templates/7/versions/12',
      );

      // ⛔ Лічильник правок вигляду підписаний і показаний лише там, де він
      // щось каже: у версії з трьома правками — є, у версії без правок — немає
      // (раніше сирі «· r3» / «· r0» стояли в тексті посилання).
      expect(within(section).getByText(t('version.presentationRevision', { revision: 3 }))).toBeDefined();
      expect(within(section).queryByText(t('version.presentationRevision', { revision: 0 }))).toBeNull();
      expect(section.textContent ?? '').not.toMatch(/·\s*r\d/);

      await user.click(first);
      expect(await screen.findByTestId('version-page', {}, { timeout: Timeout })).toBeDefined();
    },
    Timeout * 3,
  );

  it(
    'окремого посилання «← Templates» немає — назад веде хлібна крихта',
    async () => {
      respond(['Template.Edit']);
      show();

      await versionsTable();

      expect(screen.queryByRole('link', { name: /←/ })).toBeNull();
      expect(document.querySelector('a[href="/admin/templates"]')).toBeNull();
    },
    Timeout * 3,
  );

  it(
    '«New version» є з правом Template.Edit і клонує з ОСТАННЬОЇ версії',
    async () => {
      const state = respond(['Template.Edit']);
      const user = userEvent.setup();
      show();

      const section = await versionsTable();
      await user.click(within(section).getByRole('button', { name: /templates\.newVersion/ }));

      const dialog = await screen.findByRole('dialog', {}, { timeout: Timeout });
      await user.type(within(dialog).getByRole('textbox'), '1.2.0.0');
      await user.click(within(dialog).getByRole('button', { name: /common\.save/ }));

      await waitFor(() => expect(state.versionBodies).toHaveLength(1), { timeout: Timeout });
      expect(JSON.parse(state.versionBodies[0] ?? '{}')).toEqual({
        versionNumber: '1.2.0.0',
        cloneFromVersionId: 12,
      });
    },
    Timeout * 3,
  );

  it(
    '«New version» немає без права Template.Edit',
    async () => {
      respond([]);
      show();

      const section = await versionsTable();
      await waitFor(() => expect(screen.getByTestId('session').textContent).toBe('ready'), {
        timeout: Timeout,
      });

      expect(within(section).queryByRole('button', { name: /templates\.newVersion/ })).toBeNull();
    },
    Timeout * 3,
  );
});
