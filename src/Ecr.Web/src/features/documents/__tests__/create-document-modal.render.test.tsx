import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';

/**
 * Q-275 (побічна знахідка Q-274): `GET /api/v1/templates/{id}/versions` —
 * курсорний ендпоінт (Q-225), відповідь `{items, nextCursor, totalCount}`, а
 * НЕ голий масив.
 *
 * ⛔ Компонент типізував цю відповідь як `TemplateVersionSummary[]` і читав
 * `versionQueries[index]?.data ?? []` — `.data` при пагінованій відповіді НЕ
 * `undefined`, а об'єкт-обгортка, тож `?? []` не рятував, і `.filter` падав
 * із `TypeError: ((intermediate value) ?? []).filter is not a function` на
 * КОЖНОМУ відкритті «Новий документ» — той самий дефект, що Q-274 в
 * `CreateProjectModal.tsx`, знайдений тим самим агентом як побічна знахідка.
 *
 * ⚠ Мокається САМЕ РЕАЛІСТИЧНА пагінована форма відповіді (об'єкт-обгортка з
 * `items`), а не голий масив — тест, який мокає голий масив, лишився б
 * зеленим і до, і після фіксу і не довів би нічого.
 */
function respond(routes: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      const path = url.split('?')[0] ?? url;

      const body = routes[path];
      if (body === undefined) {
        throw new Error(`Немає мока для ${url}`);
      }

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <CreateDocumentModal opened onClose={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⛔ Один тест, не декілька — той самий клас вартості, що
// `create-project-modal.render.test.tsx` (Q-274): повний рендер цієї форми в
// jsdom (Mantine, кілька `Select`) сам по собі йде довго незалежно від
// коректності коду.
describe('CreateDocumentModal і пагінована відповідь версій шаблону (Q-275)', () => {
  it(
    'не падає й показує опубліковану версію з РЕАЛІСТИЧНОЇ пагінованої відповіді',
    async () => {
      respond({
        '/api/v1/projects': {
          items: [
            {
              id: 5,
              code: 'PRJ-1',
              status: 'Active',
              currentPeriodId: 202609,
              periodCount: 1,
              periodKind: 'Monthly',
              timeZoneId: 'Etc/UTC',
            },
          ],
          nextCursor: null,
          totalCount: 1,
        },
        '/api/v1/templates': {
          items: [{ id: 1, code: 'GEN72571044', versionCount: 1 }],
          nextCursor: null,
          totalCount: 1,
        },
        '/api/v1/templates/1/versions': {
          items: [
            {
              id: 10,
              version: '1.0.0.0',
              status: 'Published',
              presentationRevision: 0,
              clonedFromVersionId: null,
              publishedAt: '2026-09-12T00:00:00Z',
            },
          ],
          nextCursor: null,
          totalCount: 1,
        },
      });

      // ⛔ Головне твердження: до фіксу дані версій приходили як
      // `{items: [...]}`, а компонент читав `.data` напряму — щойно запит
      // `template-versions` вирішувався, `.filter` на об'єкті падав
      // `TypeError`, і саме це мало зʼявитися тут.
      show();

      expect(
        await screen.findByText('GEN72571044 · 1.0.0.0', {}, { timeout: 400_000 }),
      ).toBeDefined();
    },
    400_000,
  );
});
