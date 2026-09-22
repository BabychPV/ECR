import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import {
  hasLockedSheet,
  hasProjectWriteGrant,
  useBusinessKeyChangeAction,
} from '@/features/documents/BusinessKeyChangeAction';
import { showDone } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * «Змінити номер справи» (бізнес-ключ документа, ФВ-3.9): показ кнопки за
 * правом і грантом, блокування заздалегідь за станом аркушів, і три класи
 * відмов сервера — `rekeyStale` (закриває діалог, банер зовні), `rekeyKeyInvalid`/
 * `rekeyReasonRequired` (під полем, діалог лишається), `rekeyDuplicate`/
 * `rekeyLocked` (банер УСЕРЕДИНІ діалогу).
 *
 * ⛔ Сервер — справжній `fetch`-стаб із тілом `application/problem+json`, той
 * самий прийом, що `DeleteDocumentAction.test.tsx`: через тест іде той самий
 * розбір відмови (`apiFetch` → `EcrApiError` → `problemText`), що й у продукті.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DocumentId = 42;
const PeriodKey = 202601;
const ProjectId = 7;

const DraftDocument: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: ProjectId,
  sheetCount: 2,
  sheetStates: { GEN: 'Draft', AIR: 'Draft' },
  hasLateEdits: false,
};

interface FakeUser {
  denies: string[];
  grants: Record<string, string>;
  isSimulation: boolean;
  language: string;
  mustChangePassword: boolean;
  permissions: string[];
  simulatedForUserId: number | null;
  userId: number;
  userName: string;
}

function currentUser(options: {
  permissions?: string[];
  grants?: Record<string, string>;
  denies?: string[];
}): FakeUser {
  return {
    denies: options.denies ?? [],
    grants: options.grants ?? {},
    isSimulation: false,
    language: 'en',
    mustChangePassword: false,
    permissions: options.permissions ?? [],
    simulatedForUserId: null,
    userId: 9,
    userName: 'tester',
  };
}

const AllowedUser = currentUser({
  permissions: ['Document.ChangeKey'],
  grants: { 'Project:7': 'Write' },
});

interface Sent {
  url: string;
  method: string;
  body: unknown;
}

const sent: Sent[] = [];

function mockServer(me: FakeUser, postResponse?: { status: number; body?: unknown }): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(me), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/business-key')) {
        sent.push({
          url,
          method,
          body: init?.body === undefined ? undefined : (JSON.parse(String(init.body)) as unknown),
        });

        const response = postResponse ?? { status: 204 };

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

function Harness({ document }: { document: DocumentSummary }): JSX.Element {
  const action = useBusinessKeyChangeAction({
    documentId: document.id,
    document,
    periodKey: PeriodKey,
  });

  return (
    <div>
      <h1>{document.businessKey}</h1>
      {action.trigger}
      {action.refusal}
    </div>
  );
}

function show(options: {
  document?: DocumentSummary;
  me?: FakeUser;
  postResponse?: { status: number; body?: unknown };
}): QueryClient {
  const document = options.document ?? DraftDocument;
  mockServer(options.me ?? AllowedUser, options.postResponse);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness document={document} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const ChangeKeyButton = { name: '⟦documents.changeKey⟧' };

/** Чекає, доки профіль `/me` доїде і кнопка показала своє СПРАВЖНЄ рішення. */
async function settle(): Promise<void> {
  await waitFor(() => expect(screen.getByRole('heading').textContent).toBeTruthy());
  // Профіль асинхронний — даємо мікрозадачам розв'язатися перед перевіркою.
  await new Promise((resolve) => setTimeout(resolve, 0));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
});

