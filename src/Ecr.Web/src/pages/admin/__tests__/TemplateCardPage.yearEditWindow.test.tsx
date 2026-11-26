import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TemplateCardPage } from '@/pages/admin/TemplateCardPage';
import { testTheme } from '@/test/render';

/**
 * b14 document-move (D-579, рішення людини 25.11): картка шаблону показує, до коли правляться річні дані
 * («рік проєкту + зміщення» чи «за станом місяця»), і перемикає це дією з «More» тим самим `PUT …/document-policy`,
 * передаючи прапорець B151 поточним (дія вікна не змінює правила «один документ на проєкт»).
 *
 * ⛔ Мутації (локально, 26.11): прибрати рядок `templates.yearEditWindow` з `KeyValue` → червоний перший тест;
 * у `setTemplateYearEditWindow` не передавати `singleDocumentPerProject` поточним (завжди `false`) → червоний другий.
 *
 * ⚠ Підписи звіряються з КЛЮЧАМИ каталогу (`⟦templates.…⟧`): каталог у цьому наборі не завантажується.
 */
const Card = {
  code: 'HSE401',
  createdAt: '2026-01-01T00:00:00Z',
  dependents: { documents: 1, projects: 1, publishedVersions: 1, versions: 1 },
  id: 3,
  isActive: true,
  nameL10n: { values: { en: 'HSE401' } },
  singleDocumentPerProject: true,
  yearEditWindow: false,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function respond(): { readonly bodies: string[] } {
  const state = { bodies: [] as string[] };
  let current = { ...Card };

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
          permissions: ['Template.Edit'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.endsWith('/document-policy') && method === 'PUT') {
        const body = String(init?.body ?? '{}');
        state.bodies.push(body);
        const parsed = JSON.parse(body) as { singleDocumentPerProject: boolean; yearEditWindow?: boolean };
        current = {
          ...current,
          singleDocumentPerProject: parsed.singleDocumentPerProject,
          yearEditWindow: parsed.yearEditWindow ?? current.yearEditWindow,
        };
        return json(current);
      }

      if (/\/api\/v1\/templates\/\d+$/.test(url)) return json(current);
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }]);

      return json(null);
    }),
  );

  return state;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <MemoryRouter initialEntries={['/admin/templates/3']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/admin/templates/:id" element={<TemplateCardPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const SlowEnvTimeout = 400_000;

async function findMoreItem(name: RegExp): Promise<HTMLElement> {
  const more = await screen.findByRole('button', { name: 'More' }, { timeout: SlowEnvTimeout });
  if (more.getAttribute('aria-expanded') !== 'true') fireEvent.click(more);

  return screen.findByRole('menuitem', { name }, { timeout: SlowEnvTimeout });
}

beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateCardPage: вікно редагування річних даних (b14, D-579)', () => {
  it(
    'картка показує рядок «редагування річних даних» зі значенням із картки',
    async () => {
      respond();
      show();

      expect(await screen.findByText(/templates\.yearEditWindow⟧/, {}, { timeout: SlowEnvTimeout })).toBeDefined();
      expect(screen.getByText(/templates\.yearEditWindowPeriodState/)).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'дія «рік проєкту + зміщення» шле PUT з поточним B151 і оновлює картку; зворотна дія вимикає',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      await user.click(await findMoreItem(/templates\.yearEditWindowOn/i));

      await waitFor(() => expect(state.bodies).toHaveLength(1), { timeout: SlowEnvTimeout });
      expect(JSON.parse(state.bodies[0] ?? '{}')).toEqual({ singleDocumentPerProject: true, yearEditWindow: true });
      expect(await screen.findByText(/templates\.yearEditWindowProjectYear/, {}, { timeout: SlowEnvTimeout })).toBeDefined();

      await user.click(await findMoreItem(/templates\.yearEditWindowOff/i));

      await waitFor(() => expect(state.bodies).toHaveLength(2), { timeout: SlowEnvTimeout });
      expect(JSON.parse(state.bodies[1] ?? '{}')).toEqual({ singleDocumentPerProject: true, yearEditWindow: false });
    },
    SlowEnvTimeout,
  );
});
