import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * Дашборд здоров'я читає ту саму відповідь, яку віддає сервер.
 *
 * ⛔ Зразок — **спільний**: `tests/Ecr.TestKit/Fixtures/health-response.json`
 * читає і серверний тест `Форма_звіту_збігається_зі_спільним_зразком`, і цей.
 * Власний зразок на клієнті нічого не доводив би: саме розбіжність між тим,
 * що клієнт вважає відповіддю, і тим, що сервер надсилає, і є `A7-04`.
 *
 * Дефект: клієнт читав `entries` словником, сервер писав `checks` масивом.
 * `Object.entries(undefined ?? {})` не падає — дашборд відкривався порожнім і
 * виглядав рівно як здорова система без зареєстрованих перевірок.
 */
const sample = JSON.parse(
  readFileSync(
    path.resolve(process.cwd(), '../../tests/Ecr.TestKit/Fixtures/health-response.json'),
    'utf8',
  ),
) as { status: string; checks: { name: string; status: string; description: string }[] };

function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Дашборд здоров’я', () => {
  it('показує кожну перевірку зі справжньої відповіді сервера', async () => {
    respond(sample);
    show();

    for (const check of sample.checks) {
      expect(await screen.findByText(check.name)).toBeDefined();
      expect(await screen.findByText(check.description)).toBeDefined();
    }
  });

  it('показує подробиці бази з перевірки «db»', async () => {
    respond(sample);
    show();

    // Саме те, заради чого `/health/db` існує (АРХ-7 п. 5).
    expect(await screen.findAllByText('effectiveMode')).not.toHaveLength(0);
    expect(await screen.findAllByText('Enterprise')).not.toHaveLength(0);
  });

  it('невдалий запит НЕ виглядає як порожній дашборд (A7-04)', async () => {
    respond(
      {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-health-1',
      },
      503,
    );
    show();

    // ⛔ Головне твердження. До виправлення тут була порожня сітка карток —
    // тобто екран, який неможливо відрізнити від здорової системи.
    const alerts = await screen.findAllByRole('alert');
    expect(alerts.length).toBeGreaterThan(0);
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-SYS-0503');
  });

  it('порожній перелік перевірок пояснюється, а не мовчить', async () => {
    respond({ status: 'Healthy', totalDurationMs: 1, checks: [] });
    show();

    expect(await screen.findByText('health.noChecks')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