describe('hasProjectWriteGrant: дзеркало GetCurrentUserHandler.LevelForProject', () => {
  it('грант Write на проєкт — так', () => {
    expect(hasProjectWriteGrant(AllowedUser, ProjectId)).toBe(true);
  });

  it('грант Manage (вище Write) — теж так', () => {
    expect(hasProjectWriteGrant({ ...AllowedUser, grants: { 'Project:7': 'Manage' } }, ProjectId)).toBe(
      true,
    );
  });

  it('грант Read (нижче Write) — ні', () => {
    expect(hasProjectWriteGrant({ ...AllowedUser, grants: { 'Project:7': 'Read' } }, ProjectId)).toBe(
      false,
    );
  });

  it('гранта немає зовсім — ні', () => {
    expect(hasProjectWriteGrant({ ...AllowedUser, grants: {} }, ProjectId)).toBe(false);
  });

  it('явна заборона перемагає грант (ФВ-6.6)', () => {
    expect(
      hasProjectWriteGrant(
        { ...AllowedUser, grants: { 'Project:7': 'Manage' }, denies: ['Project:7'] },
        ProjectId,
      ),
    ).toBe(false);
  });

  it('профіль не визначений — ні', () => {
    expect(hasProjectWriteGrant(undefined, ProjectId)).toBe(false);
  });
});

describe('hasLockedSheet: дзеркало DocumentKeyChange.EnsureChangeable', () => {
  it('усі аркуші Draft — не заблоковано', () => {
    expect(hasLockedSheet({ GEN: 'Draft', AIR: 'Draft' })).toBe(false);
  });

  it('відхилений аркуш — так само не заблоковано (у роботі)', () => {
    expect(hasLockedSheet({ GEN: 'Rejected' })).toBe(false);
  });

  it.each(['Submitted', 'Approved'])('хоч один аркуш %s — заблоковано', (state) => {
    expect(hasLockedSheet({ GEN: 'Draft', AIR: state })).toBe(true);
  });
});

describe('useBusinessKeyChangeAction: показ кнопки', () => {
  it('право є, грант Write є, аркуші не заблоковані — кнопка активна (дзеркало)', async () => {
    show({});
    await settle();

    const button = await screen.findByRole('button', ChangeKeyButton);
    expect(button).toBeDefined();
    expect(button.hasAttribute('disabled')).toBe(false);
  });

  it('без права Document.ChangeKey — кнопки НЕМАЄ', async () => {
    show({ me: currentUser({ grants: { 'Project:7': 'Write' } }) });
    await settle();

    expect(screen.getByRole('heading', { name: 'DOC-0042' })).toBeDefined();
    expect(screen.queryByRole('button', ChangeKeyButton)).toBeNull();
  });

  it('право є, але без гранта Write на проєкт — кнопки НЕМАЄ', async () => {
    show({ me: currentUser({ permissions: ['Document.ChangeKey'], grants: {} }) });
    await settle();

    expect(screen.queryByRole('button', ChangeKeyButton)).toBeNull();
  });

  it('право є, грант лише Read — кнопки НЕМАЄ (поріг саме Write)', async () => {
    show({
      me: currentUser({ permissions: ['Document.ChangeKey'], grants: { 'Project:7': 'Read' } }),
    });
    await settle();

    expect(screen.queryByRole('button', ChangeKeyButton)).toBeNull();
  });

  it('аркуш поданий — кнопка є, але ВИМКНЕНА, з видимою причиною', async () => {
    show({ document: { ...DraftDocument, sheetStates: { GEN: 'Draft', AIR: 'Submitted' } } });
    await settle();

    const button = await screen.findByRole('button', ChangeKeyButton);
    expect(button.hasAttribute('disabled')).toBe(true);

    // ⛔ Мутаційний доказ: прибери текст `data-change-key-blocked-reason` — і
    // цей рядок почервоніє, бо причина зникне з екрана (лишиться сама сірa
    // вимкнена кнопка без жодного пояснення, як застерігає директива ФВ-14.24).
    expect(document.querySelector('[data-change-key-blocked-reason]')).not.toBeNull();
    expect(button.getAttribute('aria-describedby')).toBeTruthy();
  });

  it('аркуш погоджений — теж вимкнена', async () => {
    show({ document: { ...DraftDocument, sheetStates: { GEN: 'Approved' } } });
    await settle();

    const button = await screen.findByRole('button', ChangeKeyButton);
    expect(button.hasAttribute('disabled')).toBe(true);
  });
});

