import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EcrApiError } from '@/api/client';
import { ConditionalFormatPanel } from '../ConditionalFormatPanel';
import type { ConditionalFormatRuleDto, ConditionalFormatSet } from '../conditionalFormatApi';
import { renderWithQuery } from '@/test/render';

/**
 * Редактор умовного форматування (`ФВ-2.7`): читання, збереження з `If-Match`,
 * конфлікт і перегляд.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30):
 *  - у `mutationFn` не додавати `base.others` — червоніє «правила інших
 *    таблиць їдуть назад»;
 *  - брати `etag` не з `seed` — червоніє «If-Match — версія читання», а після
 *    конфлікту — «наступне збереження несе свіжу версію»;
 *  - у `onError` перезаписати чернетку свіжим набором — червоніє «чернетка
 *    лишається»;
 *  - прибрати `incomplete` з `disabled` кнопки — червоніє «неповне правило»;
 *  - у перегляді замість `firstMatchingRule` поставити `null` — червоніє
 *    «перегляд застосовує правило».
 */
const api = vi.hoisted(() => ({
  getConditionalFormats: vi.fn(),
  saveConditionalFormats: vi.fn(),
}));
vi.mock('../conditionalFormatApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../conditionalFormatApi')>()),
  ...api,
}));

const notify = vi.hoisted(() => ({ showApiError: vi.fn(), showDone: vi.fn() }));
vi.mock('@/shared/ui/notify', () => notify);

const columns = [
  { code: 'Q', label: 'Quantity' },
  { code: 'N', label: 'Note' },
];

const own: ConditionalFormatRuleDto = {
  columnCode: 'Q',
  operator: 'gt',
  value: '100',
  valueTo: null,
  backgroundHex: '#ff0000',
  foregroundHex: null,
  isBold: false,
};

/** Правило колонки ІНШОЇ таблиці тієї самої версії. */
const foreign: ConditionalFormatRuleDto = {
  columnCode: 'OTHER',
  operator: 'lt',
  value: '0',
  valueTo: null,
  backgroundHex: null,
  foregroundHex: '#0000ff',
  isBold: true,
};

function set(rules: ConditionalFormatRuleDto[], etag: string): ConditionalFormatSet {
  return { rules, etag };
}

function stale(): EcrApiError {
  return new EcrApiError({
    title: 't',
    status: 409,
    errorCode: 'ECR-TMPL-0409',
    correlationId: 'c',
    extensions2: { version: 'FRESH' },
  });
}

function renderPanel(canEdit = true) {
  return renderWithQuery(<ConditionalFormatPanel templateVersionId={7} columns={columns} canEdit={canEdit} />);
}

const saveButton = () => screen.getByRole('button', { name: /conditionalFormat\.save/ });

