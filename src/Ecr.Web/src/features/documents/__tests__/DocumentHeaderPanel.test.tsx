import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { showDone } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * Панель шапки документа: `GET/PATCH /api/v1/documents/{id}/header`.
 *
 * ⛔ Сервер — справжній `fetch`-стаб із тілом `application/problem+json`, той
 * самий прийом, що `BusinessKeyChangeAction.test.tsx`: через тест іде той
 * самий розбір відмови (`apiFetch` → `EcrApiError` → `problemText`), що й у
 * продукті — не замокана функція показу помилки.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DocumentId = 11;

interface HeaderField {
  code: string;
  dataType: string;
  headerFieldDefId: number;
  isRequired: boolean;
  label: { values: Record<string, string> };
  value: unknown;
}

function field(patch: Partial<HeaderField>): HeaderField {
  return {
    code: 'CODE',
    dataType: 'String',
    headerFieldDefId: 1,
    isRequired: false,
    label: { values: { en: 'Label' } },
    value: null,
    ...patch,
  };
}

interface Sent {
  url: string;
  method: string;
  body: unknown;
}

const sent: Sent[] = [];

function mockServer(fields: HeaderField[], patchResponse?: { status: number; body?: unknown }): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'GET') {
        return new Response(JSON.stringify({ fields }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'PATCH') {
        sent.push({
          url,
          method,
          body: init?.body === undefined ? undefined : (JSON.parse(String(init.body)) as unknown),
        });

        const response = patchResponse ?? { status: 200, body: { fields } };

        return response.body === undefined
          ? new Response(null, { status: response.status })
          : new Response(JSON.stringify(response.body), {
              status: response.status,
              headers: { 'Content-Type': 'application/problem+json' },
            });
      }

      throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
    }),
  );
}

function show(options: {
  fields: HeaderField[];
  canEdit?: boolean;
  patchResponse?: { status: number; body?: unknown };
}): QueryClient {
  mockServer(options.fields, options.patchResponse);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <DocumentHeaderPanel documentId={DocumentId} canEdit={options.canEdit ?? true} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
});

describe('DocumentHeaderPanel: порожній перелік полів', () => {
  it('панель не рендериться ВЗАГАЛІ — порожній масив не помилка', async () => {
    show({ fields: [] });

    // ⛔ Мутаційний доказ: заміни цю умову на «завжди показувати» — і
    // `document-header-panel` з'явиться в DOM попри порожній перелік.
    await waitFor(() => {
      expect(screen.queryByTestId('document-header-panel')).toBeNull();
    });

    // Даємо мікрозадачам розв'язатися, щоб переконатись — це стійкий стан,
    // а не проміжний кадр завантаження.
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByTestId('document-header-panel')).toBeNull();
  });
});

describe('DocumentHeaderPanel: readonly без права на запис', () => {
  it('canEdit=false — поля недоступні для редагування, кнопки збереження немає', async () => {
    show({
      fields: [field({ code: 'NOTE', dataType: 'String', value: 'привіт' })],
      canEdit: false,
    });

    const input = await screen.findByLabelText('Label');

    // ⛔ Мутаційний доказ: прибери `disabled={!canEdit || …}` — цей рядок
    // почервоніє, бо поле стане редагованим попри відсутність гранта Write.
    expect(input.hasAttribute('disabled')).toBe(true);

    expect(screen.queryByRole('button', { name: '⟦common.save⟧' })).toBeNull();
  });

  it('canEdit=true — поле доступне для редагування', async () => {
    show({ fields: [field({ code: 'NOTE', dataType: 'String', value: 'привіт' })] });

    const input = await screen.findByLabelText('Label');
    expect(input.hasAttribute('disabled')).toBe(false);
  });
});

