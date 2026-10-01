import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { RegistryEntryDto } from '@/api/types';
import { testTheme } from '@/test/render';
import EntryUsageModal, { EntryUsageReportView } from '../EntryUsageModal';
import type { EntryUsageReport } from '../entryUsage';

/**
 * Діалог «Де використовується» запису (ФВ-8.14).
 *
 * ⚠ Каталог рядків не завантажений: підписи — ключі в `⟦…⟧` з параметрами.
 */

function wrap(node: ReactNode): ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{node}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

const Entry: RegistryEntryDto = {
  id: 42,
  code: 'NOX',
  display: 'Nitrogen oxides',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
};

const Empty: EntryUsageReport = {
  fields: [],
  children: [],
  substances: [],
  columns: [],
  dataInDocuments: false,
  truncated: false,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EntryUsageReportView', () => {
  it('записи-посилання ведуть на перелік свого довідника з пошуком за кодом', () => {
    render(
      wrap(
        <EntryUsageReportView
          registryCode="SUBST"
          asOf="2026-09-30"
          report={{
            ...Empty,
            fields: [
              {
                field: { registryCode: 'PERMIT', fieldCode: 'SUBSTANCE', route: '/admin/registries/PERMIT/definition' },
                status: 'ok',
                rows: [{ id: 7, code: 'P-1', display: 'Permit 1', parentEntryId: null, validFrom: null, validTo: null, version: 'v', values: {} }],
                total: 3,
              },
            ],
          }}
        />,
      ),
    );

    const link = screen.getByRole('link', { name: 'P-1' });
    expect(link.getAttribute('href')).toBe('/admin/registries?code=PERMIT&q=P-1');
    // Сторінка з 1 рядка із 3 — чесне «показано N із M».
    expect(document.querySelector('[data-entry-usage="field-truncated"]')?.textContent).toContain('shown=1');
    expect(document.querySelector('[data-entry-usage="none-named"]')).toBeNull();
  });

  it('без поіменних посилань НЕ каже «не використано», а називає непереглянуті види', () => {
    render(wrap(<EntryUsageReportView registryCode="SUBST" asOf="2026-09-30" report={Empty} />));

    expect(document.querySelector('[data-entry-usage="none-named"]')?.textContent).toContain(
      'registries.entryUsage.noneNamed',
    );
    expect(document.querySelector('[data-entry-usage="not-listed"]')?.textContent).toContain(
      'registries.entryUsage.notListed',
    );
    expect(screen.queryByText(/registries\.usageNone/)).toBeNull();
  });

  it('відмова одного поля показується помилкою, а не порожньою групою', () => {
    render(
      wrap(
        <EntryUsageReportView
          registryCode="SUBST"
          asOf="2026-09-30"
          report={{
            ...Empty,
            fields: [
              {
                field: { registryCode: 'HIDDEN', fieldCode: 'F', route: null },
                status: 'error',
                error: new Error('refused'),
              },
            ],
          }}
        />,
      ),
    );

    const group = document.querySelector('[data-entry-usage-field="HIDDEN.F"]');
    expect(group?.querySelector('[role="alert"]')).not.toBeNull();
    expect(group?.textContent).not.toContain('registries.entryUsage.none');
  });

  it('обрізаний перелік довідника позначений', () => {
    render(wrap(<EntryUsageReportView registryCode="SUBST" asOf="2026-09-30" report={{ ...Empty, truncated: true }} />));

    expect(document.querySelector('[data-entry-usage="truncated"]')).not.toBeNull();
  });
});

describe('EntryUsageModal', () => {
  it('відмова звіту довідника — помилка з «повторити», а не порожній звіт', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              type: 'about:blank',
              title: 'Forbidden',
              status: 403,
              errorCode: 'ECR-AUTH-0403',
              messageKey: 'err.ECR-AUTH-0403.forbidden',
            }),
            { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    );

    render(wrap(<EntryUsageModal registryCode="SUBST" entry={Entry} siblings={[]} onClose={() => {}} />));

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
    expect(document.querySelector('[data-entry-usage="report"]')).toBeNull();
  });
});
