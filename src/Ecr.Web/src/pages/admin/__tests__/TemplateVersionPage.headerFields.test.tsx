import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Поля шапки документа (рівень усього документа, не таблиці) на сторінці
 * версії шаблону — той самий draft→publish контракт, що колонки (`W5.2`), і
 * та сама заморожена структура (`structureFrozenBanner`-подібний прапорець
 * `structure.data?.isEditable`).
 *
 * Мутаційний доказ: «Додати поле» і «Правити» зникають РІВНО тоді, коли
 * структура заморожена (`isEditable: false`) — прибери гейт, і кнопки
 * лишаться на екрані для опублікованої версії, хоча `PUT …/header-fields/{code}`
 * однаково відхилив би запис (`ECR-TMPL-0409`).
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.clone': 'Clone version',
  'version.relations': 'Table relations',
  'version.structureFrozen': 'The structure of a published version is frozen.',
  'periodRules.title': 'Period access rules',
  'headerFields.title': 'Header fields',
  'headerFields.add': 'Add header field',
  'headerFields.edit': 'Edit',
  'headerFields.code': 'Code',
  'headerFields.label': 'Label',
  'headerFields.dataType': 'Data type',
  'enum.dataType.Decimal': 'Number',
  'headerFields.required': 'Required',
  'headerFields.empty': 'No header fields yet.',
  'headerFields.save': 'Save field',
  'common.cancel': 'Cancel',
};

const headerFields = [
  {
    id: 1,
    code: 'Contractor',
    labelL10n: { values: { en: 'Contractor' } },
    ordinal: 0,
    dataType: 'String',
    isRequired: true,
    lookupRegistryDefId: null,
  },
  {
    id: 2,
    code: 'Area',
    labelL10n: { values: { en: 'Area' } },
    ordinal: 1,
    dataType: 'Decimal',
    isRequired: false,
    lookupRegistryDefId: null,
  },
];

function structureDto(isEditable: boolean) {
  return { isEditable, presentationRevision: 0, sheets: [], groupRules: [], templateVersionId: 1 };
}

function versionsPage(status: 'Draft' | 'Published') {
  return {
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
  };
}

function stubFetch(isEditable: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: ['Template.Publish', 'Template.Edit', 'Template.View'],
            isSimulation: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/structure')) {
        return new Response(JSON.stringify(structureDto(isEditable)), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/header-fields')) {
        return new Response(JSON.stringify(headerFields), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/versions?limit=')) {
        return new Response(JSON.stringify(versionsPage(isEditable ? 'Draft' : 'Published')), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
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

describe('TemplateVersionPage: поля шапки документа', () => {
  it('перелік показує код і тип обох полів', async () => {
    stubFetch(true);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(await screen.findByText('(Contractor)')).not.toBeNull();
    expect(screen.getByText('(Area)')).not.toBeNull();
    // ⛔ X-16: тип — підписом каталогу, а не сирим `Decimal`.
    expect(screen.getByText('Number')).not.toBeNull();
    expect(screen.queryByText('Decimal')).toBeNull();
  });

  it('версія-чернетка (isEditable: true) показує «Додати поле» і «Правити»', async () => {
    stubFetch(true);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(await screen.findByText('Add header field')).not.toBeNull();
    expect(screen.getAllByText('Edit').length).toBeGreaterThan(0);
    expect(screen.queryByText('The structure of a published version is frozen.')).toBeNull();
  });

  it('опублікована версія (isEditable: false) ховає «Додати поле» і «Правити», перелік лишається видимим', async () => {
    stubFetch(false);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    await screen.findByText('The structure of a published version is frozen.');

    expect(await screen.findByText('(Contractor)')).not.toBeNull();
    expect(screen.queryByText('Add header field')).toBeNull();
    expect(screen.queryByText('Edit')).toBeNull();
  });

  it('«Правити» відкриває форму, попередньо заповнену полем', async () => {
    stubFetch(true);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    await screen.findByText('(Contractor)');
    fireEvent.click(screen.getAllByText('Edit')[0]!);

    const codeInput = await screen.findByLabelText('Code');
    await waitFor(() => {
      expect((codeInput as HTMLInputElement).value).toBe('Contractor');
    });

    // ⚠ Код наявного поля — незмінна адреса (`draft.isNew === false`): поле
    // вимкнене, той самий патерн, що `columns.code` у `ColumnEditor`.
    expect((codeInput as HTMLInputElement).disabled).toBe(true);
  });
});
