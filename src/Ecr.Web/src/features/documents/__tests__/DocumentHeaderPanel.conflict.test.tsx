import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { unsavedCount } from '@/shared/ui/unsavedSources';
import { testTheme } from '@/test/render';

/**
 * AN-104 / `D1-04`: `409` на шапці не стирає набране людиною.
 *
 * ⛔ Доти `onError` робив `adopt(fresh)` — `setDraft(draftOf(fresh))`: УСЯ чернетка
 * замінювалась значеннями сервера, порівняти «моє / чинне» було ніде, а сторож
 * переходу після `409` бачив «чисту» чернетку (`unsavedCount() === 0`).
 *
 * ⚠ Сервер — `fetch`-стаб, що віддає шапку ПОСЛІДОВНО: перше читання — точка
 * відліку людини, друге (перечитування після `409`) — чинний стан після чужої
 * правки.
 */

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DocumentId = 12;

interface Field {
  code: string;
  dataType: string;
  headerFieldDefId: number;
  isRequired: boolean;
  label: { values: Record<string, string> };
  value: unknown;
}

function fields(a: string, b: string): Field[] {
  return [
    { code: 'A', dataType: 'String', headerFieldDefId: 1, isRequired: false, label: { values: { en: 'Field A' } }, value: a },
    { code: 'B', dataType: 'String', headerFieldDefId: 2, isRequired: false, label: { values: { en: 'Field B' } }, value: b },
  ];
}

const patches: { fields: unknown; baseVersion: unknown }[] = [];

/**
 * @param fresh Шапка після чужої правки — її віддає друге й наступні читання.
 */
function mockServer(fresh: Field[]): void {
  patches.length = 0;
  let gets = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'GET') {
        gets += 1;
        const body = gets === 1 ? { fields: fields('1', '2'), version: 'V1' } : { fields: fresh, version: 'V2' };
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'PATCH') {
        const body = JSON.parse(String(init?.body)) as { fields: unknown; baseVersion: unknown };
        patches.push(body);

        if (patches.length === 1) {
          return new Response(
            JSON.stringify({
              title: 'Conflict',
              status: 409,
              errorCode: 'ECR-DOC-0409',
              correlationId: 'cid-an104',
              detail: 'Someone else changed the document header after you opened it.',
              messageKey: 'err.ECR-DOC-0409.headerStale',
            }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        return new Response(JSON.stringify({ fields: fresh, version: 'V3' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.endsWith('/api/v1/me') && method === 'GET') {
        return new Response(JSON.stringify({ permissions: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
    }),
  );
}

function show(fresh: Field[]): void {
  mockServer(fresh);

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentHeaderPanel documentId={DocumentId} canEdit />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function saveButton(): HTMLElement {
  return screen.getByRole('button', { name: '⟦common.save⟧' });
}

/** Людина міняє `B` на «5» і зберігає — сервер відповідає `409`. */
async function editBAndHitConflict(): Promise<{ a: HTMLInputElement; b: HTMLInputElement }> {
  const a = (await screen.findByLabelText('Field A')) as HTMLInputElement;
  const b = (await screen.findByLabelText('Field B')) as HTMLInputElement;

  fireEvent.change(b, { target: { value: '5' } });
  fireEvent.click(saveButton());

  await waitFor(() => expect(patches).toHaveLength(1));

  return { a, b };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AN-104 / D1-04: 409 на шапці не стирає чернетку', () => {
  it('чужа правка ІНШОГО поля: чинне значення приходить, набране лишається, зберегти можна з новою версією', async () => {
    show(fields('9', '2'));
    const { a, b } = await editBAndHitConflict();

    await waitFor(() => expect(a.value).toBe('9'));

    // ⛔ Мутація: повернути `adopt(fresh.data)` в `onError` — тут «2».
    expect(b.value).toBe('5');
    expect(screen.queryByTestId('document-header-conflict')).toBeNull();
    expect(unsavedCount()).toBe(1);

    await waitFor(() => expect(saveButton().hasAttribute('disabled')).toBe(false));
    fireEvent.click(saveButton());

    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches[1]).toEqual({
      fields: [{ code: 'B', isEmpty: false, value: '5' }],
      baseVersion: 'V2',
    });
  });

  it('чужа правка ТОГО САМОГО поля: «ваше / чинне», збереження вимкнене до рішення; «Keep mine» його вмикає', async () => {
    show(fields('1', '7'));
    const { b } = await editBAndHitConflict();

    const conflict = await screen.findByTestId('document-header-conflict');
    expect(conflict.querySelector('[data-header-conflict="B"]')).not.toBeNull();
    expect(b.value).toBe('5');

    // ⛔ Перезаписати чуже мовчки не можна: спершу рішення людини.
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    fireEvent.click(screen.getByTestId('document-header-conflict-keep'));
    await waitFor(() => expect(screen.queryByTestId('document-header-conflict')).toBeNull());
    expect(saveButton().hasAttribute('disabled')).toBe(false);

    fireEvent.click(saveButton());
    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches[1]).toEqual({
      fields: [{ code: 'B', isEmpty: false, value: '5' }],
      baseVersion: 'V2',
    });
  });

  it('«Взяти чинне» ставить значення сервера, і зберігати вже нічого', async () => {
    show(fields('1', '7'));
    const { b } = await editBAndHitConflict();

    await screen.findByTestId('document-header-conflict');
    fireEvent.click(screen.getByTestId('document-header-conflict-current'));

    await waitFor(() => expect(b.value).toBe('7'));
    expect(screen.queryByTestId('document-header-conflict')).toBeNull();
    expect(saveButton().hasAttribute('disabled')).toBe(true);
  });
});
