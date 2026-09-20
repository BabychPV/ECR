import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10.
 *
 * ⛔ Переліки «таблиця-джерело» і «таблиця-ціль» збиралися як
 * `tableOptions(structure.data)`, тож відмова `GET …/structure` робила обидва
 * ПОРОЖНІМИ. Читається це однозначно й хибно — «у цій версії таблиць немає» —
 * і адміністратор іде перевіряти структуру версії замість повторити запит.
 *
 * ⚠ Межа `AsyncBoundary` на цій сторінці стосується ЗВ'ЯЗКІВ (`relations`), а
 * не структури: відмову структури не показувало ніде.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'структуру версії прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-structure-1',
  // ⚠ Без ознаки подробиця до екрана не доходить (рішення про мову).
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(structureFails: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      // ⚠ `/api/v1/me` — ТОЧНО: `includes` збігся б і з іншими маршрутами.
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Template.Edit', 'Template.View'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/structure')) {
        return structureFails
          ? json(Refusal, 500)
          : json({
              templateVersionId: 5,
              isEditable: true,
              presentationRevision: 0,
              sheets: [
                {
                  id: 50,
                  code: 'S1',
                  ordinal: 0,
                  titleL10n: { values: { en: 'Sheet 1' } },
                  tables: [
                    {
                      id: 500,
                      code: 'T1',
                      ordinal: 0,
                      rowMode: 'Fixed',
                      titleL10n: { values: { en: 'Table 1' } },
                      columns: [],
                    },
                  ],
                },
              ],
            });
      }

      if (path.endsWith('/relations')) {
        return json({ isEditable: true, relations: [] });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/5/relations']}>
          <Routes>
            <Route
              path="/admin/templates/:id/versions/:versionId/relations"
              element={<TableRelationsPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Відкриває форму зв'язку — саме в ній живуть обидва переліки таблиць. */
async function openForm(): Promise<void> {
  fireEvent.click(await screen.findByText(/tables\.newRelation/));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TableRelationsPage: відмова структури ≠ «таблиць у версії немає»', () => {
  it('структура не приїхала — причина з кодом у формі, а порожніх переліків таблиць НЕМАЄ', async () => {
    mockServer(true);
    show();
    await openForm();

    /*
     * ⚠ Банер шукаємо за КОДОМ відмови, а не за роллю: на цій сторінці вже
     * стоять два постійні `Alert` (підказка про необов'язковість механізму і,
     * за станом, «лише читання»), а Mantine `Alert` теж має `role="alert"`.
     */
    const alert = await waitFor(
      () => {
        const found = screen
          .getAllByRole('alert')
          .find((node) => (node.textContent ?? '').includes('ECR-SYS-0500'));

        if (found === undefined) throw new Error('банера відмови структури немає');

        return found;
      },
      { timeout: 10_000 },
    );

    expect(alert.textContent ?? '').toContain('структуру версії прочитати не вдалося');

    // ⛔ Головне: форма з порожніми переліками не показується взагалі — вона
    // обіцяла б вибір, якого немає.
    expect(screen.queryByLabelText(/tables\.sourceTable/)).toBeNull();
  }, 30_000);

  it('структура приїхала — переліки таблиць на місці й доступні, банера відмови немає', async () => {
    mockServer(false);
    show();
    await openForm();

    // ⚠ Дзеркало: спершу дочекатися ДОСТУПНОГО поля — доки структура в дорозі,
    // воно навмисно заблоковане, і твердження нижче було б зеленим на будь-якому коді.
    await waitFor(
      () => {
        const select = screen.getByLabelText(/tables\.sourceTable/);
        expect((select as HTMLSelectElement).disabled).toBe(false);
      },
      { timeout: 10_000 },
    );

    expect(
      screen.queryAllByRole('alert').some((node) => (node.textContent ?? '').includes('ECR-SYS-0500')),
    ).toBe(false);
  }, 30_000);
});
