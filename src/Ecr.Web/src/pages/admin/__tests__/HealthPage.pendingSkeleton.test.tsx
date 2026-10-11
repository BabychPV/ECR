import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * RC19: перше відкриття Health — звіт `/health/ready` збирає всі перевірки й може йти 12–16 с.
 * Сторінка не мусить здаватися зависанням: шапка стоїть одразу, а замість одного рядка «Loading…»
 * малюються скелети секцій (за формою справжніх) із поясненням.
 */
afterEach(() => vi.unstubAllGlobals());

function show(): void {
  // Запити не відповідають ніколи — сторінка лишається в стані очікування.
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('HealthPage: очікування першого звіту', () => {
  it('шапка видна одразу, а секції — скелетом із поясненням, не голим «Loading»', () => {
    show();

    expect(screen.getByRole('heading', { level: 1 })).toBeTruthy();
    const skeleton = screen.getByTestId('health-checks-skeleton');
    expect(skeleton.getAttribute('aria-busy')).toBe('true');
    expect(skeleton.textContent).toContain('⟦health.checking⟧');
    // Пустий стан «жодної перевірки» не малюється поруч зі скелетом (аудит P1-3).
    expect(screen.queryByText('⟦health.noChecks⟧')).toBeNull();
  });
});
