import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ B74 P4 (D-576): у проєкті «рік · об'єкт» документи різних шаблонів — секція «Contract» шапки показує КОД ШАБЛОНУ
 * документа службовим рядком (лише читання) між File Number і Version.
 * ⛔ Мутація: прибрати `template: templateCode` із `serviceValues` — червоне (рядка немає).
 */
const DocumentId = 52;

const Fields = [
  {
    code: 'AREA',
    dataType: 'String',
    headerFieldDefId: 1,
    isRequired: false,
    label: { values: { en: 'Area', ru: 'Область' } },
    value: null,
  },
];

function show(templateCode?: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input).split('?')[0] ?? '';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`)) return json({ fields: Fields, version: 'V1' });
      if (url.endsWith('/api/v1/registries')) return json([]);
      throw new Error(`неочікуваний запит: ${url}`);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentHeaderPanel
          documentId={DocumentId}
          canEdit
          businessKey="HSE401-P7-0001"
          templateVersion="2.0.0"
          templateCode={templateCode}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentHeaderPanel: шаблон документа (B74)', () => {
  it('показує код шаблону лише для читання між File Number і Version', async () => {
    show('HSE401');

    const section = await screen.findByTestId('document-header-contract');
    const order = [...section.querySelectorAll('[data-header-field]')].map((node) => node.getAttribute('data-header-field'));
    expect(order).toEqual(['AREA', 'fileNumber', 'template', 'version']);

    const template = section.querySelector<HTMLInputElement>('[data-header-field="template"]');
    expect(template?.value).toBe('HSE401');
    expect(template?.readOnly).toBe(true);
  });

  it('без коду шаблону рядка немає', async () => {
    show(undefined);

    const section = await screen.findByTestId('document-header-contract');
    expect(section.querySelector('[data-header-field="template"]')).toBeNull();
  });
});
