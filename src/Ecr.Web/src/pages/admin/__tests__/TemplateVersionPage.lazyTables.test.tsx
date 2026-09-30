import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import {
  LazyTableSlots,
  TemplateTableSlotAttribute,
  estimateTemplateTableHeight,
} from '@/features/templates/LazyTableSlots';

/**
 * Дерево структури версії монтує таблиці за прокруткою, а не всі одразу.
 *
 * ⛔ Що тут тримається закритим. Версія чинного розміру — 92 таблиці,
 * ~2986 колонок. До цієї зміни `Accordion.Panel` (Mantine `Collapse`, діти
 * змонтовані й у згорнутому стані) тримав УСІ таблиці всіх аркушів, і
 * відкриття сторінки на продакшн-збірці коштувало ~1 с довгих задач, а
 * розгортання аркуша з 91 таблиці — ще ~1.5 с.
 *
 * ⚠ Спостерігач підмінений: заглушка з `src/test/setup.ts` інертна за
 * побудовою (у jsdom немає розкладки), тож подію перетину тут задає сам тест,
 * поіменно — той самий прийом, що й `features/grid/__tests__/SheetTables.lazy.test.tsx`.
 *
 * ⛔ Мутаційний доказ: `live={true}` замість `isMounted(item, index)` у
 * `LazyTableSlots` (лінивість вимкнена) — червоні всі чотири перевірки
 * сторінки; прибраний `memo` зі слота — червона остання («expected 4 to be 2»).
 */

interface ObserverProbe {
  /** Повідомляє спостерігачів про появу слотів із цими `id` таблиць. */
  readonly appear: (ids: readonly number[]) => void;
}

