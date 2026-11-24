import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): крок Template майстра «New document».
 *
 * Проєкт «рік · об'єкт» має ДВА шаблони (основний `GEN` і приєднаний `LAND`); каталог шаблонів системи —
 * ширший (`OTHER`), але майстер пропонує лише шаблони проєкту (`GET /projects/{id}/templates`), бере аркуші
 * обраного шаблону (`document-template?templateId=`) і надсилає `templateId` у `POST /documents`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Gen = {
  projectTemplateId: 1, templateId: 4, templateCode: 'GEN', templateNameL10n: { values: { en: 'General' } },
  currentTemplateVersionId: 5, version: '2.3.0', layout: 'PerMonthCopies', isPrimary: true, documentCount: 1,
};
const Land = {
  projectTemplateId: 2, templateId: 8, templateCode: 'LAND', templateNameL10n: { values: { en: 'Land' } },
  currentTemplateVersionId: 12, version: '1.0', layout: 'YearWithMonthColumns', isPrimary: false, documentCount: 0,
};

let posted: Record<string, unknown> | null = null;

function mockServer(templates: unknown[] | 'refuse'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';

      if (path.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posted = JSON.parse(String(init.body)) as Record<string, unknown>;

        return json({ documentId: 91 }, 201);
      }

      if (path.endsWith('/api/v1/documents')) return json({ items: [], nextCursor: null });

      if (path.endsWith('/api/v1/projects/1/templates')) {
        return templates === 'refuse'
          ? json(
              {
                title: 'Forbidden', status: 403, detail: 'No access to the project.', errorCode: 'ECR-AUTH-0403',
                correlationId: 'c-9', messageKey: 'err.ECR-AUTH-0403.forbidden',
              },
              403,
            )
          : json(templates);
      }

      if (path.endsWith('/api/v1/projects/1/document-template')) {
        const templateId = new URL(url, 'http://x').searchParams.get('templateId');

        return templateId === '8'
          ? json({
              templateVersionId: 12, templateCode: 'LAND', version: '1.0', groupRules: [],
              sheets: [{ id: 30, code: 'L1', nameL10n: { values: { en: 'Land plots' } }, sheetGroup: null, isMandatory: false }],
            })
          : json({
              templateVersionId: 5, templateCode: 'GEN', version: '2.3.0', groupRules: [],
              sheets: [{ id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false }],
            });
      }

      // Каталог шаблонів системи — майстер його не читає (V-12); тут він є, щоб «зайвий» шаблон мав звідки взятися.
      if (path.endsWith('/api/v1/templates')) {
        return json({ items: [{ id: 99, code: 'OTHER', nameL10n: { values: {} }, isArchived: false }], nextCursor: null, totalCount: 1 });
      }

      if (path.endsWith('/api/v1/projects')) {
        return json({ items: [{ id: 1, code: 'P2026-SITE', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <CreateDocumentModal opened onClose={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const next = (): HTMLButtonElement => screen.getByTestId('wizard-next') as HTMLButtonElement;

async function toTemplateStep(): Promise<void> {
  fireEvent.click(await screen.findByLabelText(/documents\.project/, { selector: 'input' }));
  fireEvent.click(await screen.findByRole('option', { name: 'P2026-SITE' }));
  await waitFor(() => expect(next().disabled).toBe(false));
  fireEvent.click(next());
  await screen.findByText('⟦documents.createTemplateStep⟧');
}

const templateUrls = (): string[] =>
  vi.mocked(fetch).mock.calls.map(([input]) => String(input)).filter((url) => url.includes('/document-template'));

afterEach(() => {
  vi.unstubAllGlobals();
  posted = null;
});

describe('B74 P4: крок Template майстра нового документа', () => {
  it('два шаблони проєкту: пропонуються лише вони, без вибору Next вимкнений; LAND — аркуші LAND і templateId=8 у POST', async () => {
    mockServer([Gen, Land]);
    show();
    await toTemplateStep();

    // Двох шаблонів — нічого не обрано наперед: Next чекає вибору людини.
    expect(next().disabled).toBe(true);

    fireEvent.click(screen.getByLabelText(/documents\.template/, { selector: 'input' }));
    const options = (await screen.findAllByRole('option')).map((option) => option.textContent);
    expect(options).toEqual(['GEN · General', 'LAND · Land']);
    expect(options.join(' ')).not.toContain('OTHER');

    fireEvent.click(screen.getByRole('option', { name: 'LAND · Land' }));
    await screen.findByText('LAND · v1.0');
    await waitFor(() => expect(next().disabled).toBe(false));
    expect(templateUrls()).toEqual(['/api/v1/projects/1/document-template?templateId=8']);

    fireEvent.click(next()); // → Sheets
    const body = await screen.findByTestId('wizard-step-body');
    await within(body).findByLabelText('Land plots (L1)');
    expect(within(body).queryByLabelText('Air emissions (AIR)')).toBeNull();

    fireEvent.click(next()); // → Review
    const summary = await screen.findByTestId('wizard-summary');
    // ✎ B74 P4: шаблон — під підписом «Template», версія — окремим рядком «Version».
    expect(within(summary).getByText('⟦documents.template⟧')).toBeTruthy();
    expect(within(summary).getByText('LAND')).toBeTruthy();
    expect(within(summary).getByText('⟦documents.version⟧')).toBeTruthy();
    expect(within(summary).getByText('v1.0')).toBeTruthy();
    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted?.['templateId']).toBe(8);
    expect(posted?.['sheetDefIds']).toEqual([30]);
  });

  it('один шаблон проєкту: обраний сам, але крок видно; аркуші й POST — за ним', async () => {
    mockServer([Gen]);
    show();
    await toTemplateStep();

    // Крок видно, поле показує єдиний шаблон, Next доступний без дії людини.
    expect((screen.getByLabelText(/documents\.template/, { selector: 'input' }) as HTMLInputElement).value).toBe('GEN · General');
    await screen.findByText('GEN · v2.3.0');
    await waitFor(() => expect(next().disabled).toBe(false));
    expect(templateUrls()).toEqual(['/api/v1/projects/1/document-template?templateId=4']);

    fireEvent.click(next());
    await screen.findByLabelText('Air emissions (AIR)');
    fireEvent.click(next());
    await screen.findByTestId('wizard-summary');
    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted?.['templateId']).toBe(4);
  });

  it('відмова переліку шаблонів — банер з причиною у кроці, а не порожній вибір', async () => {
    mockServer('refuse');
    show();
    await toTemplateStep();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent ?? '').toContain('No access to the project.');
    expect(next().disabled).toBe(true);
  });
});
