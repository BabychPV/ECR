import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Афорданс «де використано» на рядку колонки в `TemplateVersionPage`
 * (ФВ-8.14) — конструктор шаблону.
 *
 * ⛔ Мутаційний доказ, той самий, що й для константи методики
 * (`features/methodologies/__tests__/ConstantUsageButton.test.tsx`): кнопка
 * на рядку колонки має бути завжди (право перегляду використання —
 * `Template.View`, окреме від права редагування), і модалка при
 * `total: 0` показує «ніде не використано», а не порожньо.
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'version.column': 'Column',
  'version.type': 'Type',
  'version.unit': 'Unit',
};

function structureDto(): unknown {
  return {
    isEditable: true,
    presentationRevision: 0,
    groupRules: [],
    templateVersionId: 1,
    sheets: [
      {
        id: 1,
        code: 'SHEET',
        nameL10n: { values: { en: 'Sheet' } },
        isMandatory: true,
        isVisible: true,
        ordinal: 1,
        sheetGroup: null,
        tables: [
          {
            id: 10,
            code: 'TBL',
            nameL10n: { values: { en: 'Table' } },
            layoutKind: 'Static',
            maxDynamicRows: null,
            ordinal: 1,
            rowMode: 'Fixed',
            rows: [],
            columns: [
              {
                id: 42,
                code: 'COL',
                dataType: 'Decimal',
                displayFormat: null,
                headerL10n: { values: { en: 'Column' } },
                isHidden: false,
                isReadOnly: false,
                isRequired: false,
                ordinal: 1,
                unitSymbol: null,
              },
            ],
          },
        ],
      },
    ],
  };
}

function versionsPage(): unknown {
  return {
    items: [
      {
        id: 1,
        version: '1.0.0.0',
        status: 'Draft',
        presentationRevision: 0,
        clonedFromVersionId: null,
        publishedAt: null,
      },
    ],
    nextCursor: null,
    totalCount: null,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function stubFetch(usageTotal: number): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.Publish', 'Template.Edit'],
          isSimulation: false,
        });
      }
      if (url.includes('/column-defs/42/usage')) {
        return json({
          total: usageTotal,
          items: usageTotal === 0 ? [] : [{ kind: 'templateFormula', id: '1', label: 'F', route: null }],
        });
      }
      if (url.includes('/structure')) {
        return json(structureDto());
      }
      if (url.includes('/versions?limit=')) {
        return json(versionsPage());
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

/** Аркуш згорнутий за замовчуванням (Mantine `Accordion`) — розгорнути. */
async function openSheet(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
}

function renderPage(): ReturnType<typeof render> {
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

describe('TemplateVersionPage: афорданс «де використано» колонки (ФВ-8.14)', () => {
  it('кнопка на рядку колонки є', async () => {
    stubFetch(0);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    expect(await screen.findByRole('button', { name: /registries\.tabUsage/ })).toBeDefined();
  });

  it('total: 0 — модалка все одно показує «ніде не використано», не порожньо', async () => {
    stubFetch(0);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    fireEvent.click(await screen.findByRole('button', { name: /registries\.tabUsage/ }));

    const dialog = await screen.findByRole('dialog');

    await waitFor(() => {
      expect(within(dialog).getByText(/registries\.usageNone/)).toBeDefined();
    });
  });

  it('успіх із записом — перелік у модалці', async () => {
    stubFetch(1);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    fireEvent.click(await screen.findByRole('button', { name: /registries\.tabUsage/ }));

    const dialog = await screen.findByRole('dialog');

    await waitFor(() => {
      expect(within(dialog).getByText('F')).toBeDefined();
    });
  });
});
