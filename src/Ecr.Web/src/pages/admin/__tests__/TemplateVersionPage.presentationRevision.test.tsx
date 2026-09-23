import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Скрин людини (2026-09-23): поруч із заголовком «Template version 1» стояло
 * самотнє «r0» — сире `presentationRevision` без підпису. Це лічильник правок
 * презентаційного шару (кнопка «Appearance» на колонці, `ФВ-7.2`), і тепер він
 * підписаний тим самим словом, що й кнопка, і тим самим «revision», що й
 * сповіщення після правки (`version.patched`).
 *
 * ⛔ Мутаційний доказ: повернення `r{structure.data.presentationRevision}`
 * робить обидва твердження червоними.
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.presentationRevision': 'Appearance revision {revision}',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function stubFetch(): void {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);

    if (url.includes('/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
    }
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
      return json({ isEditable: true, presentationRevision: 3, groupRules: [], templateVersionId: 1, sheets: [] });
    }
    if (url.includes('/versions?limit=')) {
      return json({ items: [], nextCursor: null, totalCount: null });
    }

    return json([]);
  });
  vi.stubGlobal('fetch', fetchMock);
}

beforeEach(async () => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  stubFetch();
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPage(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
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


describe('TemplateVersionPage — ревізія презентаційного шару в шапці', () => {
  it('показує підписаний лічильник, а не голе «r3»', async () => {
    renderPage();

    const label = await screen.findByText('Appearance revision 3');

    // Стоїть у шапці сторінки, поруч із заголовком версії.
    const heading = screen.getByRole('heading', { name: /Template version 1/ });
    const header = heading.closest('header') ?? heading.parentElement?.parentElement?.parentElement;
    expect(header?.contains(label)).toBe(true);

    expect(screen.queryByText(/^r\d+$/)).toBeNull();
  });
});
