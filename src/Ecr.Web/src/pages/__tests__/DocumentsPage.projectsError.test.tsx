import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10.
 *
 * ⛔ `projectCodeOf` повертав `…?.code ?? String(projectId)`, тож відмова
 * `GET /api/v1/projects` повертала колонку «Проєкт» рівно в той стан, який
 * прибрав аудит-пас 5 (сусідній файл `DocumentsPage.projectCode.test.tsx`):
 * голий числовий ідентифікатор у переліку на десятки рядків. Оператор
 * відкриває не той документ, і ніщо не каже, що підпис просто не прочитався.
 *
 * ⚠ Число з екрана не ховається — без коду воно єдине, що лишається від
 * адреси документа. Предмет перевірки — ФОРМА показу (`<code>`, тобто «це
 * ідентифікатор») і видима причина.
 */

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

const document_ = {
  id: 1,
  businessKey: 'P1-V1-0001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
};

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік проєктів прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-projects-list-1',
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

function mockFetch(projectsFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/documents')) {
        return json({ items: [document_], nextCursor: null, totalCount: 1 });
      }

      if (path.endsWith('/api/v1/projects')) {
        return projectsFail
          ? json(Refusal, 500)
          : json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/']}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 60_000;

describe('DocumentsPage: відмова переліку проєктів ≠ «проєкт називається 1»', () => {
  it(
    'проєкти не приїхали — причина з кодом, а ідентифікатор показано ЯК ідентифікатор',
    async () => {
      mockFetch(true);
      show();

      const alert = await waitFor(() => screen.getByRole('alert'), { timeout: 10_000 });

      expect(alert.textContent ?? '').toContain('перелік проєктів прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      /*
       * ⚠ Саме КОМІРКА проєкту, а не пошук тексту «1» по таблиці: одиниць у
       * рядку кілька (кількість аркушів, кількість помилок), і `getByText('1')`
       * падав би з «Found multiple elements» — або, що гірше, знаходив би не ту.
       */
      const row = within(table).getByRole('row', { name: /P1-V1-0001/ });
      const projectCell = within(row).getAllByRole('cell')[1];

      expect(projectCell?.querySelector('code')?.textContent).toBe('1');
    },
    SlowEnvTimeout,
  );

  it(
    'проєкти приїхали — код проєкту на місці, банера немає',
    async () => {
      mockFetch(false);
      show();

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      // ⚠ Дзеркало: спершу дочекатися самого коду — інакше твердження про
      // відсутність банера зелене на будь-якому коді.
      await within(table).findByText('AUDIT_SMOKE_PRJ', {}, { timeout: SlowEnvTimeout });

      expect(screen.queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
