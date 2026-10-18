import { useState, type JSX, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { TestProviders } from '@/test/render';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import type { PermitTypeColumn } from '../../api/types';
import { ChoicesFrom, choicesFromText } from '../ChoicesFrom';
import { ColumnInspector } from '../ColumnInspector';
import { normalizeColumn, newColumn, withListScope } from '../draftModel';
import type { DraftProblem } from '../draftValidate';
import { column, permitType } from './fixtures';

/**
 * Інспектор стовпця та «Choices come from» (PM-08b). Тексти — справжні, із `09-seed.sql`.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `ChoicesFrom` залишити `onChange({ …, choices })` без
 * локального `text` (значення з `column.choices.join`) валить «Enter у списку не губиться»; у
 * `ColumnInspector` `disabled={!editable || column.key}` → `disabled={!editable}` валить «ключовий
 * стовпець: Required вимкнений».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

const fetchMock = vi.mocked(apiFetch);
const units = [
  { id: 7, code: 't/yr' },
  { id: 8, code: 'mg/l' },
];
const registries = [
  { id: 1, code: 'Substances', nameL10n: { values: { en: 'Substances' } } },
  { id: 2, code: 'WaterBodies', nameL10n: { values: { en: 'Water bodies' } } },
];

beforeEach(() => {
  fetchMock.mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? units : path === '/api/v1/registries' ? registries : []) as never,
  );
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const tables = permitType().tables;
const storage = tables[1]!;

function Providers({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </TestProviders>
  );
}

/** Держить стовпець у стані, як це робить копія: кожна правка проходить нормалізацію моделі. */
function Host({
  initial,
  editable = true,
  problems = [],
  onSeen,
}: {
  initial: PermitTypeColumn;
  editable?: boolean;
  problems?: DraftProblem[];
  onSeen?: (c: PermitTypeColumn) => void;
}): JSX.Element {
  const [current, setCurrent] = useState(initial);
  return (
    <ColumnInspector
      table={storage}
      column={current}
      tables={tables}
      editable={editable}
      problems={problems}
      onChange={(next) => {
        const normalized = normalizeColumn(next);
        setCurrent(normalized);
        onSeen?.(normalized);
      }}
      onClose={() => undefined}
    />
  );
}

const show = (node: ReactNode): void => {
  render(<Providers>{node}</Providers>);
};

describe('ColumnInspector: поля', () => {
  it('назви, код лише для читання, тип, перемикачі', () => {
    show(<Host initial={column('Place', { key: true, required: true, nameL10n: { en: 'Place', ru: 'Место' } })} />);
    expect(screen.getByRole('heading', { name: 'Place' })).toBeTruthy();
    expect((screen.getByLabelText(/Name \(English\)/) as HTMLInputElement).value).toBe('Place');
    expect((screen.getByLabelText('Name (Russian)') as HTMLInputElement).value).toBe('Место');
    const code = screen.getByLabelText('Code') as HTMLInputElement;
    expect(code.value).toBe('Place');
    expect(code.readOnly).toBe(true);
    expect((screen.getByRole('textbox', { name: 'Type' }) as HTMLInputElement).value).toBe('Text');
    expect((screen.getByRole('switch', { name: /unique key of the row/ }) as HTMLInputElement).checked).toBe(true);
  });

  it('ключовий стовпець: Required вимкнений і ввімкнений, з поясненням', () => {
    show(<Host initial={column('Place', { key: true, required: true })} />);
    const required = screen.getByRole('switch', { name: /^Required/ }) as HTMLInputElement;
    expect(required.checked).toBe(true);
    expect(required.disabled).toBe(true);
    expect(screen.getByText('A key column is always required.')).toBeTruthy();
  });

  it('позначили ключем — Required стає ввімкненим і вимкненим', async () => {
    const user = userEvent.setup();
    show(<Host initial={column('Place', { required: false })} />);
    expect((screen.getByRole('switch', { name: /^Required/ }) as HTMLInputElement).disabled).toBe(false);
    await user.click(screen.getByRole('switch', { name: /unique key of the row/ }));
    const required = screen.getByRole('switch', { name: /^Required/ }) as HTMLInputElement;
    expect(required.checked).toBe(true);
    expect(required.disabled).toBe(true);
  });

  it('правка англійської назви доходить до onChange; очищення російської прибирає мову', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    show(<Host initial={column('Place', { nameL10n: { en: 'Pla', ru: 'М' } })} onSeen={(c) => seen.push(c)} />);
    await user.type(screen.getByLabelText(/Name \(English\)/), 'ce');
    expect(seen.at(-1)!.nameL10n).toEqual({ en: 'Place', ru: 'М' });
    await user.clear(screen.getByLabelText('Name (Russian)'));
    expect(seen.at(-1)!.nameL10n).toEqual({ en: 'Place' });
  });

  it('одиниця лише для чисел; вибір дає unitId', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    show(<Host initial={column('Annual', { dataType: 'Decimal' })} onSeen={(c) => seen.push(c)} />);
    await user.click(await screen.findByRole('textbox', { name: 'Unit' }));
    await user.click(await screen.findByRole('option', { name: 't/yr' }));
    expect(seen.at(-1)!.unitId).toBe(7);
    cleanup();
    show(<Host initial={column('Note', { dataType: 'String' })} />);
    expect(screen.queryByRole('textbox', { name: 'Unit' })).toBeNull();
  });

  it('група: недопустимий код — помилка поля', () => {
    show(<Host initial={column('Annual', { group: '1 bad' })} />);
    expect(screen.getByText('Use Latin letters, digits and underscore, starting with a letter.')).toBeTruthy();
  });

  it('вади цього стовпця показуються, чужі — ні', () => {
    const problems: DraftProblem[] = [
      { messageKey: 'err.ECR-PRM-0422.typeOnlyNoChoices', params: { columnCode: 'Place' }, tableCode: 'Storage', columnCode: 'Place' },
      { messageKey: 'err.ECR-PRM-0422.typeOnlyNoChoices', params: { columnCode: 'Other' }, tableCode: 'Storage', columnCode: 'Other' },
    ];
    show(<Host initial={column('Place')} problems={problems} />);
    expect(screen.getAllByText(/takes values from a list for this type only/)).toHaveLength(1);
  });

  it('опублікована версія: поля вимкнені, є пояснення', () => {
    show(<Host initial={column('Place', { key: true })} editable={false} />);
    expect(screen.getByText(/This version is published/)).toBeTruthy();
    expect((screen.getByLabelText(/Name \(English\)/) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('switch', { name: /unique key of the row/ }) as HTMLInputElement).disabled).toBe(true);
  });

  it('зміна типу на «Pick from a list» ставить спільний реєстр і показує блок джерела', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    show(<Host initial={column('Place')} onSeen={(c) => seen.push(c)} />);
    await user.click(screen.getByRole('textbox', { name: 'Type' }));
    await user.click(await screen.findByRole('option', { name: 'Pick from a list' }));
    expect(seen.at(-1)).toMatchObject({ dataType: 'Lookup', listScope: 'SharedRegistry' });
    expect(await screen.findByRole('textbox', { name: 'Choices come from' })).toBeTruthy();
  });
});

