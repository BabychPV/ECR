import { useState, type JSX, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TemplateColumnDto, TemplateStructureDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { ColumnEditor } from '../ColumnEditor';
import { TemplateColumnUsage } from '../ColumnUsage';
import { PeriodAccessRuleEditor, PeriodAccessRuleManager } from '../PeriodAccessRuleEditor';
import { emptyPeriodAccessRuleDraft, type CreatePeriodAccessRuleDraft } from '../periodAccessRule';
import { PresentationEditor } from '../PresentationEditor';
import { VersionDiff } from '../VersionDiff';
import { AccessMatrix } from '../AccessMatrix';

/**
 * Четвертий раунд UX, лінія D — компоненти редактора шаблону:
 * R-07 (одиниця колонки), X-15 (прив'язки правила доступу вибором, окремий
 * `loading` і підтвердження видалення), R-08…R-10/X-16/X-18 (порівняння
 * версій), X-24 (Appearance після Cancel), R-12 (де використано в чернетці),
 * X-18 (матриця доступу — «Close»).
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧` (з параметрами — `⟦ключ (a=1)⟧`).
 */

function json(body: unknown, status = 200): Response {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

type Route = (url: string, method: string) => Response | undefined;

function serve(route: Route): string[] {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      urls.push(`${method} ${url}`);

      if (url.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      return route(url, method) ?? json([]);
    }),
  );

  return urls;
}

function withProviders(node: ReactNode): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>{node}</QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Structure: TemplateStructureDto = {
  templateVersionId: 1,
  presentationRevision: 0,
  isEditable: true,
  groupRules: [],
  sheets: [
    {
      id: 3,
      code: 'AIR',
      nameL10n: { values: { en: 'Air' } },
      ordinal: 1,
      sheetGroup: null,
      isMandatory: true,
      isVisible: true,
      tables: [
        {
          id: 30,
          code: 'EMIS',
          nameL10n: { values: { en: 'Emissions' } },
          ordinal: 1,
          layoutKind: 'MonthsInColumns',
          rowMode: 'Fixed',
          maxDynamicRows: null,
          rows: [],
          columns: [
            {
              id: 300,
              code: 'SUBST',
              headerL10n: { values: { en: 'Substance' } },
              dataType: 'Lookup',
              ordinal: 1,
              isReadOnly: false,
              isRequired: false,
              isHidden: false,
              displayFormat: null,
              unitSymbol: null,
              formulaExpression: null,
              formulaDialect: null,
            },
          ],
        },
      ],
    },
  ],
} as TemplateStructureDto;

describe('ColumnEditor: одиниця колонки (R-07)', () => {
  function Harness({ initial }: { initial: ColumnDraft }): JSX.Element {
    const [draft, setDraft] = useState(initial);

    return (
      <>
        <ColumnEditor
          draft={draft}
          disabled={false}
          saving={false}
          templateVersionId={1}
          onChange={setDraft}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
        <output aria-label="unit-id">{draft.unitId === null ? 'null' : String(draft.unitId)}</output>
      </>
    );
  }

  const units: Route = (url) =>
    url.endsWith('/api/v1/units')
      ? json([{ id: 12, code: 't', dimensionCode: 'Mass', dimensionId: 1, factorToBase: '1000', offsetToBase: '0' }])
      : undefined;

  it('числова колонка вибирає одиницю зі списку', async () => {
    serve(units);
    withProviders(<Harness initial={{ ...emptyColumnDraft(0), dataType: 'Decimal' }} />);

    fireEvent.click(await screen.findByLabelText(/columns\.unit⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 't (Mass)' }));

    expect(screen.getByLabelText('unit-id').textContent).toBe('12');
  });

  it('колонка типу Unit поля одиниці не має: одиниця в неї на рядок', async () => {
    const urls = serve(units);
    withProviders(<Harness initial={{ ...emptyColumnDraft(0), dataType: 'Unit' }} />);

    await screen.findByLabelText(/columns\.code⟧/);
    expect(screen.queryByLabelText(/columns\.unit⟧/)).toBeNull();
    expect(urls.some((u) => u.endsWith('/api/v1/units'))).toBe(false);
  });
});

