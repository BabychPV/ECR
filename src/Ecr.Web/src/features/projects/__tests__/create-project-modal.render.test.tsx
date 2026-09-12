import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateProjectModal } from '@/features/projects/CreateProjectModal';

/**
 * Q-274: `GET /api/v1/templates/{id}/versions` — курсорний ендпоінт
 * (Q-225), відповідь `{items, nextCursor, totalCount}`, а НЕ голий масив.
 *
 * ⛔ Компонент типізував цю відповідь як `TemplateVersionSummary[]` і читав
 * `versionQueries[index]?.data ?? []` — `.data` при пагінованій відповіді
 * НЕ `undefined`, а об'єкт-обгортка, тож `?? []` не рятував, і `.filter`
 * падав із `TypeError: ((intermediate value) ?? []).filter is not a
 * function` на КОЖНОМУ відкритті «Новий проєкт» (`src/Ecr.Web/e2e/keyboardPath.spec.ts`,
 * побічна знахідка Q-273; відтворено наживо на реальному стенді до фіксу).
 *
 * ⚠ Мокається САМЕ РЕАЛІСТИЧНА пагінована форма відповіді (об'єкт-обгортка
 * з `items`), а не голий масив — тест, який мокає голий масив, лишився б
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
        <CreateProjectModal opened onClose={() => {}} onCreated={async () => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⛔ Один тест, не декілька: повний рендер цієї форми в jsdom іде понад
// дві хвилини (той самий факт, що вже задокументований поруч у
// `create-project.test.tsx` і є причиною, з якою `createProjectBody`/
// `createProjectIncomplete` винесені в чисті функції). Тут рендер —
// НЕОБХІДНИЙ: дефект Q-274 живе саме в тому, як компонент читає відповідь
// `useQueries`, а це не перевірити чистою функцією. Таймаут піднятий до
// того самого числа, що й `vitest.a11y.config.ts` (400_000 мс) — той самий
// клас вартості, та сама межа.
describe('CreateProjectModal і пагінована відповідь версій шаблону (Q-274)', () => {
  it(
    'не падає й показує опубліковану версію з РЕАЛІСТИЧНОЇ пагінованої відповіді',
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
        '/api/v1/projects/period-policies': [
          { id: 7, code: 'STD', graceOffsetDays: 5, hardCloseOffsetDays: 10 },
        ],
      });

      // ⛔ Головне твердження: до фіксу дані версій приходили як
      // `{items: [...]}", а компонент читав `.data` напряму — щойно запит
      // `template-versions` вирішувався, `.filter` на об'єкті падав
      // `TypeError`, і саме це мало зʼявитися тут (а не «елемент не
      // знайдено за таймаутом» — інша, не та причина).
      show();

      expect(
        await screen.findByText('GEN72571044 · 1.0.0.0', {}, { timeout: 400_000 }),
      ).toBeDefined();
    },
    400_000,
  );
});
