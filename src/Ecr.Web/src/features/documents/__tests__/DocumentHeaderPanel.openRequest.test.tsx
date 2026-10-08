import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { testTheme } from '@/test/render';

/**
 * Клік по знахідці групи «Document header» в Issues збільшує `openRequest`: секція шапки розгортається,
 * а після ручного згортання наступний клік розгортає її знову.
 *
 * ⛔ Мутаційний доказ: прибери `setOpened(true)` з ефекту `openRequest` - обидва тести почервоніють.
 */
const DocumentId = 41;

function mockHeader(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`)) {
        return new Response(
          JSON.stringify({
            fields: [
              {
                code: 'OPERATOR',
                dataType: 'String',
                headerFieldDefId: 1,
                isRequired: false,
                label: { values: { en: 'Operator' } },
                value: 'A',
              },
            ],
            version: 'V1',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`неочікуваний запит: ${url}`);
    }),
  );
}

const client = (): QueryClient => new QueryClient({ defaultOptions: { queries: { retry: false } } });

function Panel({ openRequest }: { openRequest: number }): JSX.Element {
  return (
    <DocumentHeaderPanel documentId={DocumentId} canEdit collapsible openRequest={openRequest} />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentHeaderPanel: openRequest', () => {
  it('зростання openRequest розгортає згорнуту секцію; без зміни - лишається згорнутою', async () => {
    mockHeader();
    const qc = client();
    const wrap = (n: number): JSX.Element => (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={qc}>
          <Panel openRequest={n} />
        </QueryClientProvider>
      </MantineProvider>
    );
    const { rerender } = render(wrap(0));

    const toggle = await screen.findByTestId('document-header-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    rerender(wrap(0));
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    rerender(wrap(1));
    expect(screen.getByTestId('document-header-toggle').getAttribute('aria-expanded')).toBe('true');
  });

  it('після ручного згортання наступний openRequest знову розгортає', async () => {
    mockHeader();
    const qc = client();
    const wrap = (n: number): JSX.Element => (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={qc}>
          <Panel openRequest={n} />
        </QueryClientProvider>
      </MantineProvider>
    );
    const { rerender } = render(wrap(0));
    const toggle = await screen.findByTestId('document-header-toggle');

    rerender(wrap(1));
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    rerender(wrap(2));
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });
});