describe('PeriodAccessRuleEditor: прив\'язки вибором, а не сирими id (X-15)', () => {
  function Harness(): JSX.Element {
    const [draft, setDraft] = useState<CreatePeriodAccessRuleDraft>(emptyPeriodAccessRuleDraft());

    return (
      <>
        <PeriodAccessRuleEditor
          draft={{ ...draft, ruleKind: 'SourceWindow' }}
          structure={Structure}
          roles={[{ id: 5, label: 'Reviewer' }]}
          disabled={false}
          saving={false}
          onChange={setDraft}
          onSubmit={() => {}}
        />
        <output aria-label="draft">{JSON.stringify(draft)}</output>
      </>
    );
  }

  it('аркуш, таблиця, роль і колонка-джерело — з назвами, а в чернетку лягають id', async () => {
    serve(() => undefined);
    withProviders(<Harness />);

    fireEvent.click(screen.getByLabelText(/periodRules\.sheet⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 'Air (AIR)' }));

    fireEvent.click(screen.getByLabelText(/periodRules\.table⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 'AIR · Emissions (EMIS)' }));

    fireEvent.click(screen.getByLabelText(/periodRules\.role⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 'Reviewer' }));

    fireEvent.click(screen.getByLabelText(/periodRules\.sourceColumn⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 'EMIS.SUBST — Substance' }));

    const draft = JSON.parse(screen.getByLabelText('draft').textContent ?? '{}') as CreatePeriodAccessRuleDraft;
    expect(draft).toMatchObject({ sheetDefId: 3, tableDefId: 30, roleId: 5, sourceColumnDefId: 300 });

    // ⛔ Жодного поля для сирого ідентифікатора аркуша чи таблиці.
    expect(screen.queryByLabelText(/periodRules\.sheetDefId⟧/)).toBeNull();
    expect(screen.queryByLabelText(/periodRules\.tableDefId⟧/)).toBeNull();
  });

  it('без переліку ролей лишається числове поле, а не глухий кут', () => {
    serve(() => undefined);
    withProviders(
      <PeriodAccessRuleEditor
        draft={emptyPeriodAccessRuleDraft()}
        structure={Structure}
        roles={null}
        disabled={false}
        saving={false}
        onChange={() => {}}
        onSubmit={() => {}}
      />,
    );

    expect(screen.getByLabelText(/periodRules\.roleId⟧/)).toBeDefined();
  });
});

describe('PeriodAccessRuleManager: окремий loading і підтвердження видалення (X-15, R-06)', () => {
  const draft = { onOutOfWindow: 'Warn', sheetDefId: 3, tableDefId: null, roleId: null, rowKind: null } as const;

  it('під час збереження крутиться лише «Save», а «Remove» не натискається', () => {
    serve(() => undefined);
    withProviders(
      <PeriodAccessRuleManager
        ruleId={9}
        draft={draft}
        structure={Structure}
        roles={null}
        disabled={false}
        saving
        deleting={false}
        onRuleIdChange={() => {}}
        onChange={() => {}}
        onSave={() => {}}
        onDelete={() => {}}
      />,
    );

    const save = screen.getByRole('button', { name: '⟦periodRules.save⟧' });
    const remove = screen.getByRole('button', { name: '⟦periodRules.delete⟧' });

    expect(save.getAttribute('data-loading')).toBe('true');
    expect(remove.getAttribute('data-loading')).toBeNull();
    expect((remove as HTMLButtonElement).disabled).toBe(true);
  });

  it('«Remove» спершу питає, і лише підтвердження кличе onDelete', async () => {
    serve(() => undefined);
    const onDelete = vi.fn();
    withProviders(
      <PeriodAccessRuleManager
        ruleId={9}
        draft={draft}
        structure={Structure}
        roles={null}
        disabled={false}
        saving={false}
        deleting={false}
        onRuleIdChange={() => {}}
        onChange={() => {}}
        onSave={() => {}}
        onDelete={onDelete}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '⟦periodRules.delete⟧' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('⟦periodRules.deleteTitle (id=9)⟧')).toBeDefined();
    expect(onDelete).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByTestId('confirm-verb'));
    expect(onDelete).toHaveBeenCalledTimes(1);
  });
});