describe('ChoicesFrom: три області', () => {
  const lookup = (over: Partial<PermitTypeColumn>): PermitTypeColumn => ({ ...newColumn('Pick', { en: 'Pick' }, 'Lookup'), ...over });

  function Stateful({ initial, others = [storage] }: { initial: PermitTypeColumn; others?: typeof tables }): JSX.Element {
    const [current, setCurrent] = useState(initial);
    return <ChoicesFrom column={current} otherTables={others} editable onChange={(next) => setCurrent(normalizeColumn(next))} />;
  }

  it('спільний реєстр: вибраний показується, зі списку можна обрати інший', async () => {
    const user = userEvent.setup();
    show(<Stateful initial={lookup({ listScope: 'SharedRegistry', listRegistryCode: 'Substances' })} />);
    const registry = await screen.findByRole('textbox', { name: 'Registry' });
    await waitFor(() => expect((registry as HTMLInputElement).value).toBe('Substances (Substances)'));
    await user.click(registry);
    await user.click(await screen.findByRole('option', { name: 'Water bodies (WaterBodies)' }));
    expect((screen.getByRole('textbox', { name: 'Registry' }) as HTMLInputElement).value).toBe('Water bodies (WaterBodies)');
  });

  it('перемикання на «список лише для типу»: textarea; Enter не губиться, порожні рядки відкинуто у значеннях', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    function Watch(): JSX.Element {
      const [current, setCurrent] = useState(lookup({ listScope: 'SharedRegistry', listRegistryCode: 'Substances' }));
      return (
        <ChoicesFrom
          column={current}
          otherTables={[storage]}
          editable
          onChange={(next) => {
            const n = normalizeColumn(next);
            seen.push(n);
            setCurrent(n);
          }}
        />
      );
    }
    show(<Watch />);
    await user.click(await screen.findByRole('textbox', { name: 'Choices come from' }));
    await user.click(await screen.findByRole('option', { name: 'A list only for this permit type' }));
    const area = (await screen.findByLabelText('Values, one per line')) as HTMLTextAreaElement;
    expect(seen.at(-1)).toMatchObject({ listScope: 'TypeOnly', listRegistryCode: null, choices: [] });
    await user.type(area, 'Cooling{Enter}{Enter}Process{Enter}');
    expect(area.value).toBe('Cooling\n\nProcess\n');
    expect(seen.at(-1)!.choices).toEqual(['Cooling', 'Process']);
  });

  it('таблиця того самого дозволу: вибір з інших таблиць', async () => {
    const user = userEvent.setup();
    show(<Stateful initial={withListScope(lookup({}), 'permit', ['Storage'])} others={[storage, tables[0]!]} />);
    const select = await screen.findByRole('textbox', { name: 'Table of this permit' });
    expect((select as HTMLInputElement).value).toBe('Storage');
    await user.click(select);
    await user.click(await screen.findByRole('option', { name: 'Discharge' }));
    expect((screen.getByRole('textbox', { name: 'Table of this permit' }) as HTMLInputElement).value).toBe('Discharge');
  });

  it('інших таблиць немає — пояснення замість порожнього вибору', async () => {
    show(<Stateful initial={withListScope(lookup({}), 'permit', [])} others={[]} />);
    expect(await screen.findByText(/Add another table first/)).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'Table of this permit' })).toBeNull();
  });

  it('перехід permit → shared лишає лише реєстр', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    function Watch(): JSX.Element {
      const [current, setCurrent] = useState(withListScope(lookup({}), 'permit', ['Storage']));
      return <ChoicesFrom column={current} otherTables={[storage]} editable onChange={(n) => { const x = normalizeColumn(n); seen.push(x); setCurrent(x); }} />;
    }
    show(<Watch />);
    await user.click(await screen.findByRole('textbox', { name: 'Choices come from' }));
    await user.click(await screen.findByRole('option', { name: 'A shared registry (used by many types)' }));
    expect(seen.at(-1)).toMatchObject({ listScope: 'SharedRegistry', siblingTableCode: null, choices: null });
  });

  it('вимкнений режим: жодне поле не редагується', async () => {
    show(<ChoicesFrom column={lookup({ listScope: 'TypeOnly', choices: ['a'] })} otherTables={[]} editable={false} onChange={() => undefined} />);
    expect((await screen.findByLabelText('Values, one per line') as HTMLTextAreaElement).disabled).toBe(true);
    expect(within(screen.getByRole('textbox', { name: 'Choices come from' }).parentElement!).getByRole('textbox')).toHaveProperty('disabled', true);
  });
});

describe('choicesFromText', () => {
  it('рядки обрізаються, порожні відкидаються, CRLF розуміється', () => {
    expect(choicesFromText(' a \r\n\r\n b\n')).toEqual(['a', 'b']);
    expect(choicesFromText('')).toEqual([]);
  });
});
