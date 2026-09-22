import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TemplateColumnUsage } from '@/features/templates/ColumnUsage';
import { testTheme } from '@/test/render';

/**
 * `TemplateColumnUsage` — «де використано» колонки шаблону (ФВ-8.14).
 *
 * ⚠ Перевикористовує `RegistryUsageList`: сам список і його фолбеки тут не
 * тестуються заново (те саме — у `registries/__tests__/RegistryUsage.test.tsx`).
 * Предмет цього файла — що НОВИЙ ендпоінт і НОВА обгортка з'єднані правильно.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'залежності колонки прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-column-usage-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function mockUsage(usage: unknown | 'refuse'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/column-defs/42/usage')) {
        return usage === 'refuse' ? json(Refusal, 500) : json(usage);
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <TemplateColumnUsage columnDefId={42} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateColumnUsage', () => {
  it(
    // ⛔ Мутаційний доказ: якби афорданс ховався при `total: 0`, цей блок був
    // би порожнім. Тут навпаки — список завжди щось малює.
    'total: 0 — «ніде не використано» показано, а не порожньо',
    async () => {
      mockUsage({ total: 0, items: [] });
      show();

      const none = await waitFor(() => {
        const el = document.querySelector('[data-registry-usage="none"]');
        if (el === null) throw new Error('none-блок ще не намальовано');
        return el;
      });

      expect(none.textContent).toContain('registries.usageNone');
    },
  );

  it('успіх із записами — формула шаблону й прив’язка методики у переліку', async () => {
    mockUsage({
      total: 2,
      items: [
        { kind: 'templateFormula', id: '1', label: 'TBL.FORMULA', route: '/admin/templates/1' },
        { kind: 'calculationBinding', id: '2', label: 'BIND.1', route: null },
      ],
    });
    show();

    const list = await waitFor(() => {
      const el = document.querySelector('[data-registry-usage="list"]');
      if (el === null) throw new Error('перелік ще не намальовано');
      return el;
    });

    expect(list.textContent).toContain('registries.usageTotal (total=2)');
    expect(screen.getByText('TBL.FORMULA')).toBeDefined();
    expect(screen.getByText('BIND.1')).toBeDefined();
  });

  it('відмова — причина з кодом, і НЕ «ніде не використано»', async () => {
    mockUsage('refuse');
    show();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('залежності колонки прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByText(/registries\.usageNone/)).toBeNull();
  });
});
