import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle, type HeldWrites } from '@/test/closedDialogProbe';
import { SheetCopyBar, type SheetCopyTarget } from '../SheetCopyBar';

/**
 * B145b: діалог перейменування групи аркушів шле один `PUT …/name` на одне підтвердження.
 *
 * ⛔ Діра (зонд на базі 812a049c9, справжній діалог):
 *  - подвійний клік по «Save» слав ДВА `PUT /documents/42/sheets/10/name` (`loading` вмикається лише через 100 мс,
 *    `isPending` — після перерендеру);
 *  - після відповіді власник закриває діалог, власний `Modal` ще ~200 мс у DOM, чернетка та сама, кнопка активна —
 *    клік слав ще один `PUT` з тим самим тілом.
 *
 * Захист — у самому діалозі: кнопка діє лише у відкритому діалозі й по одному запиту (ref, синхронно); відмова
 * сервера звільняє її — діалог лишається відкритим, можна повторити.
 *
 * Мутаційний доказ: прибрати `!renameOpen ||` у `SheetCopyBar.tsx` → червоний другий тест
 * (`expected [ …(2) ] to deeply equal [ Array(1) ]`); прибрати `|| renaming.current`
 * → червоний перший (той самий текст).
 */

const Target: SheetCopyTarget = {
  sheetDefId: 10,
  sheetCode: 'GEN',
  label: 'Boiler 2',
  groupNameValues: { en: 'Boiler' },
  sheetCopyId: 77,
  isCopyable: true,
};

const Rename = 'PUT /api/v1/documents/42/sheets/10/name';
const Languages = [{ code: 'en', nameNative: 'English', isDefault: true }];

// ⚠ Код і ключ відмови — з каталогу (`SHEETCOPY-sheet-copies.sql`).
const Refusal = {
  status: 409,
  body: {
    title: 'Conflict',
    status: 409,
    errorCode: 'ECR-DOC-0409',
    correlationId: 'c-1',
    messageKey: 'err.ECR-DOC-0409.sheetCopyProjectArchived',
    detail: 'The project is archived.',
  },
};

async function openRename(write?: Parameters<typeof holdWrites>[0]): Promise<{ server: HeldWrites; save: HTMLElement }> {
  const server = holdWrites({ read: (url) => (url.includes('/languages') ? Languages : []), ...write });
  renderKeepMounted(<SheetCopyBar documentId={42} target={Target} canAdd onSelect={() => undefined} />);

  fireEvent.click(screen.getByTestId('sheet-copy-rename'));
  const save = await screen.findByTestId('sheet-copy-rename-save');
  await waitFor(() => {
    expect(save).toHaveProperty('disabled', false);
  });

  return { server, save };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: перейменування групи аркушів — один запит на підтвердження', () => {
  it('подвійний клік по «Save» шле один PUT', async () => {
    const { server, save } = await openRename({ write: () => ({ status: 204 }) });

    fireEvent.click(save);
    fireEvent.click(save);
    await settle();

    expect(server.sent).toEqual([Rename]);
    expect(server.bodies).toEqual([{ nameL10n: { en: 'Boiler' } }]);
  });

  it('клік по «Save» діалогу, що закрився (ще в DOM), нічого не шле', async () => {
    const { server, save } = await openRename({ write: () => ({ status: 204 }) });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Rename]);
    });
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    // Перечитування після закриття завершилось: кнопка вже не зайнята запитом.
    await settle();
    await settle();

    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([Rename]);
  });

  it('відмова сервера звільняє кнопку: діалог відкритий, повтор шле другий PUT', async () => {
    const { server, save } = await openRename({ write: (_method, _url, index) => (index === 0 ? Refusal : { status: 204 }) });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Rename]);
    });
    server.release();
    expect(await screen.findByRole('alert')).not.toBeNull();
    expect(screen.queryByRole('dialog')).not.toBeNull();

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Rename, Rename]);
    });
  });
});
