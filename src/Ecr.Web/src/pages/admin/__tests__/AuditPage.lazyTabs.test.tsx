import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

/**
 * Вкладка «Structure» лінивим чанком (`AuditPage`, D-132): «Export» і журнал структурних змін — під ОДНІЄЮ межею
 * `Suspense`.
 *
 * ⛔ Предмет — розмітка, поки чанк журналу в дорозі: кнопка «Export» (її чанк уже приїхав) не має з'являтися сама над
 * порожнім місцем і потім зсуватися журналом, що приїде слідом. Обидва з'являються разом, коли приїхав журнал.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`: сторінка сама передзавантажує
 * чанки вкладок (`preloadAuditTabs`), і другий `import()` того самого модуля, поки async-фабрика ще працює, vitest
 * віддавав би справжнім модулем (див. `VersionDiffButton.lateChunk.test.tsx`).
 *
 * Мутаційний доказ: винести `<StructureExportButton>` з-під спільної межі у власний `Suspense` — тест червоний
 * («Export» видно до приїзду журналу).
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release(), loaded: false as boolean };
});

vi.mock('@/features/audit/StructureChangesPanel', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/features/audit/StructureChangesPanel')>();
  function LatePanel(props: Parameters<typeof actual.StructureChangesPanel>[0]): ReturnType<typeof actual.StructureChangesPanel> {
    use(chunk.ready);

    return createElement(actual.StructureChangesPanel, props);
  }

  chunk.loaded = true;

  return { ...actual, StructureChangesPanel: LatePanel };
});

const Page = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      changedByUserId: 41,
      changeReason: null,
      entityId: 3,
      entityType: 'cfg.RegistryDef',
      newJson: null,
      oldJson: null,
      operation: 'SaveRules',
    },
  ],
  nextCursor: null,
  totalCount: null,
};

const SlowEnvTimeout = 60_000;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AuditPage: вкладка «Structure» лінивим чанком', () => {
  it(
    '«Export» і журнал з’являються разом, коли приїхав чанк журналу',
    async () => {
      let meAnswered = false;
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL) => {
          const url = String(input);
          const json = (body: unknown): Response =>
            new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

          if (url.endsWith('/api/v1/me')) {
            meAnswered = true;

            return json({
              userId: 1,
              userName: 'auditor',
              language: 'en',
              permissions: ['Security.ViewAudit'],
              isSimulation: false,
              denies: [],
              grants: {},
              mustChangePassword: false,
              simulatedForUserId: null,
            });
          }

          return json(url.includes('/api/v1/audit/') ? Page : null);
        }),
      );

      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={['/admin/audit?view=structure&from=2026-01-01&to=2026-01-08']}>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <AuditPage />
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      // Сесія відповіла (без неї «Export» не малюється взагалі — перевірка нижче була б порожньою), фабрика доробила.
      await waitFor(() => expect(meAnswered).toBe(true), { timeout: SlowEnvTimeout });
      await waitFor(() => expect(chunk.loaded).toBe(true), { timeout: SlowEnvTimeout });
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
      });

      // Чанк журналу «в дорозі»: ні журналу, ні самотньої кнопки «Export».
      expect(screen.queryByText('Save rules')).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦audit.exportCsv⟧' })).toBeNull();

      chunk.open();
      await screen.findByText('Save rules', {}, { timeout: SlowEnvTimeout });
      expect(screen.getByRole('button', { name: '⟦audit.exportCsv⟧' })).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
