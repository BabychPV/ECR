import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { RequiredInputCell } from '@/api/client';
import { mantineProviderProps } from '@/shared/theme/provider';
import { readSeedEnCatalog, readSeedSql } from '@/test/seedSql';
import {
  dedupeRequiredInputs,
  groupRequiredInputs,
  liveRequiredInputs,
  RequiredInputsBlockedAlert,
  RequiredInputsSummary,
  requiredInputLine,
} from '../requiredInputsAlert';

/**
 * ✎ 25.11, живі проходи (таблиця 3.00, документ 100 LAND_UI): над сіткою ріс розгорнутий банер «N required column(s)
 * missing» з десятками рядків-повторів (на кожну методологію й кожне збереження), кодами колонок, українською в
 * англійському інтерфейсі, без номерів рядків, і штовхав сітку вниз під час введення.
 *
 * Тексти — СПРАВЖНІЙ каталог із сіду (`REQIN-required-inputs-banner.sql`) мовою, яку вибрав тест: `⟦key⟧` означав би
 * ключ без рядка в сіді.
 */

type Lang = 'en' | 'ru' | 'kz';
const catalogs: Record<Lang, Record<string, string>> = { en: {}, ru: {}, kz: {} };
const rowPattern = /\(\s*N'([^']+)'\s*,\s*N'(en|ru|kz)'\s*,\s*N'((?:[^']|'')*)'/g;
for (const text of [readSeedEnCatalog(), readSeedSql()]) {
  for (const match of text.matchAll(rowPattern)) {
    const [, key, lang, value] = match;
    if (key !== undefined && value !== undefined) catalogs[lang as Lang][key] = value.replace(/''/g, "'");
  }
}

let current: Lang = 'en';
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (catalogs[current][key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(() => {
  cleanup();
  current = 'en';
});

const Columns = [
  { code: 'Land_Status', header: 'Status' },
  { code: 'Land_TypeEquipment', header: 'Type of equipment' },
  { code: 'Land_TypeFuel', header: 'Type of fuel' },
];
const Rows = [
  { rowKey: 'r-a', label: null },
  { rowKey: 'r-b', label: null },
  { rowKey: 'r-new', label: null },
];

const ukrainian = (column: string, methodology: string): string =>
  `Колонка «${column}» обов'язкова для методології «${methodology}».`;

/** Те, що сітка накопичувала з відповідей ДО фіксу: по повідомленню на (рядок, колонка, методологія), з повторами. */
function legacyMessages(rowKey: string): RequiredInputCell[] {
  return ['ECW_C05_01', 'ECW_C05_02', 'ECW_C05_03', 'ECW_C05_04'].flatMap((methodology) =>
    ['Land_TypeEquipment', 'Land_TypeFuel'].map((columnCode) => ({
      rowKey,
      columnCode,
      ruleCode: 'ECR-CALC-0437',
      message: ukrainian(columnCode, methodology),
      methodologies: [methodology],
    })),
  );
}

function shell(node: JSX.Element, scheme: 'light' | 'dark' = 'light'): ReturnType<typeof render> {
  return render(
    <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
      {node}
    </MantineProvider>,
  );
}

describe('groupRequiredInputs / dedupe / live', () => {
  it('10 повідомлень (2 колонки × 4 методології + повтори) → 2 рядки переліку, лічильник = довжина переліку', () => {
    const cells = [...legacyMessages('r-new'), ...legacyMessages('r-new').slice(0, 2)];
    const groups = groupRequiredInputs(dedupeRequiredInputs(cells), Columns, Rows);

    expect(groups.map((g) => g.columnCode)).toEqual(['Land_TypeEquipment', 'Land_TypeFuel']);
    expect(groups[0]?.methodologies).toEqual(['ECW_C05_01', 'ECW_C05_02', 'ECW_C05_03', 'ECW_C05_04']);
    expect(groups[0]?.rows).toEqual(['3']);
  });

  it('назва колонки — заголовок зрізу, рядки — номером у зрізі, без кодів і без кирилиці', () => {
    const groups = groupRequiredInputs(
      dedupeRequiredInputs([...legacyMessages('r-b'), ...legacyMessages('r-a')]),
      Columns,
      Rows,
    );
    const lines = groups.map(requiredInputLine);

    expect(lines).toEqual([
      'Type of equipment · rows: 1, 2 · needed by ECW_C05_01, ECW_C05_02, ECW_C05_03, ECW_C05_04',
      'Type of fuel · rows: 1, 2 · needed by ECW_C05_01, ECW_C05_02, ECW_C05_03, ECW_C05_04',
    ]);
    expect(lines.join(' ')).not.toMatch(/Land_|[Ѐ-ӿ]/);
  });

  it('з поточних даних: заповнена комірка і рядок, якого вже немає, випадають', () => {
    const cells = dedupeRequiredInputs([...legacyMessages('r-a'), ...legacyMessages('gone')]);
    const live = liveRequiredInputs(cells, [
      { __rowKey: 'r-a', Land_TypeFuel: 36, Land_TypeEquipment: '' },
      { __rowKey: 'r-b' },
    ]);

    expect(live.map((c) => `${c.rowKey}:${c.columnCode}`)).toEqual(['r-a:Land_TypeEquipment']);
    expect(liveRequiredInputs(cells, null)).toHaveLength(cells.length);
  });
});

describe('RequiredInputsSummary: Warn згорнутий до одного рядка', () => {
  it('за замовчуванням згорнутий; розкривається на вимогу; лічильник = кількість рядків', async () => {
    shell(<RequiredInputsSummary cells={legacyMessages('r-new')} columns={Columns} rows={Rows} />);

    const toggle = screen.getByRole('button', { name: /2 required column\(s\) not filled yet/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('list')).toBeNull();
    // Нічого не розгорнуто над сіткою: ні alert, ні переліку.
    expect(screen.queryByRole('alert')).toBeNull();

    await userEvent.setup().click(toggle);

    const list = await screen.findByRole('list');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(list.textContent).toContain('Type of fuel · rows: 3 · needed by ECW_C05_01, ECW_C05_02');
    expect(document.body.textContent).not.toContain('⟦');
  });

  it('без вимог — нічого', () => {
    shell(<RequiredInputsSummary cells={[]} columns={Columns} rows={Rows} />);
    expect(screen.queryByRole('button')).toBeNull();
    expect(document.querySelector('[data-required-inputs]')).toBeNull();
  });

  it('мова інтерфейсу: російський каталог — російський текст, без української', async () => {
    current = 'ru';
    shell(<RequiredInputsSummary cells={legacyMessages('r-new')} columns={Columns} rows={Rows} />);

    const toggle = screen.getByRole('button', { name: /Не заполнено обязательных столбцов: 2/ });
    await userEvent.setup().click(toggle);
    const list = await screen.findByRole('list');
    expect(list.textContent).toContain('Type of fuel · строки: 3 · требуется для ECW_C05_01');
    expect(document.body.textContent).not.toMatch(/обов'язкова|⟦/);
  });

  it('kz: ключі є в сіді', () => {
    current = 'kz';
    shell(<RequiredInputsSummary cells={legacyMessages('r-new')} columns={Columns} rows={Rows} />);
    expect(screen.getByRole('button').textContent).toContain('2 міндетті баған толтырылмаған');
  });
});

describe('RequiredInputsBlockedAlert: відмова — розгорнута, теж без повторів', () => {
  it('заголовок і перелік — однієї довжини', async () => {
    shell(<RequiredInputsBlockedAlert cells={[...legacyMessages('r-a'), ...legacyMessages('r-a')]} columns={Columns} rows={Rows} />);

    const alert = screen.getByRole('alert');
    await waitFor(() => expect(within(alert).getAllByRole('listitem')).toHaveLength(2));
    expect(alert.textContent).toContain('Cannot save: 2 required column(s) missing');
  });
});
