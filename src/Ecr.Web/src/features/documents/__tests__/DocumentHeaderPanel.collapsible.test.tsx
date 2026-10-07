import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { testTheme } from '@/test/render';

/**
 * UI-16: шапка документа згорнута з підсумком значень у заголовку (KIT §1 п.4,
 * §6.7 `E.Collapsible`) і розгортається сама, коли поле потребує уваги.
 */
const DocumentId = 31;

interface Field {
  code: string;
  dataType: string;
  headerFieldDefId: number;
  isRequired: boolean;
  label: { values: Record<string, string> };
  value: unknown;
}

function field(patch: Partial<Field>): Field {
  return {
    code: 'OPERATOR',
    dataType: 'String',
    headerFieldDefId: 1,
    isRequired: false,
    label: { values: { en: 'Operator' } },
    value: 'Acceptance A2',
    ...patch,
  };
}

function show(fields: Field[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`)) {
        return new Response(JSON.stringify({ fields, version: 'V1' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      throw new Error(`неочікуваний запит: ${url}`);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentHeaderPanel documentId={DocumentId} canEdit collapsible />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentHeaderPanel: згорнута секція (UI-16)', () => {
  it('за замовчуванням згорнута, у заголовку — підсумок значень; кнопки «Save» не видно', async () => {
    show([field({}), field({ code: 'DATE', dataType: 'String', label: { values: { en: 'Report date' } }, value: '2026-10-05', headerFieldDefId: 2 })]);

    const toggle = await screen.findByTestId('document-header-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByTestId('document-header-summary').textContent).toBe(
      '· Operator Acceptance A2 · Report date 2026-10-05',
    );
    expect(screen.queryByRole('button', { name: '⟦common.save⟧' })).toBeNull();

    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByRole('button', { name: '⟦common.save⟧' })).toBeDefined();
    expect(screen.queryByTestId('document-header-summary')).toBeNull();
  });

  it('обов\'язкове порожнє поле — секція розгорнута сама й не згортається', async () => {
    // ⛔ Мутаційний доказ: прибери `missingRequired` з `mustStayOpen` — секція
    // лишиться згорнутою, і перше очікування почервоніє.
    show([field({ isRequired: true, value: null })]);

    const toggle = await screen.findByTestId('document-header-toggle');
    await waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('true'));

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByRole('textbox', { name: 'Operator *' })).toBeDefined();
  });

  it('незбережена правка не ховається: поки поле змінене, згорнути не можна', async () => {
    show([field({})]);

    const toggle = await screen.findByTestId('document-header-toggle');
    fireEvent.click(toggle);

    fireEvent.change(await screen.findByRole('textbox', { name: 'Operator' }), { target: { value: 'B' } });

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect((screen.getByRole('textbox', { name: 'Operator' }) as HTMLInputElement).value).toBe('B');
  });
});
