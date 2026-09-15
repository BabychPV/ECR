import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Аудит-пас 8, lane7, п.11: опублікована версія коректно не дає редагувати
 * структуру, але ЄДИНЕ пояснення («Clone version» заморожує структуру…)
 * жило ЛИШЕ всередині діалогу клонування, який треба здогадатися відкрити.
 * Мітка `· Fixed` біля таблиці — про ІНШЕ (спосіб формування рядків, не стан
 * заморозки) і оманливо виглядала як пояснення.
 *
 * ⛔ Мутаційний доказ: банер зʼявляється РІВНО тоді, коли
 * `structure.data?.isEditable === false` (і є право `Template.Edit` —
 * банер про можливість редагування нічого не каже тому, хто редагувати не
 * може). Відкат до «банера немає взагалі» чи до «банер завжди видно»
 * зробить один із двох тестів нижче червоним.
 */
const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'version.deprecate': 'Withdraw from use',
  'version.clone': 'Clone version',
  'version.relations': 'Table relations',
  'periodRules.title': 'Period access rules',
  'version.structureFrozen':
    'This published version is frozen: structural changes go through "Clone version".',
};

function structureDto(isEditable: boolean) {
  return {
    isEditable,
    presentationRevision: 0,
    sheets: [],
    templateVersionId: 1,
  };
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

function stubFetch(status: 'Draft' | 'Published', isEditable: boolean): void {
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
            permissions: ['Template.Publish', 'Template.Edit'],
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
      if (url.includes('/versions?limit=')) {
        return new Response(JSON.stringify(versionsPage(status)), {
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

describe('TemplateVersionPage: банер про заморожену структуру (аудит-пас 8, lane7, п.11)', () => {
  it('Published, isEditable=false — банер про заморожену структуру видно НА сторінці', async () => {
    stubFetch('Published', false);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(
      await screen.findByText(
        'This published version is frozen: structural changes go through "Clone version".',
      ),
    ).not.toBeNull();
  });

  it('Draft, isEditable=true — банера немає: структуру можна редагувати напряму', async () => {
    stubFetch('Draft', true);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    // Дочекатися стабілізації запитів перш ніж стверджувати відсутність.
    await screen.findByText('Publish');
    expect(
      screen.queryByText(
        'This published version is frozen: structural changes go through "Clone version".',
      ),
    ).toBeNull();
  });
});
