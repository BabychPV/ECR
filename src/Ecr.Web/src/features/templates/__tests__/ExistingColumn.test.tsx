import { useState, type JSX } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { testTheme } from '@/test/render';
import { ExistingColumn } from '../ExistingColumn';
import type { ColumnDraft } from '../column';

/**
 * Аудит L9-26: відмова фонового перечитування колонки не підміняє форму правки.
 *
 * ⛔ Доти `column.error !== null` повертав `ErrorAlert` ЗАМІСТЬ форми навіть тоді, коли дані вже
 * були: форма розмонтовувалась, а з нею й локальна чернетка (`LocalDraft`) — незбережені правки
 * колонки зникали від невдалого фокус-перезапиту.
 */
const api = vi.hoisted(() => ({ getColumn: vi.fn() }));
vi.mock('../columnApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../columnApi')>()),
  ...api,
}));

const column = {
  code: 'Q',
  headerL10n: { values: { en: 'Quantity' } },
  ordinal: 1,
  dataType: 'Decimal',
  isRequired: false,
  isReadOnly: false,
  isHidden: false,
  precision: 18,
  scale: 2,
  defaultValue: null,
  displayFormat: null,
  lookupRegistryDefId: null,
  lookupFilter: null,
  unitId: null,
  widthPx: null,
  styleId: null,
};

/** Форма з власною чернеткою — як `LocalDraft`: стан живе, лише поки форма змонтована. */
function Form({ draft }: { readonly draft: ColumnDraft }): JSX.Element {
  const [header, setHeader] = useState(draft.headerL10n['en'] ?? '');

  return <input aria-label="header" value={header} onChange={(event) => setHeader(event.currentTarget.value)} />;
}

describe('ExistingColumn: L9-26', () => {
  it('перша відповідь — відмова: ErrorAlert замість форми', async () => {
    api.getColumn.mockReset();
    api.getColumn.mockRejectedValue(new EcrApiError({ title: 't', status: 503, errorCode: 'ECR-SYS-0503', correlationId: 'c' }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <ExistingColumn templateVersionId={1} tableId={2} code="Q">
            {(draft) => <Form draft={draft} />}
          </ExistingColumn>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(screen.getByText(/ECR-SYS-0503/)).toBeDefined());
    expect(screen.queryByLabelText('header')).toBeNull();
  });

  it('відмова перечитування — ErrorAlert поруч, форма з чернеткою лишається', async () => {
    api.getColumn.mockReset();
    api.getColumn.mockResolvedValueOnce(column);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <ExistingColumn templateVersionId={1} tableId={2} code="Q">
            {(draft) => <Form draft={draft} />}
          </ExistingColumn>
        </QueryClientProvider>
      </MantineProvider>,
    );

    const header = (await screen.findByLabelText('header')) as HTMLInputElement;
    fireEvent.change(header, { target: { value: 'Кількість (чернетка)' } });

    api.getColumn.mockRejectedValue(new EcrApiError({ title: 't', status: 503, errorCode: 'ECR-SYS-0503', correlationId: 'c' }));
    await act(async () => {
      await client.refetchQueries();
    });

    await waitFor(() => expect(screen.getByText(/ECR-SYS-0503/)).toBeDefined());
    expect((screen.getByLabelText('header') as HTMLInputElement).value).toBe('Кількість (чернетка)');
  });
});
