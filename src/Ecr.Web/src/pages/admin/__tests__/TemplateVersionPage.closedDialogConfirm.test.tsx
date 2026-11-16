import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): сім форм конструктора версії шаблону (аркуш, таблиця,
 * колонка, поле шапки, рядок, формула, правило валідації) не шлють `PUT` удруге.
 *
 * Зонд на кожній (справжня сторінка й діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` мутації сторінки чекає перечитування структури (поля шапки — свого
 *   переліку) ВСЕРЕДИНІ мутації — `isPending` тримається, кнопка редактора `loading={saving}` → `disabled`;
 * - після закриття (`…Draft/…Edit = null`) тіло `{x !== null && <LocalDraft>…}` знімається в тому ж рендері —
 *   кнопка від'єднана. Ціль запиту (`tableId`, код) — знімок стану в мить кліку.
 * Вердикт: ЗАКРИТО для всіх семи; цей тест — регресія.
 *
 * ⚠ Monaco і перевірку виразу мокнуто за зразком `TemplateVersionPage.formulaReload.test.tsx`; фейкова Monaco
 * тримає значення в `<textarea>` з `ariaLabel` і на `input` кличе слухача змін компонента.
 */
vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();
  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [], formulas: [], arguments: [], headers: [] }),
    validateExpression: vi.fn().mockResolvedValue({ diagnostics: [], resultType: 'Number', skippedChecks: [] }),
  };
});

vi.mock('@/features/expressions/monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-template',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: (host: HTMLElement, options: { value: string; ariaLabel: string }) => {
      let value = options.value;
      let changed: () => void = () => {};
      const area = host.ownerDocument.createElement('textarea');
      area.setAttribute('aria-label', options.ariaLabel);
      area.value = options.value;
      area.addEventListener('input', () => {
        value = area.value;
        changed();
      });
      host.appendChild(area);

      return {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          area.value = next;
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          changed = handler;
        },
        dispose: () => {
          area.remove();
        },
      };
    },
  };
});

const V = '/api/v1/template-versions/1';
const Structure = `${V}/structure`;
const HeaderFields = `${V}/header-fields`;

const Column = {
  id: 42,
  code: 'COL',
  dataType: 'Decimal',
  displayFormat: null,
  headerL10n: { values: { en: 'Limit' } },
  isHidden: false,
  isReadOnly: false,
  isRequired: false,
  ordinal: 1,
  unitSymbol: null,
  formulaExpression: null,
  formulaDialect: null,
};

const Total = {
  ...Column,
  id: 43,
  code: 'TOTAL',
  dataType: 'Formula',
  headerL10n: { values: { en: 'Total' } },
  ordinal: 2,
  formulaExpression: 'A + B',
  formulaDialect: 'Template',
};

const Row = {
  rowKey: 'R1',
  ordinal: 1,
  rowKind: 'Item',
  label: 'First row',
  parentRowKey: null,
  isReadOnly: false,
  formulaExpression: null,
  formulaDialect: null,
};

const StructureDto = {
  isEditable: true,
  presentationRevision: 0,
  groupRules: [],
  templateVersionId: 1,
  sheets: [
    {
      id: 1,
      code: 'SHEET',
      nameL10n: { values: { en: 'Sheet' } },
      isMandatory: true,
      isVisible: true,
      ordinal: 1,
      sheetGroup: null,
      tables: [
        {
          id: 10,
          code: 'TBL',
          nameL10n: { values: { en: 'Table' } },
          layoutKind: 'Static',
          maxDynamicRows: null,
          ordinal: 1,
          rowMode: 'Fixed',
          rows: [Row],
          columns: [Column, Total],
        },
      ],
    },
  ],
};

const FullColumn = {
  id: 42,
  code: 'COL',
  headerL10n: { values: { en: 'Limit' } },
  ordinal: 1,
  dataType: 'Decimal',
  isRequired: false,
  isReadOnly: false,
  isHidden: false,
  precision: 18,
  scale: 4,
  defaultValue: null,
  displayFormat: null,
  styleId: null,
  lookupRegistryDefId: null,
  lookupFilter: null,
  unitId: null,
};

const HeaderField = {
  id: 1,
  code: 'Contractor',
  labelL10n: { values: { en: 'Contractor' } },
  ordinal: 0,
  dataType: 'String',
  isRequired: true,
  lookupRegistryDefId: null,
};

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 0,
      userName: 'test',
      language: 'en',
      permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
      isSimulation: false,
    };
  }
  if (url.endsWith('/api/v1/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }];
  if (url === Structure) return StructureDto;
  if (url === HeaderFields) return [HeaderField];
  if (url === `${V}/tables/10/columns/COL`) return FullColumn;
  if (url.endsWith('/versions')) {
    return {
      items: [{ id: 1, version: '1.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }],
      nextCursor: null,
      totalCount: null,
    };
  }
  return [];
}

interface Case {
  readonly name: string;
  readonly query: string;
  readonly trigger: string;
  readonly save: string;
  /** Адреса `PUT` (для формули — початок: сегмент області дає `formulaApi`). */
  readonly put: string;
  readonly reply: unknown;
  readonly refetch: string;
  readonly fill?: (dialog: HTMLElement) => Promise<void>;
}

const Cases: readonly Case[] = [
  {
    name: 'аркуш',
    query: '?node=s:SHEET',
    trigger: '⟦sheets.edit⟧',
    save: '⟦sheets.save⟧',
    put: `PUT ${V}/sheets/SHEET`,
    reply: StructureDto.sheets[0],
    refetch: Structure,
  },
  {
    name: 'таблиця',
    query: '',
    trigger: '⟦ctor.tableProperties⟧',
    save: '⟦tableDef.save⟧',
    put: `PUT ${V}/sheets/SHEET/tables/TBL`,
    reply: { id: 10, code: 'TBL' },
    refetch: Structure,
  },
  {
    name: 'колонка',
    query: '',
    trigger: '⟦columns.edit⟧',
    save: '⟦columns.save⟧',
    put: `PUT ${V}/tables/10/columns/COL`,
    reply: FullColumn,
    refetch: Structure,
  },
  {
    name: 'поле шапки',
    query: '?node=root',
    trigger: '⟦headerFields.edit⟧',
    save: '⟦headerFields.save⟧',
    put: `PUT ${V}/header-fields/Contractor`,
    reply: HeaderField,
    refetch: HeaderFields,
  },
  {
    name: 'рядок',
    query: '?tab=rows',
    trigger: '⟦rows.edit⟧',
    save: '⟦rows.save⟧',
    put: `PUT ${V}/tables/10/rows/R1`,
    reply: { ...Row, tableDefId: 10 },
    refetch: Structure,
  },
  {
    name: 'формула',
    query: '',
    trigger: '⟦formulas.edit⟧',
    save: '⟦formulas.save⟧',
    put: `PUT ${V}/tables/10/formulas/`,
    reply: {},
    refetch: Structure,
  },
  {
    name: 'правило валідації',
    query: '?tab=rules',
    trigger: '⟦ctor.addRule⟧',
    save: '⟦validationRules.save⟧',
    put: `PUT ${V}/tables/10/validation-rules/POSITIVE`,
    reply: { id: 7, code: 'POSITIVE' },
    refetch: Structure,
    fill: async (dialog) => {
      fireEvent.change(await within(dialog).findByLabelText(/validationRules\.code⟧/), { target: { value: 'POSITIVE' } });
      const expression = await within(dialog).findByRole('textbox', { name: '⟦validationRules.expression⟧' });
      fireEvent.input(expression, { target: { value: '[COL] >= 0' } });
      fireEvent.change(await within(dialog).findByLabelText(/validationRules\.message⟧ · English/), {
        target: { value: 'Must be positive' },
      });
    },
  },
];

/*
 * ⛔ Правило 386 / F13: «таблиця» плавала під навантаженням. Причина — холодний лінивий чанк редактора, який відкриває
 * САМЕ цей випадок (`TableEditor`; «аркуш» перед ним гріє лише `SheetEditor`), плюс робоча область таблиці: перше
 * `import()` під навантаженням з'їдало бюджет `findBy`. Прогрів усіх редакторів, які відкривають випадки, — до тестів.
 */
beforeAll(async () => {
  await Promise.all([
    import('@/features/templates/SheetEditor'),
    import('@/features/templates/TableEditor'),
    import('@/features/templates/ColumnEditor'),
    import('@/features/templates/HeaderFieldEditor'),
    import('@/features/templates/RowEditor'),
    import('@/features/templates/FormulaEditor'),
    import('@/features/templates/ValidationRuleEditor'),
    import('@/features/templates/ctor/TableWorkspace'),
  ]);
}, 60_000);

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: форми конструктора версії шаблону — один PUT на відкриття', () => {
  it.each(Cases)('$name: ні під час перечитування, ні після закриття другого PUT немає', async (probe) => {
    const server = holdWrites({ read, write: () => ({ body: probe.reply }) });
    const reads = holdReads((url) => url === probe.refetch);
    renderKeepMounted(
      <Routes>
        <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
      </Routes>,
      `/admin/templates/1/versions/1${probe.query}`,
    );

    const triggers = await screen.findAllByRole('button', { name: probe.trigger });
    fireEvent.click(triggers[0]!);
    const dialog = await screen.findByRole('dialog');
    await probe.fill?.(dialog);

    const save = await within(dialog).findByRole('button', { name: probe.save });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    const first = server.sent[0]!;
    expect(first.startsWith(probe.put)).toBe(true);

    // Сервер прийняв; мутація чекає перечитування — діалог ще відкритий, кнопка зайнята.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBeGreaterThan(0);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([first]);

    // Перечитано, діалог закрито: тіло знято разом із кнопкою.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([first]);
  });
});
