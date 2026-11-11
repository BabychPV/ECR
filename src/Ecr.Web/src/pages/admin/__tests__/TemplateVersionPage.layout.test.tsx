import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * ✎ D-474, п. 2: сторінка версії показує «Layout» зі СТРУКТУРИ версії (`TemplateStructureDto.layout`):
 * чернетка (`isEditable`, право `Template.Edit`) — список, опублікована — значок лише для читання.
 *
 * ⛔ Мутаційний доказ: прибрати `TemplateLayoutControl` зі сторінки — обидва тести червоніють; передати
 * `editable={true}` завжди — червоніє тест опублікованої версії.
 */
const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'template.layout.label': 'Layout',
  'template.layout.perMonthCopies': 'A copy per month',
  'template.layout.yearWithMonthColumns': 'Year, months in columns',
  'template.layout.hint': 'The layout defines which period a cell belongs to. It can be changed only in a draft.',
};

function stubFetch(status: 'Draft' | 'Published', isEditable: boolean, layout: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
          isSimulation: false,
        });
      }
      if (url.includes('/structure')) {
        return json({ isEditable, presentationRevision: 0, sheets: [], templateVersionId: 1, layout });
      }
      if (url.includes('/versions?limit=')) {
        return json({
          items: [
            {
              id: 1,
              version: '1.0.0.0',
              status,
              presentationRevision: 0,
              clonedFromVersionId: null,
              publishedAt: status === 'Draft' ? null : '2026-09-14T00:00:00',
              layout,
            },
          ],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('TemplateVersionPage: розкладка версії (D-474)', () => {
  it('чернетка — список «Layout» зі значенням зі структури', async () => {
    stubFetch('Draft', true, 'YearWithMonthColumns');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    const field = (await screen.findByRole('textbox', { name: /Layout/ }, { timeout: 30_000 })) as HTMLInputElement;
    expect(field.value).toBe('Year, months in columns');
  });

  it('опублікована — значок лише для читання, списку немає', async () => {
    stubFetch('Published', false, 'PerMonthCopies');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(await screen.findByText('A copy per month', {}, { timeout: 30_000 })).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: /Layout/ })).toBeNull();
  });
});
