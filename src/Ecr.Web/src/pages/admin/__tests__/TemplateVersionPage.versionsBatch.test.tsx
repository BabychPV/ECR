import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { NewTemplateVersionModal } from '@/features/templates/NewTemplateVersionModal';

/**
 * Аудит L9-23: публікація, клон і виведення з обігу скидають і ПАКЕТНИЙ перелік версій
 * `/admin/templates` (`queryKeys.templates.versionsBatch`), а не лише `versionsOf`.
 *
 * ⛔ Пакетний ключ навмисно не префікс `versionsOf` (`queryKeys.ts`), тож інвалідація
 * `allVersionsOf()` його не зачіпає. Без явного скидання перелік протягом `staleTime` (30 с)
 * показував старий статус, а «New version» клонувала зі старої «останньої» версії.
 *
 * ⚠ Перевіряємо стан запису кешу (`isInvalidated`), а не повторний запит: на перелік шаблонів
 * у цьому тесті ніхто не підписаний, і інвалідований запис лише позначається застарілим.
 */

const SeededStrings: Record<string, string> = {
  'version.publish': 'Publish',
  'version.deprecate': 'Withdraw from use',
  'version.clone': 'Clone version',
  'templates.versionNumber': 'Version number',
  'workflow.reason': 'Reason',
  'common.save': 'Save',
};

type Status = 'Draft' | 'Published';

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubFetch(status: Status): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.Publish', 'Template.Edit', 'Template.View'],
          isSimulation: false,
        });
      }
      if (method === 'POST' && (url.endsWith('/clone') || url.endsWith('/versions'))) return json({ versionId: 2 });
      if (method === 'POST') return json({});
      if (url.includes('/structure')) {
        return json({ isEditable: false, presentationRevision: 0, sheets: [], templateVersionId: 1 });
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

beforeEach(async () => {
  vi.stubGlobal('ResizeObserver', class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function renderPage(status: Status): Promise<QueryClient> {
  stubFetch(status);
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // Перелік `/admin/templates` уже відвідано: пакет версій лежить у кеші.
  client.setQueryData(queryKeys.templates.versionsBatch([1, 5]), [{ templateId: 1, versions: [] }]);

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
            <Route path="/admin/templates/:id/versions/2" element={<p>clone opened</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

function batchInvalidated(client: QueryClient): boolean {
  return client.getQueryState(queryKeys.templates.versionsBatch([1, 5]))?.isInvalidated === true;
}

describe('TemplateVersionPage: L9-23 — пакетний перелік версій скидається', () => {
  it('після публікації', async () => {
    const client = await renderPage('Draft');

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'квартальний випуск' } });
    expect(batchInvalidated(client)).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(batchInvalidated(client)).toBe(true));
  });

  it('після виведення з обігу', async () => {
    const client = await renderPage('Published');

    fireEvent.click(await screen.findByRole('button', { name: 'Withdraw from use' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'замінено новою' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Withdraw from use' }));

    await waitFor(() => expect(batchInvalidated(client)).toBe(true));
  });

  it('після клону', async () => {
    const client = await renderPage('Published');

    fireEvent.click(await screen.findByRole('button', { name: 'Clone version' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: '1.1.0.0' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Clone version' }));

    await waitFor(() => expect(batchInvalidated(client)).toBe(true));
  });
});

describe('NewTemplateVersionModal: L9-23 — нова версія скидає пакетний перелік', () => {
  it('після створення версії з картки шаблону', async () => {
    stubFetch('Published');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(queryKeys.templates.versionsBatch([1, 5]), [{ templateId: 1, versions: [] }]);

    render(
      <MantineProvider theme={theme}>
        <QueryClientProvider client={client}>
          <NewTemplateVersionModal templateId={1} cloneFrom={1} onClose={() => undefined} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: '2.0.0.0' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(batchInvalidated(client)).toBe(true));
  });
});
