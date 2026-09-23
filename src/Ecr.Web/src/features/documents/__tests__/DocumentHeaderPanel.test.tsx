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
  lookupRegistryDefId?: number | null;
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

/** Довідник — фікстура `GET /api/v1/registries` (`RegistryDefDto`). */
interface RegistryDef {
  code: string;
  fields: unknown[];
  id: number;
  isHierarchical: boolean;
  isTemporal: boolean;
  nameL10n: { values: Record<string, string> };
}

function registryDef(patch: Partial<RegistryDef> & { id: number; code: string }): RegistryDef {
  return {
    fields: [],
    isHierarchical: false,
    isTemporal: false,
    nameL10n: { values: { en: patch.code } },
    ...patch,
  };
}

/** Запис довідника — фікстура `GET /api/v1/registries/{code}/entries` (`RegistryEntryDto`). */
interface RegistryEntry {
  code: string;
  display: string;
  id: number;
  parentEntryId: number | null;
  validFrom: string | null;
  validTo: string | null;
}

function registryEntry(patch: Partial<RegistryEntry> & { id: number; display: string }): RegistryEntry {
  return {
    code: String(patch.id),
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    ...patch,
  };
}

interface Sent {
  url: string;
  method: string;
  body: unknown;
}

const sent: Sent[] = [];

/** Кожен GET `…/entries`: код довідника і сирий рядок запиту (без `?`). */
interface EntriesRequest {
  code: string;
  query: string | null;
}

const entriesRequests: EntriesRequest[] = [];

