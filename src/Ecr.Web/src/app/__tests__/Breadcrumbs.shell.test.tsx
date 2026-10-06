import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { Breadcrumbs, buildCrumbChain, type CrumbMatch } from '@/app/Breadcrumbs';
import { routes } from '@/app/routes';
import type { DocumentSummary, DocumentTableDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * UI-32: крихти «Group › Page» у верхній смузі на кожному екрані (макет
 * `docs/design/hybrid`, KIT §2.1 `crumbs`: без поля — `group › title`), а на
 * документі — «Documents › DOC-… › аркуш» (`screen-document.js`, `setCrumbs`).
 *
 * ⛔ P1 прихованих аркушів: назва аркуша — лише з того, що повернув сервер.
 */

function client(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

function show(path: string, routePath: string, handle: unknown, queryClient = client()): void {
  const router = createMemoryRouter([{ path: routePath, element: <Breadcrumbs />, handle }], {
    initialEntries: [path],
  });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function table(sheetCode: string, sheetOrdinal: number, name: string): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode,
    sheetDefId: sheetOrdinal,
    sheetNameL10n: { values: { en: name } },
    sheetOrdinal,
    tableCode: `T${sheetCode}`,
    tableDefId: sheetOrdinal,
    tableInstanceId: 100 + sheetOrdinal,
    tableNameL10n: { values: { en: `Table of ${name}` } },
    tableOrdinal: 1,
  };
}

function documentCache(tables: DocumentTableDto[] | undefined, periodKey = 202609): QueryClient {
  const qc = client();
  qc.setQueryData(['document', 5, periodKey], { businessKey: 'DOC-000005' } as DocumentSummary);
  if (tables !== undefined) qc.setQueryData(['document-tables', 5, periodKey], tables);
  return qc;
}

function crumbsText(): string[] {
  const root = screen.getByTestId('breadcrumbs');
  return within(root)
    .getAllByText(/./)
    .map((node) => node.textContent ?? '');
}

describe('UI-32: крихти у верхній смузі — «Group › Page» на кожному екрані', () => {
  it('екран верхнього рівня з меню: група (не посилання) › сторінка (поточна)', () => {
    show('/admin/units', '/admin/units', routes.adminUnits.handle);

    const group = screen.getByTestId('crumb-group');
    expect(group.textContent).toBe('⟦nav.group.configure⟧');
    expect(group.closest('a')).toBeNull();
    expect(screen.getByText('⟦nav.units⟧').getAttribute('aria-current')).toBe('page');
  });

  it('вкладений екран бере групу свого предка: «Configure › Methodologies › версії»', () => {
    show('/admin/methodologies/3/versions', '/admin/methodologies/:id/versions', routes.adminMethodologyVersions.handle);

    expect(crumbsText()).toEqual([
      '⟦nav.group.configure⟧',
      '⟦nav.methodologies⟧',
      '⟦methodologies.versionsTitle⟧',
    ]);
    expect(screen.getByRole('link', { name: '⟦nav.methodologies⟧' }).getAttribute('href')).toBe('/admin/methodologies');
  });

  it('область навігації підписана (макет: nav aria-label="Breadcrumb")', () => {
    show('/admin/units', '/admin/units', routes.adminUnits.handle);

    expect(screen.getByRole('navigation', { name: '⟦nav.breadcrumb⟧' })).toBeTruthy();
  });
});

describe('UI-32: документ — «Documents › DOC-… › аркуш», без групи', () => {
  it('аркуш з адреси, бізнес-ключ веде на той самий період', () => {
    const qc = documentCache([table('A', 1, 'Air emissions'), table('B', 2, 'Water')]);
    show('/documents/5?periodKey=202609&sheet=B', '/documents/:id', routes.documentDetail.handle, qc);

    expect(crumbsText()).toEqual(['⟦nav.documents⟧', 'DOC-000005', 'Water']);
    expect(screen.queryByTestId('crumb-group')).toBeNull();
    expect(screen.getByRole('link', { name: '⟦nav.documents⟧' }).getAttribute('href')).toBe('/');
    expect(screen.getByRole('link', { name: 'DOC-000005' }).getAttribute('href')).toBe('/documents/5?periodKey=202609');
    expect(screen.getByText('Water').getAttribute('aria-current')).toBe('page');
  });

  it('без ?sheet= — перший аркуш за порядком, як на сторінці', () => {
    const qc = documentCache([table('B', 2, 'Water'), table('A', 1, 'Air emissions')]);
    show('/documents/5?periodKey=202609', '/documents/:id', routes.documentDetail.handle, qc);

    expect(crumbsText().at(-1)).toBe('Air emissions');
  });

  it('⛔ P1: код прихованого аркуша в адресі назвою не стає — роль з одним аркушем бачить лише його', () => {
    const qc = documentCache([table('A', 1, 'Air emissions')]);
    show('/documents/5?periodKey=202609&sheet=HIDDEN', '/documents/:id', routes.documentDetail.handle, qc);

    expect(crumbsText()).toEqual(['⟦nav.documents⟧', 'DOC-000005', 'Air emissions']);
    expect(screen.queryByText(/HIDDEN/)).toBeNull();
  });

  it('переліку таблиць ще немає й запиту немає — крихти аркуша немає (не вигадана назва)', () => {
    const qc = documentCache(undefined);
    show('/documents/5?periodKey=202609&sheet=B', '/documents/:id', routes.documentDetail.handle, qc);

    expect(crumbsText()).toEqual(['⟦nav.documents⟧', 'DOC-000005']);
    expect(screen.getByText('DOC-000005').getAttribute('aria-current')).toBe('page');
  });

  it('перелік таблиць іще їде — вузький скелет на місці аркуша', () => {
    const qc = documentCache(undefined);
    void qc.prefetchQuery({ queryKey: ['document-tables', 5, 202609], queryFn: () => new Promise(() => {}) });
    show('/documents/5?periodKey=202609', '/documents/:id', routes.documentDetail.handle, qc);

    expect(screen.getByTestId('crumb-skeleton')).toBeTruthy();
  });

  it('період з адреси має перевагу над іншим закешованим періодом', () => {
    const qc = documentCache([table('A', 1, 'Air emissions')], 202609);
    qc.setQueryData(['document-tables', 5, 202608], [table('A', 1, 'Старий підпис')], { updatedAt: Date.now() + 60_000 });

    const matches: CrumbMatch[] = [
      { pathname: '/documents/5', params: { id: '5' }, handle: routes.documentDetail.handle },
    ];
    const chain = buildCrumbChain(matches, qc, '?periodKey=202609');

    expect(chain.map((entry) => entry.text)).toEqual(['⟦nav.documents⟧', 'DOC-000005', 'Air emissions']);
  });

  it('сусідні ключі під тим самим префіксом (історія, цілі міграції) не підміняють бізнес-ключ', () => {
    const qc = documentCache([table('A', 1, 'Air emissions')]);
    qc.setQueryData(['document', 5, 202609, 'workflow-history'], [], { updatedAt: Date.now() + 60_000 });
    qc.setQueryData(['document', 5, 'migrate-version-targets'], [], { updatedAt: Date.now() + 60_000 });
    show('/documents/5', '/documents/:id', routes.documentDetail.handle, qc);

    expect(crumbsText()).toEqual(['⟦nav.documents⟧', 'DOC-000005', 'Air emissions']);
  });

  it('нуль HTTP-запитів: крихти документа лише читають кеш', () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);
    try {
      const qc = documentCache([table('A', 1, 'Air emissions')]);
      show('/documents/5?periodKey=202609', '/documents/:id', routes.documentDetail.handle, qc);
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