describe('useBusinessKeyChangeAction: діалог і валідність форми', () => {
  it('обидва поля порожні — підтвердження вимкнене', async () => {
    show({});
    await settle();

    fireEvent.click(await screen.findByRole('button', ChangeKeyButton));

    const confirm = await screen.findByTestId('business-key-confirm');
    expect(confirm.hasAttribute('disabled')).toBe(true);

    // Відкриття діалогу нічого не надсилає.
    expect(sent).toEqual([]);
  });

  it('лише ключ заповнено, причина порожня — підтвердження вимкнене (причина ОБОВ’ЯЗКОВА)', async () => {
    show({});
    await settle();

    fireEvent.click(await screen.findByRole('button', ChangeKeyButton));
    fireEvent.change(await screen.findByLabelText(/newBusinessKey/), {
      target: { value: 'DOC-9999' },
    });

    // ⛔ Мутаційний доказ: прибери `reasonValid` з `canSubmit` — і цей рядок
    // стане червоним, бо кнопку можна буде натиснути з порожньою причиною.
    expect(screen.getByTestId('business-key-confirm').hasAttribute('disabled')).toBe(true);
  });

  it('новий ключ збігається з чинним — підтвердження вимкнене', async () => {
    show({});
    await settle();

    fireEvent.click(await screen.findByRole('button', ChangeKeyButton));
    fireEvent.change(await screen.findByLabelText(/newBusinessKey/), {
      target: { value: DraftDocument.businessKey },
    });
    fireEvent.change(screen.getByLabelText(/reason/i), { target: { value: 'бо так треба' } });

    expect(screen.getByTestId('business-key-confirm').hasAttribute('disabled')).toBe(true);
  });

  it('ключ і причина заповнені — підтвердження активне', async () => {
    show({});
    await settle();

    fireEvent.click(await screen.findByRole('button', ChangeKeyButton));
    fireEvent.change(await screen.findByLabelText(/newBusinessKey/), {
      target: { value: 'DOC-9999' },
    });
    fireEvent.change(screen.getByLabelText(/reason/i), { target: { value: 'бо так треба' } });

    expect(screen.getByTestId('business-key-confirm').hasAttribute('disabled')).toBe(false);
  });
});

async function submitChange(newKey: string, reason: string): Promise<void> {
  fireEvent.click(await screen.findByRole('button', ChangeKeyButton));
  fireEvent.change(await screen.findByLabelText(/newBusinessKey/), { target: { value: newKey } });
  fireEvent.change(screen.getByLabelText(/reason/i), { target: { value: reason } });
  fireEvent.click(screen.getByTestId('business-key-confirm'));
}

describe('useBusinessKeyChangeAction: успіх', () => {
  it(
    'надсилає businessKey/expectedBusinessKey/reason, інвалідизує кеш, закриває діалог',
    async () => {
      const client = show({});
      await settle();
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await submitChange('DOC-9999', 'Помилка нумерації при створенні');

      await waitFor(() => expect(sent.length).toBe(1));

      expect(sent[0]).toMatchObject({
        url: `/api/v1/documents/${String(DocumentId)}/business-key`,
        method: 'POST',
      });

      // ⛔ Мутаційний доказ (перший пункт задачі): не передай
      // `expectedBusinessKey` або передай СТАРЕ значення — цей рядок
      // почервоніє. Значення береться з `document.businessKey`, тобто з
      // того, що людина БАЧИТЬ на екрані зараз.
      expect(sent[0]?.body).toEqual({
        businessKey: 'DOC-9999',
        expectedBusinessKey: DraftDocument.businessKey,
        reason: 'Помилка нумерації при створенні',
      });

      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['document', DocumentId, PeriodKey] });
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['documents'] });

      expect(showDone).toHaveBeenCalled();

      await waitFor(() => expect(screen.queryByTestId('business-key-confirm')).toBeNull());
    },
  );

  it('expectedBusinessKey відображає ПОТОЧНЕ значення на екрані, а не перше побачене', async () => {
    // ⚠ Документ, чий ключ уже інший, ніж «типовий» фікстурний — переконуємось,
    // що функція не підставляє константу, а бере САМЕ проп `document`.
    const changed: DocumentSummary = { ...DraftDocument, businessKey: 'DOC-7777' };
    show({ document: changed });
    await settle();

    await submitChange('DOC-8888', 'причина');

    await waitFor(() => expect(sent.length).toBe(1));
    expect(sent[0]?.body).toMatchObject({ expectedBusinessKey: 'DOC-7777' });
  });
});

