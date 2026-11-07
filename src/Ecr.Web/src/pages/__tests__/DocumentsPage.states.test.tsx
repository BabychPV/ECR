import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Перелік документів: стани «помилка», «завантаження» (`ФВ-14.22`).
 *
 * ⚠ «Порожньо», «фільтр нічого не знайшов» і 403 переліку вже покриті в
 * `features/documents/__tests__/DocumentsPage.filters.test.tsx` — тут не
 * дублюються.
 *
 * ✎ Period clarity (п. e): автовибору періоду (і запиту календарів) більше немає — типовий перегляд РОКУ, тож
 * випадки «відмова календаря» прибрано разом із ним.
 */
configure({ asyncUtilTimeout: 10_000 });

const Find = { timeout: 10_000 };

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };


function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const problem = (status: number, errorCode: string): Response =>
  json({ title: 'Server error', status, errorCode, correlationId: 'corr-docs', detail: null }, status);

interface Replies {
  readonly documents: () => Promise<Response>;
  readonly periods?: () => Promise<Response>;
}

/** Адреси, куди сторінка сходила. */
let requested: string[] = [];

function serve(replies: Replies): void {
  requested = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
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

      // ⚠ Календар — раніше за перелік проєктів: його адреса містить ту як префікс.
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) {
        return replies.periods?.() ?? json({ timeZoneId: 'UTC', policy: null, periods: [] });
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      if (url.includes('/api/v1/documents')) return replies.documents();

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage — стани', () => {
  it('500 переліку: помилка з кодом, а не «документів немає»', async () => {
    serve({ documents: () => Promise.resolve(problem(500, 'ECR-SYS-0500')) });
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false }, Find)).toBeTruthy();
    expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('перелік у дорозі: «завантаження», а не «документів немає»', async () => {
    serve({ documents: () => new Promise<Response>(() => undefined) });
    show();

    expect((await screen.findByRole('status', {}, Find)).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

});
