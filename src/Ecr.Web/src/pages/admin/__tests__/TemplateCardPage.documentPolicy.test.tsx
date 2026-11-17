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
 * B151: картка шаблону показує правило «документів на проєкт» і перемикає його дією з «More»
 * (`PUT …/document-policy`). Відмова `409 ECR-TMPL-0409` (дублікати) — тост, картка лишається з «кілька».
 * Тест 409 — мутація `onError: showApiError` → `onError: () => {}` дає червоний (тосту немає).
 *
 * ⛔ Мутації: прибрати рядок `templates.documentsPerProject` з `KeyValue` → червоний перший тест; поміняти
 * місцями `mutate(true)` / `mutate(false)` → червоний другий (тіло запиту); `onSuccess` без `applyCard` → картка не
 * оновлюється, червоний другий (рядок лишається «Many»).
 *
 * ⚠ Підписи звіряються з КЛЮЧАМИ каталогу (`⟦templates.…⟧`): каталог у цьому наборі не завантажується.
 */
const Card = {
  code: 'TPL-HSE',
  createdAt: '2026-01-01T00:00:00Z',
  dependents: { documents: 1, projects: 1, publishedVersions: 1, versions: 1 },
  id: 7,
  isActive: true,
  nameL10n: { values: { en: 'HSE400' } },
  singleDocumentPerProject: false,
};

interface Stub {
  readonly policyBodies: string[];
  conflict: boolean;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status === 200 ? 'application/json' : 'application/problem+json' },
  });
}

function respond(conflict = false): Stub {
  const state: Stub = { policyBodies: [], conflict };
  let single = false;

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
        state.policyBodies.push(String(init?.body ?? ''));
        if (state.conflict) {
          return json(
            {
              title: 'Duplicates',
              status: 409,
              errorCode: 'ECR-TMPL-0409',
              correlationId: 'cid-test-0151',
              detail: 'Some projects already have several documents.',
              messageKey: 'err.ECR-TMPL-0409.singleDocumentDuplicates',
            },
            409,
          );
        }

        single = (JSON.parse(String(init?.body ?? '{}')) as { singleDocumentPerProject: boolean }).singleDocumentPerProject;
        return json({ ...Card, singleDocumentPerProject: single });
      }

      if (/\/api\/v1\/templates\/\d+$/.test(url)) return json({ ...Card, singleDocumentPerProject: single });
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
      <MemoryRouter initialEntries={['/admin/templates/7']}>
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

describe('TemplateCardPage: правило «один документ на проєкт» (B151)', () => {
  it(
    'картка показує рядок «документів на проєкт» зі значенням із картки',
    async () => {
      respond();
      show();

      expect(await screen.findByText(/templates\.documentsPerProject⟧/, {}, { timeout: SlowEnvTimeout })).toBeDefined();
      expect(screen.getByText(/templates\.documentsPerProjectMany/)).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'дія «дозволити один документ» шле PUT з true і оновлює картку; зворотна дія шле false',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      await user.click(await findMoreItem(/templates\.allowOneDocument/i));

      await waitFor(() => expect(state.policyBodies).toHaveLength(1), { timeout: SlowEnvTimeout });
      expect(JSON.parse(state.policyBodies[0] ?? '{}')).toEqual({ singleDocumentPerProject: true });
      expect(await screen.findByText(/templates\.documentsPerProjectOne/, {}, { timeout: SlowEnvTimeout })).toBeDefined();

      await user.click(await findMoreItem(/templates\.allowManyDocuments/i));

      await waitFor(() => expect(state.policyBodies).toHaveLength(2), { timeout: SlowEnvTimeout });
      expect(JSON.parse(state.policyBodies[1] ?? '{}')).toEqual({ singleDocumentPerProject: false });
    },
    SlowEnvTimeout,
  );

  it(
    '409 ECR-TMPL-0409 (дублікати) — тост із кодом, картка лишається «кілька»',
    async () => {
      const state = respond(true);
      const user = userEvent.setup();
      show();

      await user.click(await findMoreItem(/templates\.allowOneDocument/i));

      await waitFor(() => expect(state.policyBodies).toHaveLength(1), { timeout: SlowEnvTimeout });
      // Тост несе відмову сервера (`showApiError`: подробиця, коли її можна показати, інакше назва з кодом).
      expect(
        await screen.findByText(/several documents|ECR-TMPL-0409/, {}, { timeout: SlowEnvTimeout }),
      ).toBeDefined();
      expect(screen.getByText(/templates\.documentsPerProjectMany/)).toBeDefined();
    },
    SlowEnvTimeout,
  );
});
