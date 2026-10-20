import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { renderWithQuery } from '@/test/render';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import * as typesApi from '../../../api/typesApi';
import type { PermitType } from '../../../api/types';
import { draftOf } from '../../draftModel';
import { permitType } from '../../__tests__/fixtures';
import { DeleteTableDialog } from '../DeleteTableDialog';

/**
 * «Remove table» з кількостями (PM-03b2): що діалог каже в кожному стані — без запиту, «перевіряємо», опублікована
 * таблиця з числами, таблиця лише з чернетки, відмова запиту. Тексти — справжні, із сіду (`Catalog`).
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `DeleteTableDialog` `enabled: asksUsage` → `enabled: true` валить
 * «ніколи не публікувався: запиту немає» і «посилання»; прибрати `blocked = true` у гілці опублікованої таблиці валить
 * «опублікована таблиця: числа й вимкнена кнопка».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('../../../api/typesApi', () => ({ getPermitTypeUsage: vi.fn() }));

const usage = vi.mocked(typesApi.getPermitTypeUsage);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const published = (): PermitType => permitType({ status: 'Published', version: 2, draftVersion: 3 });

/** `type` — що знає сервер; `shown` — з чого зібрана робоча копія (за замовчуванням те саме). */
function mount(type: PermitType, shown: PermitType = type) {
  const onRemove = vi.fn();
  const onClose = vi.fn();
  renderWithQuery(<DeleteTableDialog type={type} draft={draftOf(shown)} tableCode="Storage" onRemove={onRemove} onClose={onClose} />);

  return { onRemove, onClose };
}

const removeButton = (): HTMLButtonElement => within(screen.getByRole('dialog')).getByRole('button', { name: 'Remove table' });

describe('DeleteTableDialog з кількостями', () => {
  it('тип ніколи не публікувався: запиту немає, підтвердження прибирає таблицю', () => {
    const { onRemove } = mount(permitType());

    expect(usage).not.toHaveBeenCalled();
    expect(removeButton().disabled).toBe(false);
    fireEvent.click(removeButton());
    expect(onRemove).toHaveBeenCalledWith('Storage');
  });

  it('таблиця додана в чернетці опублікованого типу й не збережена: запиту немає, видалення можна', () => {
    const withoutStorage = permitType({ status: 'Published', version: 2, draftVersion: 3 });
    withoutStorage.tables = withoutStorage.tables.filter((entry) => entry.code !== 'Storage');
    mount(withoutStorage, published());

    expect(usage).not.toHaveBeenCalled();
    expect(removeButton().disabled).toBe(false);
  });

  it('посилання з іншої таблиці: запиту немає, названо стовпець, кнопка вимкнена', () => {
    const type = permitType({ status: 'Published', version: 2, draftVersion: 3 });
    type.tables[0]!.columns[0] = { ...type.tables[0]!.columns[0]!, dataType: 'Lookup', listScope: 'SamePermitTable', siblingTableCode: 'Storage' };
    mount(type);

    expect(usage).not.toHaveBeenCalled();
    expect(screen.getByText(/Discharge\.Water_body/)).toBeTruthy();
    expect(removeButton().disabled).toBe(true);
  });

  it('опублікований тип: поки йде запит — «перевіряємо» і вимкнена кнопка', () => {
    usage.mockReturnValue(new Promise(() => undefined));
    mount(published());

    expect(usage).toHaveBeenCalledWith('WaterDischarge');
    expect(screen.getByRole('status').textContent).toBe('Checking what this table holds…');
    expect(removeButton().disabled).toBe(true);
  });

  it('опублікована таблиця: відмова сервера, числа дозволів і рядків, вимкнена кнопка', async () => {
    usage.mockResolvedValue({ instanceCount: 3, tables: [{ code: 'Discharge', rowCount: 4 }, { code: 'Storage', rowCount: 12 }] });
    const { onRemove } = mount(published());

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/Permits of this type: 3\. Rows in this published table: 12\./)).toBeTruthy();
    expect(within(dialog).getByText(/Table "Storage" is published and cannot be removed\./)).toBeTruthy();
    expect(removeButton().disabled).toBe(true);
    fireEvent.click(removeButton());
    expect(onRemove).not.toHaveBeenCalled();
  });

  it('таблиці немає серед опублікованих: видалення можна, сказано, що рядків у ній немає', async () => {
    usage.mockResolvedValue({ instanceCount: 3, tables: [{ code: 'Discharge', rowCount: 4 }] });
    const { onRemove } = mount(published());

    expect(await screen.findByText(/not published yet: none of the 3 permits of this type holds rows in it\./)).toBeTruthy();
    await waitFor(() => expect(removeButton().disabled).toBe(false));
    fireEvent.click(removeButton());
    expect(onRemove).toHaveBeenCalledWith('Storage');
  });

  it('запит не вдався: числа не показуються, збережену таблицю опублікованого типу не видалити', async () => {
    usage.mockRejectedValue(new Error('offline'));
    mount(published());

    expect(await screen.findByText(/Table "Storage" is published and cannot be removed\./)).toBeTruthy();
    expect(screen.queryByText(/Permits of this type/)).toBeNull();
    expect(removeButton().disabled).toBe(true);
  });
});
