import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { HealthPage, checkLabel } from '@/pages/admin/HealthPage';

/**
 * `U-14`: картки стану були названі іменами реєстрації перевірок — `db`,
 * `jobs`, `sources` (`Program.cs`), маленькими літерами, над цілком людським
 * реченням під ними («Database is available.»).
 *
 * ⚠ Другий випадок так само обов'язковий: набір перевірок задає СЕРВЕР, і
 * четверта з'явиться у звіті раніше, ніж рядок під неї в `09-seed.sql`. Тоді
 * на картці має лишитися її ім'я, а не `⟦health.check.smtp⟧`.
 */

const SeededStrings: Record<string, string> = {
  'health.title': 'Health',
  'health.check.db': 'Database',
  'health.check.jobs': 'Background jobs',
  'health.check.sources': 'Collection sources',
};

const report = {
  status: 'Healthy',
  totalDurationMs: 2,
  checks: [
    { name: 'db', status: 'Healthy', description: 'Database is available.', durationMs: 1, data: {} },
    { name: 'jobs', status: 'Healthy', description: 'The scheduler is running.', durationMs: 1, data: {} },
    { name: 'sources', status: 'Healthy', description: 'No active collection sources.', durationMs: 1, data: {} },
    // ⛔ Перевірка, якої в каталозі рядків немає.
    { name: 'smtp', status: 'Healthy', description: 'Mail transport is configured.', durationMs: 1, data: {} },
  ],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

beforeEach(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      return json(report);
    }),
  );

  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('Картки стану названі людською мовою (U-14)', () => {
  it('заголовок картки — рядок каталогу, а не ідентифікатор перевірки', async () => {
    show();

    // ⛔ Це й ламається поверненням `{check.name}` у заголовок картки.
    expect(await screen.findByText('Database')).not.toBeNull();
    expect(await screen.findByText('Background jobs')).not.toBeNull();
    expect(await screen.findByText('Collection sources')).not.toBeNull();

    // ⚠ І самих ідентифікаторів на екрані більше немає — інакше «назва
    // з'явилася» означало б лише «додали ще один рядок поруч із кодом».
    expect(screen.queryByText('db')).toBeNull();
    expect(screen.queryByText('jobs')).toBeNull();
    expect(screen.queryByText('sources')).toBeNull();
  });

  it('НЕВІДОМА перевірка лишається під власним іменем, а не під позначеним ключем', async () => {
    show();

    expect(await screen.findByText('smtp')).not.toBeNull();
    expect(screen.queryByText('⟦health.check.smtp⟧')).toBeNull();
  });

  it('запасний варіант — сам ідентифікатор, і він не містить позначки', () => {
    expect(checkLabel('db')).toBe('Database');
    expect(checkLabel('smtp')).toBe('smtp');
    expect(checkLabel('smtp')).not.toContain('⟦');
  });
});