describe('useBusinessKeyChangeAction: rekeyStale — закриває діалог, банер зовні, перечитує документ', () => {
  it('банер із причиною сервера, документ інвалідизовано, діалог закрито', async () => {
    const client = show({
      postResponse: {
        status: 409,
        body: {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-stale',
          detail: 'The document key has changed since it was read; it is now "DOC-9000".',
          messageKey: 'err.ECR-DOC-0409.rekeyStale',
          businessKey: 'DOC-9000',
        },
      },
    });
    await settle();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await submitChange('DOC-9999', 'причина');

    // Діалог закрито: кнопка підтвердження зникла.
    await waitFor(() => expect(screen.queryByTestId('business-key-confirm')).toBeNull());

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('The document key has changed since it was read');
    expect(alert.textContent).toContain('ECR-DOC-0409');

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['document', DocumentId, PeriodKey] });
    expect(showDone).not.toHaveBeenCalled();
  });
});

describe('useBusinessKeyChangeAction: rekeyDuplicate/rekeyLocked — банер УСЕРЕДИНІ діалогу', () => {
  it('rekeyLocked — показано ПРИЧИНУ сервера, а не загальну відмову; діалог лишається', async () => {
    show({
      postResponse: {
        status: 409,
        body: {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-locked',
          detail: 'The document key cannot be changed: sheet 3 for period 202601 is Submitted.',
          messageKey: 'err.ECR-DOC-0409.rekeyLocked',
          reason: 'Submitted',
        },
      },
    });
    await settle();

    await submitChange('DOC-9999', 'причина');

    // ⛔ Мутаційний доказ (останній пункт задачі): підмінити текст на
    // узагальнене «не вдалося» — і рядок нижче почервоніє, бо саме РЕЧЕННЯ
    // сервера («sheet 3 for period 202601 is Submitted») на екрані пропаде.
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('sheet 3 for period 202601 is Submitted');
    expect(alert.textContent).toContain('ECR-DOC-0409');

    // Діалог НЕ закрився: форма й далі на екрані для повторної спроби.
    expect(screen.getByTestId('business-key-confirm')).toBeDefined();
  });

  it('rekeyDuplicate — банер із ключем, який уже зайнято', async () => {
    show({
      postResponse: {
        status: 409,
        body: {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-dup',
          detail: 'Another document of this project already has the key "DOC-9999".',
          messageKey: 'err.ECR-DOC-0409.rekeyDuplicate',
          businessKey: 'DOC-9999',
        },
      },
    });
    await settle();

    await submitChange('DOC-9999', 'причина');

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Another document of this project already has the key');
    expect(screen.getByTestId('business-key-confirm')).toBeDefined();
  });
});

describe('useBusinessKeyChangeAction: rekeyKeyInvalid/rekeyReasonRequired — під полем', () => {
  it('rekeyKeyInvalid — текст сервера під полем ключа, а не банером', async () => {
    show({
      postResponse: {
        status: 422,
        body: {
          title: 'Unprocessable Entity',
          status: 422,
          errorCode: 'ECR-DOC-0422',
          correlationId: 'cid-key',
          detail: 'The new key must be 1 to 200 characters and differ from the current key.',
          messageKey: 'err.ECR-DOC-0422.rekeyKeyInvalid',
          maxLength: 200,
        },
      },
    });
    await settle();

    await submitChange('DOC-9999', 'причина');

    await waitFor(() =>
      expect(screen.getByLabelText(/newBusinessKey/).getAttribute('aria-invalid')).toBe('true'),
    );

    // Не банер: під полем — розбір за `messageKey`, не за узагальненою відмовою.
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText(/must be 1 to 200 characters/)).toBeDefined();
  });

  it('rekeyReasonRequired — текст сервера під полем причини', async () => {
    show({
      postResponse: {
        status: 422,
        body: {
          title: 'Unprocessable Entity',
          status: 422,
          errorCode: 'ECR-DOC-0422',
          correlationId: 'cid-reason',
          detail: 'A reason is required to change the document key.',
          messageKey: 'err.ECR-DOC-0422.rekeyReasonRequired',
        },
      },
    });
    await settle();

    await submitChange('DOC-9999', 'причина');

    await waitFor(() =>
      expect(screen.getByLabelText(/reason/i).getAttribute('aria-invalid')).toBe('true'),
    );
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
