import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { splitContractFields } from '@/features/documents/contractSection';
import { testTheme } from '@/test/render';

/** RC15-A: секція «Contract» шапки — порядок як в Excel, підписи «EN — RU», File Number і Version лише для читання. */
const DocumentId = 41;

interface Field {
  code: string;
  dataType: string;
  headerFieldDefId: number;
  isRequired: boolean;
  label: { values: Record<string, string> };
  value: unknown;
  lookupRegistryDefId?: number | null;
}

let nextId = 1;

function field(code: string, en: string, ru: string, patch: Partial<Field> = {}): Field {
  return {
    code,
    dataType: 'String',
    headerFieldDefId: nextId++,
    isRequired: false,
    label: { values: { en, ru } },
    value: null,
    ...patch,
  };
}

// Навмисно НЕ в порядку Excel: порядок має дати клієнт, а не відповідь сервера.
const Fields = [
  field('PERMIT_NUMBER', 'Permit Number', 'Номер разрешения'),
  field('CONTRACTOR', 'Contractor', 'Подрядчик', { dataType: 'Lookup', lookupRegistryDefId: 7, value: 501 }),
  field('VERSION', 'Version', 'Версия', { value: '3' }),
  field('AREA', 'Area', 'Область'),
  field('REMARK', 'Remark', 'Примечание'),
  field('FILE_NUMBER', 'File Number', 'Номер файла', { value: 'stale' }),
];

const Registries = [
  {
    code: 'CONTRACTORS',
    fields: [],
    id: 7,
    isHierarchical: false,
    isTemporal: false,
    nameL10n: { values: { en: 'Contractors' } },
  },
];

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input).split('?')[0] ?? '';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`)) return json({ fields: Fields, version: 'V1' });
      if (url.endsWith('/api/v1/registries')) return json(Registries);
      // Запис 501 закритий: серед чинних його немає — підпис має бути назвою, не id.
      if (url.endsWith('/api/v1/registries/CONTRACTORS/entries')) return json([]);
      throw new Error(`неочікуваний запит: ${url}`);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentHeaderPanel documentId={DocumentId} canEdit businessKey="ECR-2026-0007" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('splitContractFields', () => {
  it('ставить відомі поля в порядок Excel, решту лишає після секції', () => {
    const { contract, other } = splitContractFields(Fields as never);

    expect(contract.map(([key]) => key)).toEqual(['area', 'contractor', 'fileNumber', 'permitNumber', 'version']);
    expect(other.map((item) => item.code)).toEqual(['REMARK']);
  });
});

describe('DocumentHeaderPanel: секція Contract', () => {
  it('малює поля в порядку Excel з підписами «EN — RU»', async () => {
    show();

    const section = await screen.findByTestId('document-header-contract');
    const order = [...section.querySelectorAll('[data-header-field]')].map((node) => node.getAttribute('data-header-field'));

    expect(order).toEqual(['AREA', 'CONTRACTOR', 'FILE_NUMBER', 'PERMIT_NUMBER', 'VERSION']);
    expect(screen.getByText('Area — Область')).toBeTruthy();
    expect(screen.getByText('Permit Number — Номер разрешения')).toBeTruthy();
  });

  it('File Number показує BusinessKey документа і Version — лише для читання', async () => {
    show();

    await screen.findByTestId('document-header-contract');

    const fileNumber = document.querySelector<HTMLInputElement>('[data-header-field="FILE_NUMBER"]');
    const version = document.querySelector<HTMLInputElement>('[data-header-field="VERSION"]');
    const area = document.querySelector<HTMLInputElement>('[data-header-field="AREA"]');

    expect(fileNumber?.value).toBe('ECR-2026-0007');
    expect(fileNumber?.disabled).toBe(true);
    expect(version?.disabled).toBe(true);
    expect(area?.disabled).toBe(false);
  });

  it('Lookup закритого запису показує назву, а не ідентифікатор', async () => {
    show();

    await waitFor(() => {
      const contractor = document.querySelector<HTMLInputElement>('[data-header-field="CONTRACTOR"]');
      expect(contractor?.value).not.toBe('501');
    });
  });
});