function installObserver(): ObserverProbe {
  const live = new Set<{ targets: Set<Element>; notify: IntersectionObserverCallback }>();

  class FakeObserver {
    private readonly targets = new Set<Element>();
    private readonly entry: { targets: Set<Element>; notify: IntersectionObserverCallback };

    constructor(callback: IntersectionObserverCallback) {
      this.entry = { targets: this.targets, notify: callback };
      live.add(this.entry);
    }

    observe(target: Element): void {
      this.targets.add(target);
    }

    unobserve(target: Element): void {
      this.targets.delete(target);
    }

    disconnect(): void {
      this.targets.clear();
      live.delete(this.entry);
    }

    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  vi.stubGlobal('IntersectionObserver', FakeObserver);

  return {
    appear: (ids) => {
      const wanted = new Set(ids.map(String));

      act(() => {
        for (const entry of [...live]) {
          const hits = [...entry.targets].filter((target) =>
            wanted.has(target.getAttribute(TemplateTableSlotAttribute) ?? ''),
          );
          if (hits.length === 0) continue;

          entry.notify(
            hits.map(
              (target) => ({ target, isIntersecting: true }) as unknown as IntersectionObserverEntry,
            ),
            null as unknown as IntersectionObserver,
          );
        }
      });
    },
  };
}

const TableIds = [10, 11, 12, 13, 14] as const;

function table(id: number): unknown {
  return {
    id,
    code: `T${String(id)}`,
    nameL10n: { values: { en: `Table ${String(id)}` } },
    layoutKind: 'Static',
    maxDynamicRows: null,
    ordinal: id,
    rowMode: 'Fixed',
    rows: [],
    columns: [
      {
        id: id * 100,
        code: `COL${String(id)}`,
        dataType: 'Decimal',
        displayFormat: null,
        headerL10n: { values: { en: `Column ${String(id)}` } },
        isHidden: false,
        isReadOnly: false,
        isRequired: false,
        ordinal: 1,
        unitSymbol: null,
        formulaExpression: null,
        formulaDialect: null,
      },
    ],
  };
}

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
        tables: TableIds.map(table),
      },
    ],
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.Publish', 'Template.Edit'],
          isSimulation: false,
        });
      }
      if (url.includes('/structure')) return json(structureDto());
      if (url.includes('/versions?limit=')) {
        return json({
          items: [{ id: 1, version: '1.0.0.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

function renderPage(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
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

/** Вміст таблиці змонтовано — видно її колонку. */
function columnShown(id: number): boolean {
  return screen.queryByText(`(COL${String(id)})`) !== null;
}

function slot(id: number): Element {
  const node = document.querySelector(`[${TemplateTableSlotAttribute}="${String(id)}"]`);
  if (node === null) throw new Error(`slot ${String(id)} is missing`);
  return node;
}

let probe: ObserverProbe;

beforeEach(async () => {
  probe = installObserver();
  stubFetch();
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateVersionPage: таблиці дерева структури монтуються за прокруткою', () => {
  it('при відкритті (аркуш згорнутий) не змонтовано ЖОДНОЇ таблиці — лише заповнювачі', async () => {
    renderPage();
    await screen.findByRole('button', { name: /Sheet \(SHEET\)/ });

    for (const id of TableIds) {
      expect(columnShown(id)).toBe(false);
      expect(slot(id).getAttribute('data-template-table-mounted')).toBe('false');
    }
  });

  it('розгорнутий аркуш: перша таблиця змонтована, решта — заповнювачі з доступною назвою і висотою', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));

    await waitFor(() => expect(columnShown(10)).toBe(true));
    for (const id of TableIds.slice(1)) {
      expect(columnShown(id)).toBe(false);

      // ⚠ Заповнювач має назву таблиці (a11y) і НЕнульову висоту — інакше
      // усі слоти схлопнулись би в один екран і змонтувалось би все.
      const placeholder = await screen.findByRole('group', { name: `Table ${String(id)}` });
      expect(placeholder.getAttribute('aria-busy')).toBe('true');
      expect((placeholder as HTMLElement).style.height).toBe(
        `${String(estimateTemplateTableHeight({ rowMode: 'Fixed', columns: [{}], rows: [] }))}px`,
      );
    }
  });

  it('таблиця монтується, коли її слот стає видимим, — і лише вона', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
    await waitFor(() => expect(columnShown(10)).toBe(true));

    probe.appear([13]);

    expect(columnShown(13)).toBe(true);
    expect(columnShown(11)).toBe(false);
    expect(columnShown(12)).toBe(false);
    expect(columnShown(14)).toBe(false);

    // Змонтоване лишається змонтованим і після згортання аркуша.
    fireEvent.click(screen.getByRole('button', { name: /Sheet \(SHEET\)/ }));
    expect(slot(13).getAttribute('data-template-table-mounted')).toBe('true');
  });

  it('фокус у таблиці монтує наступну — Tab не перестрибує незмонтованих', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
    await waitFor(() => expect(columnShown(10)).toBe(true));
    expect(columnShown(11)).toBe(false);

    const button = slot(10).querySelector('button');
    if (button === null) throw new Error('table 10 has no button');
    act(() => button.focus());

    expect(columnShown(11)).toBe(true);
    expect(columnShown(12)).toBe(false);
  });
});

describe('LazyTableSlots: монтування однієї таблиці не перемальовує вже змонтованих', () => {
  it('render змонтованої таблиці не викликається вдруге, коли з’являється інша', () => {
    const items = [{ id: 1 }, { id: 2 }, { id: 3 }] as const;
    const renders = new Map<number, number>();
    const renderItem = (item: { id: number }): JSX.Element => {
      renders.set(item.id, (renders.get(item.id) ?? 0) + 1);
      return <p>{`content ${String(item.id)}`}</p>;
    };
    const nameOf = (item: { id: number }): string => `T${String(item.id)}`;
    const heightOf = (): number => 100;

    render(
      <MantineProvider theme={theme}>
        <LazyTableSlots
          items={items}
          active
          nameOf={nameOf}
          titleOf={nameOf}
          heightOf={heightOf}
          render={renderItem}
        />
      </MantineProvider>,
    );

    expect(screen.getByText('content 1')).toBeDefined();
    const first = renders.get(1) ?? 0;

    probe.appear([2]);
    probe.appear([3]);

    expect(screen.getByText('content 3')).toBeDefined();
    // ⛔ Без `memo` на слоті кожне монтування перерендерює ВСІ змонтовані —
    // O(n²) на прокрутку аркуша (зміряно: 37.7 с довгих задач на v2).
    expect(renders.get(1)).toBe(first);
    expect(renders.get(2)).toBe(1);
  });
});