function mockServer(
  fields: HeaderField[],
  patchResponse?: { status: number; body?: unknown },
  registries?: { list: readonly RegistryDef[]; entries: Readonly<Record<string, readonly RegistryEntry[]>> },
): void {
  sent.length = 0;
  entriesRequests.length = 0;

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

      // ⚠ Той самий двокроковий резолв, що `DocumentGrid.tsx` для Lookup-
      // колонок сітки: перелік довідників (ID → код), тоді записи ЗА КОДОМ.
      if (url.endsWith('/api/v1/registries') && method === 'GET') {
        return new Response(JSON.stringify(registries?.list ?? []), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      // ⚠ `?asOf=…` — лише для ТЕМПОРАЛЬНОГО довідника (`GetRegistryEntriesHandler`
      // фікс, той самий PR): шлях звіряється БЕЗ рядка запиту, а сам URL
      // записується в `entriesRequests` нижче — тест «Lookup з temporal-
      // довідником» перевіряє САМЕ query-параметр. Окремий масив, а не
      // спільний `sent`: той рахує лише `PATCH …/header`, і GET `/entries`
      // серед Lookup-полів приходить ДО кліку «Зберегти» — потрапивши в
      // `sent`, він зламав би `sent.length === 1` у сусідніх тестах.
      const [entriesPath, entriesQuery] = url.split('?');
      const entriesMatch = /\/api\/v1\/registries\/([^/]+)\/entries$/.exec(entriesPath ?? '');
      if (entriesMatch !== null && method === 'GET') {
        const code = decodeURIComponent(entriesMatch[1] ?? '');
        entriesRequests.push({ code, query: entriesQuery ?? null });
        return new Response(JSON.stringify(registries?.entries[code] ?? []), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
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
  registries?: { list: readonly RegistryDef[]; entries: Readonly<Record<string, readonly RegistryEntry[]>> };
}): QueryClient {
  mockServer(options.fields, options.patchResponse, options.registries);

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

  it('Date рендериться без падіння (лінивий DateInput, `import(\'@mantine/dates\')`)', async () => {
    show({
      fields: [
        field({ code: 'REPORT_DATE', dataType: 'Date', value: '2026-01-15', label: { values: { en: 'Date' } } }),
      ],
    });

    // `DateInput` — окремий чанк; дочекатись, доки Suspense розв'яжеться і
    // поле з'явиться.
    await waitFor(() => expect(screen.queryByLabelText('Date')).not.toBeNull());
  });
});

/**
 * Lookup-поле шапки — повноцінний picker, не сире число (`4f167396` дав
 * `lookupRegistryDefId`; клієнт резолвить назву й вибір ТИМ САМИМ шляхом, що
 * `DocumentGrid.tsx`/`LookupCellEditor.ts` для Lookup-комірок сітки:
 * `GET /api/v1/registries` → код, тоді `GET /api/v1/registries/{code}/entries`
 * → записи, `entry.display` як підпис. Взаємодія з `Select` — той самий
 * прийом, що `ColumnEditor.registryLookup.test.tsx` (клік по полю відкриває
 * список, опції шукаються через `screen`, бо випадний список рендериться в
 * порталі поза деревом форми).
 */
describe('DocumentHeaderPanel: Lookup-поле — picker за довідником', () => {
  it('показує людську назву обраного запису, не сирий ValueRegistryEntryId', async () => {
    show({
      fields: [
        field({
          code: 'UNIT',
          dataType: 'Lookup',
          value: 42,
          lookupRegistryDefId: 7,
          label: { values: { en: 'Unit' } },
        }),
      ],
      registries: {
        list: [registryDef({ id: 7, code: 'UNITS' })],
        entries: { UNITS: [registryEntry({ id: 42, display: 'Кілограм' })] },
      },
    });

    const select = await screen.findByLabelText('Unit');

    // ⛔ Мутаційний доказ: поверни показ сирого `ValueRegistryEntryId`
    // замість резолву через довідник — і це порівняння почервоніє (`value`
    // знову стане `'42'`, не назвою запису).
    await waitFor(() => {
      expect((select as HTMLInputElement).value).toBe('Кілограм');
    });
  });

  it('вибір запису в picker надсилає числовий ValueRegistryEntryId у PATCH, не назву', async () => {
    show({
      fields: [
        field({
          code: 'UNIT',
          dataType: 'Lookup',
          value: null,
          lookupRegistryDefId: 7,
          label: { values: { en: 'Unit' } },
        }),
      ],
      registries: {
        list: [registryDef({ id: 7, code: 'UNITS' })],
        entries: {
          UNITS: [
            registryEntry({ id: 42, display: 'Кілограм' }),
            registryEntry({ id: 43, display: 'Тонна' }),
          ],
        },
      },
    });

    const select = await screen.findByLabelText('Unit');

    // ⚠ Доки записи довідника не приїхали, поле вимкнене (`lookupPending`) —
    // клік по вимкненому `Select` не відкриває список, і `findByRole('option')`
    // нижче не знайшов би нічого ніколи.
    await waitFor(() => expect(select.hasAttribute('disabled')).toBe(false));

    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: 'Тонна' }));

    fireEvent.click(await screen.findByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(sent.length).toBe(1));

    // ⛔ Мутаційний доказ: надішли текст («Тонна») чи рядок замість числа —
    // цей рядок почервоніє.
    expect(sent[0]?.body).toEqual({
      fields: [{ code: 'UNIT', isEmpty: false, value: 43 }],
    });
  });

  it('запис видалено з довідника — не падає, показує зрозумілий стан (id, не порожньо)', async () => {
    show({
      fields: [
        field({
          code: 'UNIT',
          dataType: 'Lookup',
          value: 999,
          lookupRegistryDefId: 7,
          label: { values: { en: 'Unit' } },
        }),
      ],
      registries: {
        // 999 навмисно відсутній серед записів — запис видалили з довідника.
        list: [registryDef({ id: 7, code: 'UNITS' })],
        entries: { UNITS: [registryEntry({ id: 42, display: 'Кілограм' })] },
      },
    });

    const select = await screen.findByLabelText('Unit');

    // ⛔ Мутаційний доказ: показ порожнього поля замість ідентифікатора (той
    // самий фолбек, що `lookupCellDisplay` для READ-показу комірки сітки) —
    // або падіння рендера — цей рядок почервоніє.
    await waitFor(() => {
      expect((select as HTMLInputElement).value).toBe('999');
    });

    // Панель лишається робочою: рендер не впав, кнопка збереження є.
    expect(screen.queryByRole('button', { name: '⟦common.save⟧' })).not.toBeNull();
  });

  it('lookupRegistryDefId відсутній (null) — захисний фолбек: сире число текстовим полем', async () => {
    show({
      fields: [
        field({
          code: 'REF',
          dataType: 'Lookup',
          value: 42,
          lookupRegistryDefId: null,
          label: { values: { en: 'Ref' } },
        }),
      ],
    });

    const input = await screen.findByLabelText('Ref');
    expect((input as HTMLInputElement).value).toBe('42');
  });

  /*
   * ⛔ Дефект живого прогону (той самий PR, `GetRegistryEntriesHandler.cs`):
   * `GET …/entries` вимагав `asOf` БЕЗУМОВНО, і цей піцкер його не надсилав
   * НІКОЛИ — сервер відмовляв `422` для КОЖНОГО довідника, включно з
   * нетемпоральним (усі фікстури вище — `isTemporal: false` за замовчуванням
   * `registryDef()`, і саме тому жоден із попередніх тестів цього не ловив).
   * Два тести нижче — контраст: без `asOf` для нетемпорального (як і
   * раніше), з `asOf` — лише для темпорального.
   */
  it('нетемпоральний довідник (за замовчуванням) — запит БЕЗ query-параметра asOf', async () => {
    show({
      fields: [
        field({
          code: 'UNIT',
          dataType: 'Lookup',
          value: null,
          lookupRegistryDefId: 7,
          label: { values: { en: 'Unit' } },
        }),
      ],
      registries: {
        list: [registryDef({ id: 7, code: 'UNITS', isTemporal: false })],
        entries: { UNITS: [registryEntry({ id: 42, display: 'Кілограм' })] },
      },
    });

    await screen.findByLabelText('Unit');

    await waitFor(() => {
      expect(entriesRequests.some((r) => r.code === 'UNITS')).toBe(true);
    });

    const request = entriesRequests.find((r) => r.code === 'UNITS');
    expect(request?.query).toBeNull();
  });

  it('темпоральний довідник — запит несе asOf (сьогоднішня дата клієнта)', async () => {
    show({
      fields: [
        field({
          code: 'UNIT',
          dataType: 'Lookup',
          value: null,
          lookupRegistryDefId: 7,
          label: { values: { en: 'Unit' } },
        }),
      ],
      registries: {
        list: [registryDef({ id: 7, code: 'UNITS', isTemporal: true })],
        entries: { UNITS: [registryEntry({ id: 42, display: 'Кілограм' })] },
      },
    });

    await screen.findByLabelText('Unit');

    await waitFor(() => {
      expect(entriesRequests.some((r) => r.code === 'UNITS')).toBe(true);
    });

    const request = entriesRequests.find((r) => r.code === 'UNITS');
    const today = new Date();
    const expected = `asOf=${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;

    // ⛔ Мутаційний доказ: прибери гейт `isTemporal` перед `asOf` у
    // `DocumentHeaderPanel.tsx` (лишити `null` завжди чи навпаки завжди
    // надсилати) — цей рядок почервоніє: query-параметр зникне або
    // з'явиться для нетемпорального тесту вище.
    expect(request?.query).toBe(expected);
  });
});