describe('DocumentHeaderPanel: ECR-HDR-0422 — конкретне повідомлення каталогу', () => {
  it('банер показує РЕЧЕННЯ сервера, а не узагальнену відмову', async () => {
    show({
      fields: [field({ code: 'QTY', dataType: 'Int', value: 5 })],
      patchResponse: {
        status: 422,
        body: {
          title: 'Unprocessable Entity',
          status: 422,
          errorCode: 'ECR-HDR-0422',
          correlationId: 'cid-hdr-422',
          detail: 'Поле «QTY» очікує ціле число, отримано текст.',
          messageKey: 'err.ECR-HDR-0422',
        },
      },
    });

    const input = await screen.findByLabelText('Label');
    fireEvent.change(input, { target: { value: '6' } });

    fireEvent.click(await screen.findByRole('button', { name: '⟦common.save⟧' }));

    const alert = await screen.findByRole('alert');

    // ⛔ Мутаційний доказ: підмінити показ на узагальнене «щось пішло не
    // так» (наприклад, `t('common.saveFailed')` замість `shown.detail`) — і
    // цей рядок почервоніє, бо саме РЕЧЕННЯ сервера з екрана пропаде.
    expect(alert.textContent).toContain('Поле «QTY» очікує ціле число, отримано текст.');
    expect(alert.textContent).toContain('ECR-HDR-0422');
  });

  it('без messageKey подробиця НЕ показується — лишається лише назва (доказ, що показ саме каталожний)', async () => {
    show({
      fields: [field({ code: 'QTY', dataType: 'Int', value: 5 })],
      patchResponse: {
        status: 422,
        body: {
          title: 'Unprocessable Entity',
          status: 422,
          errorCode: 'ECR-HDR-0422',
          correlationId: 'cid-hdr-422-raw',
          detail: 'raw developer message, not for users',
        },
      },
    });

    const input = await screen.findByLabelText('Label');
    fireEvent.change(input, { target: { value: '6' } });
    fireEvent.click(await screen.findByRole('button', { name: '⟦common.save⟧' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).not.toContain('raw developer message');
  });
});

describe('DocumentHeaderPanel: збереження', () => {
  it('надсилає лише ЗМІНЕНІ поля', async () => {
    show({
      fields: [
        field({ code: 'A', dataType: 'String', value: 'стара' }),
        field({ code: 'B', dataType: 'String', value: 'незмінна', label: { values: { en: 'B' } } }),
      ],
    });

    const inputA = await screen.findByLabelText('Label');
    fireEvent.change(inputA, { target: { value: 'нова' } });

    fireEvent.click(await screen.findByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(sent.length).toBe(1));

    expect(sent[0]).toMatchObject({
      url: `/api/v1/documents/${String(DocumentId)}/header`,
      method: 'PATCH',
    });

    // ⛔ Мутаційний доказ: надішли ВСІ поля замість лише змінених — цей
    // рядок почервоніє, бо `B` з'явиться в тілі запиту.
    expect(sent[0]?.body).toEqual({
      fields: [{ code: 'A', isEmpty: false, value: 'нова' }],
    });

    expect(showDone).toHaveBeenCalled();
  });

  it('непорушене поле без значення НЕ вважається зміненим (порожній текст ≡ null)', async () => {
    show({ fields: [field({ code: 'NOTE', dataType: 'String', value: null })] });

    await screen.findByLabelText('Label');

    // Кнопка збереження вимкнена, доки нічого не змінено.
    const button = await screen.findByRole('button', { name: '⟦common.save⟧' });
    expect(button.hasAttribute('disabled')).toBe(true);
  });

  it('Bool: не показаний як змінений, доки чекбокс не торкнулись (немає хибного dirty на null)', async () => {
    show({ fields: [field({ code: 'FLAG', dataType: 'Bool', value: null })] });

    await screen.findByLabelText('Label');

    const button = await screen.findByRole('button', { name: '⟦common.save⟧' });
    expect(button.hasAttribute('disabled')).toBe(true);
  });

  it('Decimal: приведення значення йде через ту саму coerce(), що й комірка сітки', async () => {
    show({ fields: [field({ code: 'AMOUNT', dataType: 'Decimal', value: '5.0000000000' })] });

    const input = await screen.findByLabelText('Label');

    // Той самий текст, лише інакше записаний, — НЕ зміна.
    fireEvent.change(input, { target: { value: '5' } });
    expect((await screen.findByRole('button', { name: '⟦common.save⟧' })).hasAttribute('disabled')).toBe(
      true,
    );

    fireEvent.change(input, { target: { value: '6' } });
    const button = await screen.findByRole('button', { name: '⟦common.save⟧' });
    expect(button.hasAttribute('disabled')).toBe(false);

    fireEvent.click(button);
    await waitFor(() => expect(sent.length).toBe(1));
    expect(sent[0]?.body).toEqual({
      fields: [{ code: 'AMOUNT', isEmpty: false, value: '6' }],
    });
  });

  it('Date і Lookup рендеряться без падіння (лінивий DateInput, сире число Lookup)', async () => {
    show({
      fields: [
        field({ code: 'REPORT_DATE', dataType: 'Date', value: '2026-01-15', label: { values: { en: 'Date' } } }),
        field({ code: 'REF', dataType: 'Lookup', value: 42, label: { values: { en: 'Ref' } } }),
      ],
    });

    await screen.findByLabelText('Ref');
    expect((await screen.findByLabelText('Ref')).getAttribute('value')).toBe('42');

    // `DateInput` — окремий чанк (`import('@mantine/dates')`); дочекатись,
    // доки Suspense розв'яжеться і поле з'явиться.
    await waitFor(() => expect(screen.queryByLabelText('Date')).not.toBeNull());
  });
});
