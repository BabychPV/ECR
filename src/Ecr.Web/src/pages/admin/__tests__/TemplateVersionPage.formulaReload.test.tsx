import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Дефект живого перегляду (2026-09-23): кнопка "Formula" на колонці чи рядку,
 * де формула вже збережена, при ПОВТОРНОМУ відкритті показувала ПОРОЖНІЙ
 * редактор — `TemplateVersionPage.tsx` завжди будувала
 * `emptyFormulaDraft(table.id, 'Column'|'Row', target)`, ніколи не питаючи,
 * чи формула вже є. Формула справді зберігалася на сервері (`PUT` працює) —
 * проблема лише клієнтська: `GET …/structure` тепер несе
 * `formulaExpression`/`formulaDialect` на кожній колонці й рядку
 * (`GetTemplateStructureHandler.cs`), і кнопка має читати їх ПЕРЕД тим, як
 * будувати чернетку.
 *
 * ⛔ Мутаційний доказ: на невиправленому коді (`onClick` завжди
 * `emptyFormulaDraft`, без перевірки `column.formulaExpression`/
 * `row.formulaExpression`) textarea редактора формули показує порожній
 * рядок замість збереженого виразу — перевірено вручну поверненням старого
 * `onClick` (RED), фікс повертає GREEN.
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
};

function structureDto(): unknown {
  return {
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
            rows: [
              {
                rowKey: 'R1',
                ordinal: 1,
                rowKind: 'Item',
                label: 'Row with formula',
                parentRowKey: null,
                isReadOnly: false,
                formulaExpression: '[C1] + [C2]',
                formulaDialect: 'Template',
              },
              {
                rowKey: 'R2',
                ordinal: 2,
                rowKind: 'Item',
                label: 'Row without formula',
                parentRowKey: null,
                isReadOnly: false,
                formulaExpression: null,
                formulaDialect: null,
              },
            ],
            columns: [
              {
                // Колонка, на якій формула ВЖЕ збережена — головний випадок.
                id: 42,
                code: 'TOTAL',
                dataType: 'Formula',
                displayFormat: null,
                headerL10n: { values: { en: 'Total' } },
                isHidden: false,
                isReadOnly: false,
                isRequired: false,
                ordinal: 1,
                unitSymbol: null,
                formulaExpression: 'A + B',
                formulaDialect: 'Template',
              },
              {
                // Колонка типу Formula БЕЗ формули — регресія не має ламати
                // цей випадок: редактор має відкритися порожнім.
                id: 43,
                code: 'EMPTY',
                dataType: 'Formula',
                displayFormat: null,
                headerL10n: { values: { en: 'Empty' } },
                isHidden: false,
                isReadOnly: false,
                isRequired: false,
                ordinal: 2,
                unitSymbol: null,
                formulaExpression: null,
                formulaDialect: null,
              },
            ],
          },
        ],
      },
    ],
  };
}

function versionsPage(): unknown {
  return {
    items: [
      {
        id: 1,
        version: '1.0.0.0',
        status: 'Draft',
        presentationRevision: 0,
        clonedFromVersionId: null,
        publishedAt: null,
      },
    ],
    nextCursor: null,
    totalCount: null,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
          isSimulation: false,
        });
      }
      if (url.includes('/structure')) {
        return json(structureDto());
      }
      if (url.includes('/versions?limit=')) {
        return json(versionsPage());
      }

      return json([]);
    }),
  );
}

// ⛔ `ExpressionEditor` усередині `FormulaEditor` тягне за собою Monaco й
// живий `POST /expressions/validate` — той самий обхід, що й
// `FormulaEditor.placementIdentity.test.tsx`. Фейкова Monaco тримає значення
// поля в `<textarea>`, підписаній `ariaLabel`, і саме її текст — головне
// твердження цього тесту.
vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();
  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({
      functions: [],
      constants: [],
      formulas: [],
      arguments: [],
      headers: [],
    }),
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

      const area = host.ownerDocument.createElement('textarea');
      area.setAttribute('aria-label', options.ariaLabel);
      area.value = options.value;
      host.appendChild(area);

      return {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          area.value = next;
        },
        getModel: () => model,
        onDidChangeModelContent: () => {},
        dispose: () => {
          area.remove();
        },
      };
    },
  };
});

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

/** Аркуш згорнутий за замовчуванням (Mantine `Accordion`) — розгорнути. */
async function openSheet(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
}

function renderPage(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('TemplateVersionPage: кнопка "Formula" підвантажує наявний вираз при повторному відкритті', () => {
  it('колонка зі збереженою формулою — редактор показує ЧИННИЙ вираз, а не порожній', async () => {
    stubFetch();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    // Перша кнопка "Formula" в DOM — колонка `TOTAL` (id 42, вираз "A + B").
    const formulaButtons = await screen.findAllByRole('button', { name: /formulas\.edit⟧/ });
    fireEvent.click(formulaButtons[0]!);

    await screen.findByRole('dialog');

    // ⛔ Головне твердження. На невиправленому коді тут порожній рядок:
    // `onClick` завжди будував `emptyFormulaDraft`, ігноруючи
    // `column.formulaExpression`, який `GET …/structure` уже приносить.
    await waitFor(() => {
      const field = screen.getByLabelText(/formulas\.expression⟧/) as HTMLTextAreaElement;
      expect(field.value).toBe('A + B');
    });
  });

  it('колонка БЕЗ формули — редактор і далі відкривається порожнім (регресія не зламана)', async () => {
    stubFetch();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    // Друга кнопка "Formula" в переліку колонок — `EMPTY` (id 43, без формули).
    const formulaButtons = await screen.findAllByRole('button', { name: /formulas\.edit⟧/ });
    fireEvent.click(formulaButtons[1]!);

    await screen.findByRole('dialog');

    await waitFor(() => {
      const field = screen.getByLabelText(/formulas\.expression⟧/) as HTMLTextAreaElement;
      expect(field.value).toBe('');
    });
  });

  it('рядок зі збереженою формулою — редактор показує ЧИННИЙ вираз', async () => {
    stubFetch();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPage();
    await openSheet();

    // Дві колонки з кнопкою "Formula" йдуть першими в DOM, тому кнопка
    // рядка R1 (з формулою) — третя за порядком.
    const formulaButtons = await screen.findAllByRole('button', { name: /formulas\.edit⟧/ });
    fireEvent.click(formulaButtons[2]!);

    await screen.findByRole('dialog');

    await waitFor(() => {
      const field = screen.getByLabelText(/formulas\.expression⟧/) as HTMLTextAreaElement;
      expect(field.value).toBe('[C1] + [C2]');
    });
  });
});
