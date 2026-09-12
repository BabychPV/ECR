import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';

/**
 * Q-275 (побічна знахідка Q-274): `GET /api/v1/templates/{id}/versions` —
 * курсорний ендпоінт (Q-225), відповідь `{items, nextCursor, totalCount}`, а
 * НЕ голий масив.
 *
 * ⛔ Сторінка типізувала цю відповідь як `TemplateVersionSummary[]` і читала
 * `versions.data ?? []` — `.data` при пагінованій відповіді НЕ `undefined`,
 * а об'єкт-обгортка, тож `?? []` не рятував, і `.map` нижче падав
 * `TypeError` на кожному відкритті вкладки «Вираз шаблону» (діалект
 * `Template` — початковий за замовчуванням) — той самий дефект, що Q-274 в
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
      <QueryClientProvider client={client}>
        <ExpressionsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⛔ Один тест, не декілька — той самий клас вартості, що
// `create-project-modal.render.test.tsx` (Q-274): повний рендер цієї
// сторінки в jsdom (Mantine `Select`, редактор виразів) сам по собі йде
// довго незалежно від коректності коду.
describe('ExpressionsPage і пагінована відповідь версій шаблону (Q-275)', () => {
  it(
    'не падає й показує версію шаблону з РЕАЛІСТИЧНОЇ пагінованої відповіді',
    async () => {
      respond({
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
      // `{items: [...]}`, а сторінка читала `.data` напряму — щойно запит
      // `template-versions` вирішувався, `.map` на об'єкті падав
      // `TypeError`, і саме це мало зʼявитися тут.
      show();

      expect(
        await screen.findByText('1.0.0.0 · Published', {}, { timeout: 400_000 }),
      ).toBeDefined();
    },
    400_000,
  );
});
