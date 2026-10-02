import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { UsageResponse } from '@/features/registries/api';
import { RegistryUsageList } from '@/features/registries/RegistryUsage';
import { testTheme } from '@/test/render';

/**
 * ФВ-8.14 (B5.4): у переліку «де використовується» основний підпис — читабельна
 * назва (`name`), код (`label`) — другорядний; без назви лишається код, як було.
 */
function show(usage: UsageResponse): HTMLElement {
  const { container } = render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <RegistryUsageList usage={usage} />
      </MemoryRouter>
    </MantineProvider>,
  );
  return container;
}

describe('RegistryUsageList: назва замість коду', () => {
  it('з name — посилання підписане назвою, код показано поруч', () => {
    const container = show({
      total: 1,
      items: [
        {
          kind: 'templateColumn',
          id: '7',
          label: 'TB1.CL1',
          name: 'Обсяг викидів',
          route: '/admin/templates/1/versions/2',
        },
      ],
    });

    const link = screen.getByRole('link', { name: 'Обсяг викидів' });
    expect(link.getAttribute('href')).toBe('/admin/templates/1/versions/2');
    expect(container.querySelector('[data-registry-usage="code"]')?.textContent).toBe('TB1.CL1');
  });

  it('без name — лишається код, дубль не малюється', () => {
    const container = show({
      total: 1,
      items: [{ kind: 'sourceEntity', id: '3', label: 'ENT_A', name: null, route: null }],
    });

    expect(screen.getByText('ENT_A')).toBeTruthy();
    expect(container.querySelector('[data-registry-usage="code"]')).toBeNull();
  });
});
