import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * UI-аудит, lane 7: `lane7-publish-withdraw-buttons-not-state-aware.md` —
 * `editable` тут БУВ зашитий у `true` назавжди: «Publish» і «Withdraw from
 * use» лишалися активними ОБИДВІ одночасно, включно з уже опублікованою чи
 * виведеною з обігу версією, хоча сервер (`ECR-TMPL-0409`) однаково відхилив
 * би обидві дії поза їхнім єдиним допустимим станом (`Publish` вимагає
 * `Draft`, `Deprecate` — саме `Published`).
 *
 * Тест доводить: на `Draft`-версії видно РІВНО «Publish», на `Published` —
 * РІВНО «Withdraw from use» — жодна кнопка не показується на обох статусах
 * одночасно (інакше «показувати завжди» пройшло б так само, як і
 * правильний фікс).
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'version.deprecate': 'Withdraw from use',
  'version.clone': 'Clone version',
  'version.relations': 'Table relations',
  'periodRules.title': 'Period access rules',
};

function structureDto(overrides: Partial<{ isEditable: boolean }> = {}) {
  return {
    isEditable: overrides.isEditable ?? false,
    presentationRevision: 0,
    sheets: [],
    templateVersionId: 1,
  };
}

function versionsPage(status: 'Draft' | 'Published' | 'Deprecated') {
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

function stubFetch(status: 'Draft' | 'Published' | 'Deprecated'): void {
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
        return new Response(JSON.stringify(structureDto()), {
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

describe('TemplateVersionPage: Publish/Withdraw відповідають статусу версії (lane7)', () => {
  it('Draft-версія показує «Publish» і НЕ показує «Withdraw from use»', async () => {
    stubFetch('Draft');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(await screen.findByText('Publish')).not.toBeNull();
    expect(screen.queryByText('Withdraw from use')).toBeNull();
  });

  it('Published-версія показує «Withdraw from use» і НЕ показує «Publish»', async () => {
    stubFetch('Published');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    expect(await screen.findByText('Withdraw from use')).not.toBeNull();
    expect(screen.queryByText('Publish')).toBeNull();
  });

  it('Deprecated-версія не показує ні «Publish», ні «Withdraw from use»', async () => {
    stubFetch('Deprecated');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();

    // Дочекатися стабілізації запитів перш ніж стверджувати відсутність:
    // «Clone version» не залежить від статусу версії, тож її поява доводить,
    // що і `session`, і `versionsList` уже прийшли.
    await screen.findByText('Clone version');
    expect(screen.queryByText('Publish')).toBeNull();
    expect(screen.queryByText('Withdraw from use')).toBeNull();
  });
});