describe('VersionDiff: версії свого шаблону, напрям, підписи, Close (R-08…R-10, X-16, X-18)', () => {
  const versions = [
    { id: 10, version: '1.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null },
    { id: 11, version: '2.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: 10, publishedAt: null },
  ] as const;

  it('друга версія — лише зі списку свого шаблону; один запит на вибір; напрям «від старшої»', async () => {
    const urls = serve((url) =>
      url.includes('/diff/')
        ? json({
            changes: [
              { elementPath: 'AIR.EMIS.NEW', kind: 'Added', changeClass: 'Safe', oldValue: null, newValue: 'NEW' },
            ],
            affectedDocumentCount: 37,
            fromVersionId: 10,
            toVersionId: 11,
          })
        : undefined,
    );
    withProviders(<VersionDiff templateVersionId={11} versions={versions} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦version.diff⟧' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.click(within(dialog).getByLabelText(/version\.diffOther⟧/));
    // ⛔ R-09: поточної версії серед варіантів немає, чужих — теж.
    expect(screen.queryByRole('option', { name: 'v2.0' })).toBeNull();
    fireEvent.click(await screen.findByRole('option', { name: 'v1.0' }));

    expect(await within(dialog).findByText('⟦version.diffDirection (from=1.0, to=2.0)⟧')).toBeDefined();
    expect(within(dialog).getByText('⟦version.diffAffectedHint (count=37)⟧')).toBeDefined();

    // ⛔ X-16: вид і клас — підписами каталогу, не сирими значеннями.
    expect(within(dialog).getByText('⟦enum.diffKind.Added⟧')).toBeDefined();
    expect(within(dialog).getByText('⟦enum.changeClass.Safe⟧')).toBeDefined();

    // ⛔ R-10: один запит, а не по одному на клавішу.
    expect(urls.filter((u) => u.includes('/diff/'))).toEqual(['GET /api/v1/template-versions/11/diff/10']);

    // ⛔ X-18: діалог лише для читання закривається «Close».
    expect(within(dialog).getByRole('button', { name: '⟦common.close⟧' })).toBeDefined();
    expect(within(dialog).queryByRole('button', { name: '⟦common.cancel⟧' })).toBeNull();
  });
});

describe('AccessMatrix: діалог лише для читання — «Close» (X-18)', () => {
  it('кнопка закриття підписана «Close», а не «Cancel»', async () => {
    serve((url) =>
      url.includes('/access-matrix') ? json({ templateVersionId: 1, periods: [], sheets: [] }) : undefined,
    );
    withProviders(<AccessMatrix templateVersionId={1} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦version.accessMatrix⟧' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByRole('button', { name: '⟦common.close⟧' })).toBeDefined();
    expect(within(dialog).queryByRole('button', { name: '⟦common.cancel⟧' })).toBeNull();
  });
});

describe('PresentationEditor: Cancel скидає незбережене введення (X-24)', () => {
  const column: TemplateColumnDto = {
    id: 42,
    code: 'COL',
    headerL10n: { values: { en: 'Limit' } },
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isHidden: false,
    displayFormat: 'N2',
    unitSymbol: null,
    formulaExpression: null,
    formulaDialect: null,
  };

  function Harness(): JSX.Element {
    const [editing, setEditing] = useState<TemplateColumnDto | null>(null);

    return (
      <>
        <button type="button" onClick={() => setEditing(column)}>
          open
        </button>
        <PresentationEditor templateVersionId={1} column={editing} onClose={() => setEditing(null)} />
      </>
    );
  }

  it('повторне відкриття після Cancel показує збережене, а не введене', async () => {
    serve(() => undefined);
    withProviders(<Harness />);

    fireEvent.click(screen.getByRole('button', { name: 'open' }));
    const format = await screen.findByLabelText(/version\.displayFormat⟧/);
    fireEvent.change(format, { target: { value: 'N4' } });

    fireEvent.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));
    await waitFor(() => expect(screen.queryByLabelText(/version\.displayFormat⟧/)).toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'open' }));
    const reopened = await screen.findByLabelText(/version\.displayFormat⟧/);

    expect((reopened as HTMLInputElement).value).toBe('N2');
  });
});

describe('TemplateColumnUsage: чернетка пояснює порожній перелік формул (R-12)', () => {
  it('у чернетці — пояснення, що формули з\'являться після публікації', async () => {
    serve((url) => (url.includes('/usage') ? json({ total: 0, items: [] }) : undefined));
    withProviders(<TemplateColumnUsage columnDefId={42} isDraft />);

    expect(await screen.findByText('⟦columns.usageDraftNote⟧')).toBeDefined();
  });

  it('в опублікованій версії пояснення немає', async () => {
    serve((url) => (url.includes('/usage') ? json({ total: 0, items: [] }) : undefined));
    withProviders(<TemplateColumnUsage columnDefId={42} />);

    await screen.findByText('⟦registries.usageNone⟧');
    expect(screen.queryByText('⟦columns.usageDraftNote⟧')).toBeNull();
  });
});