describe('ConditionalFormatPanel', () => {
  beforeEach(() => {
    api.getConditionalFormats.mockReset();
    api.saveConditionalFormats.mockReset();
    notify.showApiError.mockReset();
    notify.showDone.mockReset();
    api.getConditionalFormats.mockResolvedValue(set([own, foreign], '"V1"'));
    api.saveConditionalFormats.mockImplementation((_id: number, rules: ConditionalFormatRuleDto[]) =>
      Promise.resolve(set(rules, '"V2"')),
    );
  });

  it('показує правила своєї таблиці; чужі — ні', async () => {
    renderPanel();

    await waitFor(() => expect(screen.getAllByRole('group', { name: /conditionalFormat\.rule/ })).toHaveLength(1));
    expect(api.getConditionalFormats).toHaveBeenCalledWith(7);
    expect((screen.getByRole('textbox', { name: /conditionalFormat\.value(?!To)/ }) as HTMLInputElement).value).toBe(
      '100',
    );
  });

  it('незмінений набір не зберігається; змінений — з If-Match версії читання, і правила інших таблиць їдуть назад', async () => {
    renderPanel();
    const user = userEvent.setup();
    const value = await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    expect((saveButton() as HTMLButtonElement).disabled).toBe(true);

    await user.clear(value);
    await user.type(value, '200');
    await user.click(saveButton());

    await waitFor(() => expect(api.saveConditionalFormats).toHaveBeenCalledTimes(1));
    const [id, rules, etag] = api.saveConditionalFormats.mock.calls[0] as [number, ConditionalFormatRuleDto[], string];
    expect(id).toBe(7);
    expect(etag).toBe('"V1"');
    expect(rules).toEqual([foreign, { ...own, value: '200' }]);
    await waitFor(() => expect(notify.showDone).toHaveBeenCalledWith(expect.stringMatching(/conditionalFormat\.saved/)));

    // Збережене — нова точка відліку: зберігати знову нічого.
    await waitFor(() => expect((saveButton() as HTMLButtonElement).disabled).toBe(true));
  });

  it('неповне правило блокує збереження й пояснює чому', async () => {
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.add/ }));

    expect((saveButton() as HTMLButtonElement).disabled).toBe(true);
    expect(saveButton().getAttribute('aria-describedby')).toBe('conditional-format-save-hint');
    expect(screen.getByText(/conditionalFormat\.incomplete/)).toBeTruthy();
  });

  it('конфлікт 409: чернетка лишається, пояснення видно, наступне збереження несе свіжу версію', async () => {
    renderPanel();
    const user = userEvent.setup();
    const value = await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    const theirs = { ...own, value: '5', backgroundHex: '#00ff00' };
    api.saveConditionalFormats.mockRejectedValueOnce(stale());
    api.getConditionalFormats.mockResolvedValue(set([theirs, foreign], '"V9"'));

    await user.clear(value);
    await user.type(value, '200');
    await user.click(saveButton());

    await screen.findByTestId('conditional-format-conflict');
    expect(notify.showApiError).toHaveBeenCalledTimes(1);
    expect((screen.getByRole('textbox', { name: /conditionalFormat\.value(?!To)/ }) as HTMLInputElement).value).toBe(
      '200',
    );

    await user.click(saveButton());
    await waitFor(() => expect(api.saveConditionalFormats).toHaveBeenCalledTimes(2));
    expect(api.saveConditionalFormats.mock.calls[1]?.[2]).toBe('"V9"');
  });

  it('L9-26: відмова перечитування після 409 — ErrorAlert поруч, редактор із чернеткою лишається', async () => {
    renderPanel();
    const user = userEvent.setup();
    const value = await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    api.saveConditionalFormats.mockRejectedValueOnce(stale());
    api.getConditionalFormats.mockRejectedValue(
      new EcrApiError({ title: 't', status: 503, errorCode: 'ECR-SYS-0503', correlationId: 'c' }),
    );

    await user.clear(value);
    await user.type(value, '200');
    await user.click(saveButton());

    await waitFor(() => expect(screen.getByText(/ECR-SYS-0503/)).toBeDefined());
    // ⛔ Редактор не підмінено: чернетка на місці й її можна зберегти ще раз.
    expect((screen.getByRole('textbox', { name: /conditionalFormat\.value(?!To)/ }) as HTMLInputElement).value).toBe(
      '200',
    );
    expect((saveButton() as HTMLButtonElement).disabled).toBe(false);
  });

  it('«Відкинути мої зміни» після конфлікту показує чужі правила', async () => {
    renderPanel();
    const user = userEvent.setup();
    const value = await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    api.saveConditionalFormats.mockRejectedValueOnce(stale());
    api.getConditionalFormats.mockResolvedValue(set([{ ...own, value: '5' }], '"V9"'));

    await user.clear(value);
    await user.type(value, '200');
    await user.click(saveButton());
    await screen.findByTestId('conditional-format-conflict');

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.discard/ }));

    expect((screen.getByRole('textbox', { name: /conditionalFormat\.value(?!To)/ }) as HTMLInputElement).value).toBe(
      '5',
    );
    expect(screen.queryByTestId('conditional-format-conflict')).toBeNull();
  });

  it('без права або в опублікованій версії — лише перегляд', async () => {
    renderPanel(false);

    const value = await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });
    expect((value as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByTestId('conditional-format-read-only')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /conditionalFormat\.save/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /conditionalFormat\.add/ })).toBeNull();
  });

  it('перегляд застосовує правило до значення-прикладу', async () => {
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    const preview = document.querySelector('[data-conditional-preview]');
    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }), '150');
    expect(preview?.getAttribute('data-conditional-preview')).toBe('match');

    await user.clear(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }));
    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }), '50');
    expect(preview?.getAttribute('data-conditional-preview')).toBe('none');
  });

  it('L9-24: приклад малюється тим самим cellLook, що й перегляд таблиці: нечитабельний текст автора — колір теми', async () => {
    // Білий текст на білій заливці: сітка (`cellAppearance.ts`) і `TablePreview` такого кольору не
    // покажуть — замінять кольором тексту теми з кращим контрастом.
    api.getConditionalFormats.mockResolvedValue(
      set([{ ...own, backgroundHex: '#ffffff', foregroundHex: '#fefefe', isBold: true }], '"V1"'),
    );
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }), '150');
    const preview = document.querySelector<HTMLElement>('[data-conditional-preview="match"]');

    expect(preview?.style.backgroundColor).toBe('rgb(255, 255, 255)');
    expect(preview?.style.color).toBe('rgb(0, 0, 0)');
    expect(preview?.style.fontWeight).toBe('bold');
  });

  it('правило додається й видаляється', async () => {
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('textbox', { name: /conditionalFormat\.value(?!To)/ });

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.add/ }));
    expect(screen.getAllByRole('group', { name: /conditionalFormat\.rule/ })).toHaveLength(2);

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.remove.*1/ }));
    expect(screen.getAllByRole('group', { name: /conditionalFormat\.rule/ })).toHaveLength(1);
  });
});

/**
 * Клавіатура (WCAG 2.4.3): «Додати правило» — фокус у новому правилі; «Прибрати» — на «Додати».
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `focus.added()` → червоний «додати»;
 * прибрати `focus.removed()` → червоний «прибрати».
 */
describe('ConditionalFormatPanel — фокус', () => {
  it('додати: фокус у першому полі нового правила', async () => {
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('group', { name: /conditionalFormat\.rule/ });

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.add/ }));

    const second = screen.getByRole('group', { name: /conditionalFormat\.rule.*2/ });
    expect(second.contains(document.activeElement)).toBe(true);
  });

  it('прибрати: фокус на «Додати правило», а не на <body>', async () => {
    renderPanel();
    const user = userEvent.setup();
    await screen.findByRole('group', { name: /conditionalFormat\.rule/ });

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.remove/ }));

    expect(document.activeElement).toBe(screen.getByRole('button', { name: /conditionalFormat\.add/ }));
  });
});

