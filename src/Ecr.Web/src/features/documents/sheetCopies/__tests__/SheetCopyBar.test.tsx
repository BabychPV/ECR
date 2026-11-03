import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { SheetCopyBar, type SheetCopyTarget } from '../SheetCopyBar';

/**
 * Дії копій аркуша: «Add copy», «Rename», «Delete copy» (лише для копії).
 *
 * ⛔ Сервер — справжній `fetch`-стаб (`apiFetch` → `EcrApiError` → `ErrorAlert`), а не підмінені
 * функції `sheetCopyApi`: так перевіряється той самий шлях запиту й розбір відмови, що й у продукті.
 * Контракт копій ще не запушено (TODO(contract) у `sheetCopyTypes.ts`), тому відповіді — планової форми.
 */
interface Sent {
  readonly url: string;
  readonly method: string;
  readonly body: string | null;
}

const sent: Sent[] = [];

type Reply = { status: number; body?: unknown };

function mockServer(replies: Record<string, Reply>): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.endsWith('/api/v1/languages')) {
        return new Response(JSON.stringify([{ code: 'en', nameNative: 'English', isDefault: true }]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      sent.push({ url, method, body: typeof init?.body === 'string' ? init.body : null });
      const reply = replies[`${method} ${url}`] ?? { status: 204 };

      return reply.body === undefined
        ? new Response(null, { status: reply.status })
        : new Response(JSON.stringify(reply.body), {
            status: reply.status,
            headers: { 'Content-Type': reply.status >= 400 ? 'application/problem+json' : 'application/json' },
          });
    }),
  );
}

const Original: SheetCopyTarget = {
  sheetDefId: 10,
  sheetCode: 'GEN',
  label: 'Boiler 1',
  groupNameValues: { en: 'Boiler' },
  sheetCopyId: null,
  isCopyable: true,
};

const Copy: SheetCopyTarget = { ...Original, label: 'Boiler 2', sheetCopyId: 77 };

function show(target: SheetCopyTarget, options: { canAdd?: boolean } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onSelect = vi.fn<(tabKey: string) => void>();
  const invalidate = vi.spyOn(client, 'invalidateQueries');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetCopyBar documentId={42} target={target} canAdd={options.canAdd ?? true} onSelect={onSelect} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { onSelect, invalidate };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetCopyBar: показ', () => {
  it('аркуш, що не копіюється, і не копія — нічого не малює', () => {
    mockServer({});
    show({ ...Original, isCopyable: false });

    expect(screen.queryByTestId('sheet-copy-bar')).toBeNull();
  });

  it('оригінал: «Add copy» і «Rename» є, «Delete copy» НЕМАЄ', () => {
    mockServer({});
    show(Original);

    expect(screen.getByTestId('sheet-copy-add')).toBeDefined();
    expect(screen.getByTestId('sheet-copy-rename')).toBeDefined();
    expect(screen.queryByTestId('sheet-copy-delete')).toBeNull();
  });

  it('копія: «Delete copy» є', () => {
    mockServer({});
    show(Copy);

    expect(screen.getByTestId('sheet-copy-delete')).toBeDefined();
  });

  it('аркуш не можна редагувати — «Add copy» вимкнена', () => {
    mockServer({});
    show(Original, { canAdd: false });

    expect((screen.getByTestId('sheet-copy-add') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('SheetCopyBar: додати копію', () => {
  it('POST …/copies, інвалідизація таблиць і вибір нової вкладки', async () => {
    mockServer({
      'POST /api/v1/documents/42/sheets/10/copies': {
        status: 201,
        body: { sheetCode: 'GEN', sheetCopyId: 78, copyOrdinal: 3 },
      },
    });
    const { onSelect, invalidate } = show(Original);

    fireEvent.click(screen.getByTestId('sheet-copy-add'));

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith('GEN~78');
    });
    expect(sent).toEqual([{ url: '/api/v1/documents/42/sheets/10/copies', method: 'POST', body: null }]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['document-tables', 42] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['document', 42] });
  });

  it('відмова сервера — банер із причиною, вкладка не змінюється', async () => {
    mockServer({
      'POST /api/v1/documents/42/sheets/10/copies': {
        status: 409,
        body: {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-add',
          detail: 'The sheet cannot be copied now.',
        },
      },
    });
    const { onSelect } = show(Original);

    fireEvent.click(screen.getByTestId('sheet-copy-add'));

    // Без messageKey сирий detail не показується (так задумано в `problemText`): заголовок, код, кореляція.
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('ECR-DOC-0409');
    expect(alert.textContent).toContain('cid-add');
    expect(onSelect).not.toHaveBeenCalled();
  });
});

describe('SheetCopyBar: перейменувати групу', () => {
  it('діалог із поточною назвою; збереження — PUT …/name з новою назвою мовами каталогу', async () => {
    mockServer({});
    const { invalidate } = show(Original);

    fireEvent.click(screen.getByTestId('sheet-copy-rename'));

    const input = await screen.findByRole('textbox', { name: /English/ });
    expect((input as HTMLInputElement).value).toBe('Boiler');

    fireEvent.change(input, { target: { value: 'Boiler house' } });
    fireEvent.click(screen.getByTestId('sheet-copy-rename-save'));

    await waitFor(() => {
      expect(sent).toEqual([
        {
          url: '/api/v1/documents/42/sheets/10/name',
          method: 'PUT',
          body: JSON.stringify({ nameL10n: { en: 'Boiler house' } }),
        },
      ]);
    });
    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['document-tables', 42] });
    });
  });

  it('порожня назва — «Зберегти» вимкнена, нічого не надсилається', async () => {
    mockServer({});
    show(Original);

    fireEvent.click(screen.getByTestId('sheet-copy-rename'));
    const input = await screen.findByRole('textbox', { name: /English/ });
    fireEvent.change(input, { target: { value: '' } });

    expect((screen.getByTestId('sheet-copy-rename-save') as HTMLButtonElement).disabled).toBe(true);
    expect(sent).toEqual([]);
  });
});

describe('SheetCopyBar: видалити копію', () => {
  it('відкриття діалогу нічого не надсилає; підтвердження — DELETE і повернення на оригінал', async () => {
    mockServer({});
    const { onSelect } = show(Copy);

    fireEvent.click(screen.getByTestId('sheet-copy-delete'));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Boiler 2');
    expect(sent).toEqual([]);

    fireEvent.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => {
      expect(sent).toEqual([{ url: '/api/v1/documents/42/sheet-copies/77', method: 'DELETE', body: null }]);
    });
    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith('GEN');
    });
  });

  it('409 (подана копія) — банер із причиною сервера й кодом; вкладка не перемикається', async () => {
    mockServer({
      'DELETE /api/v1/documents/42/sheet-copies/77': {
        status: 409,
        body: {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-del',
          detail: 'A submitted copy cannot be deleted.',
          // ⚠ Припущення: сервер кладе причину в messageKey (як ECR-DOC-0409 при видаленні документа);
          // без нього `ErrorAlert` показує лише заголовок і код (див. тест «без messageKey» вище).
          messageKey: 'err.ECR-DOC-0409.sheetCopyDelete',
        },
      },
    });
    const { onSelect } = show(Copy);

    fireEvent.click(screen.getByTestId('sheet-copy-delete'));
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('A submitted copy cannot be deleted.');
    expect(alert.textContent).toContain('ECR-DOC-0409');
    expect(onSelect).not.toHaveBeenCalled();
  });
});
