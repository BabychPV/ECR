import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import {
  SheetTables,
  TableSlotAttribute,
  TableSlotMinHeight,
} from '../SheetTables';

/**
 * Сітки аркуша монтуються за прокруткою, а не всі одразу.
 *
 * ⛔ Що тут тримається закритим. Документ чинного розміру — 91 таблиця на
 * аркуші (`Ecr.DataGen/DistributionProfile.cs`), а оператор бачить одночасно
 * одну-дві. До цієї картки `tables.map` монтував УСІ 91 `DocumentGrid`, і
 * кожен із них одразу йшов по власний зріз — найважчий регулярний запит
 * системи (`GetTableSliceHandler`, бюджет p95 1.5 с на 500×60).
 *
 * ⛔ ЧОМУ ТУТ ТРИ ПЕРЕВІРКИ, А НЕ ОДНА. Механізм ламається трьома різними
 * способами, і кожен із них виглядає як робочий код:
 *   1. монтувати все одразу — тоді лінивість лише на словах;
 *   2. дати заглушці нульову (чи дрібну) висоту — тоді 91 слот схлопується в
 *      один екран, спостерігач бачить їх усі й монтується знову все; саме ця
 *      помилка найпоширеніша в цьому патерні, і зовні вона НЕ ВИДНА;
 *   3. розмонтовувати те, що поїхало геть, — тоді мовчки гинуть незбережені
 *      правки, історія Undo/Redo і виділення, а повернення прокрутки коштує
 *      ще одного запиту зрізу.
 *
 * ⚠ `DocumentGrid` підмінений: тут перевіряється РІШЕННЯ про монтування, а не
 * сітка. Справжній `DocumentGrid` тягне `RevoGrid` і йде по зріз — тобто
 * перевірка вимірювала б чужу поведінку і падала б із чужих причин.
 *
 * ⚠ Спостерігач теж підмінений — і саме тому, що заглушка з `src/test/setup.ts`
 * інертна за побудовою: у jsdom немає розкладки, і «що видно» там не визначено
 * зовсім. Подію перетину тут задає сам тест, поіменно.
 */
vi.mock('../DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }) => (
    <div data-testid={`grid-${String(tableInstanceId)}`} />
  ),
}));

/** Скільки таблиць у документі чинного розміру. */
const TablesPerSheet = 91;

interface ObserverProbe {
  readonly observed: () => Element[];
  /** Повідомляє спостерігачів про появу вузлів із цими `tableInstanceId`. */
  readonly appear: (ids: readonly number[]) => void;
  readonly disconnects: () => number;
}

/**
 * Підміна `IntersectionObserver`, якою керує тест.
 *
 * ⚠ Живих екземплярів може бути кілька: компонент створює спостерігача
 * НАНОВО на кожну зміну складу змонтованих (так він добирає слоти, що доїхали
 * у видиму область після зсуву розмітки). Тому `appear` розсилає подію всім
 * живим — так само, як це зробив би браузер.
 */
function installObserver(): ObserverProbe {
  const live = new Set<{ targets: Set<Element>; notify: IntersectionObserverCallback }>();
  let disconnects = 0;

  class FakeObserver {
    private readonly targets = new Set<Element>();
    private readonly entry: { targets: Set<Element>; notify: IntersectionObserverCallback };

    constructor(private readonly callback: IntersectionObserverCallback) {
      this.entry = { targets: this.targets, notify: this.callback };
      live.add(this.entry);
    }

    observe(target: Element): void {
      this.targets.add(target);
    }

    unobserve(target: Element): void {
      this.targets.delete(target);
    }

    disconnect(): void {
      disconnects += 1;
      this.targets.clear();
      live.delete(this.entry);
    }

    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  vi.stubGlobal('IntersectionObserver', FakeObserver);

  return {
    observed: () => [...new Set([...live].flatMap((entry) => [...entry.targets]))],
    disconnects: () => disconnects,
    appear: (ids) => {
      const wanted = new Set(ids.map(String));

      act(() => {
        for (const entry of [...live]) {
          const hits = [...entry.targets].filter((target) =>
            wanted.has(target.getAttribute(TableSlotAttribute) ?? ''),
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

/**
 * `tableInstanceId` таблиці за порядковим номером.
 *
 * ⚠ Окрема функція, а не `tables[i].tableInstanceId`: під
 * `noUncheckedIndexedAccess` кожне таке звертання довелося б розкривати `!`
 * або перевіркою на `undefined`, і сам тест читався б гірше за те, що він
 * перевіряє.
 */
function idAt(index: number): number {
  return 1000 + index;
}

function tableFixture(index: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(index)}`,
    tableDefId: index,
    tableInstanceId: idAt(index),
    tableNameL10n: { values: { en: `Table ${String(index)}` } },
    tableOrdinal: index,
  } as DocumentTableDto;
}

const tables = Array.from({ length: TablesPerSheet }, (_, index) => tableFixture(index));

function renderSheet(): void {
  render(
    <MantineProvider>
      <SheetTables documentId={7} periodKey={202609} readOnly={false} tables={tables} />
    </MantineProvider>,
  );
}

describe('SheetTables монтує сітки за прокруткою', () => {
  let probe: ObserverProbe;

  beforeEach(() => {
    probe = installObserver();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('до першої появи слота не монтує ЖОДНОЇ сітки, але показує всі заголовки', () => {
    renderSheet();

    // ⛔ Саме нуль, а не «менше за 91»: доки спостерігач не сказав, що щось
    // видно, видимого немає нічого, і будь-яка змонтована сітка тут — це
    // запит зрізу, якого ніхто не просив.
    expect(document.querySelectorAll('[data-testid^="grid-"]')).toHaveLength(0);

    // ⚠ Заголовки — навпаки, ВСІ: інакше прокрутка сторінкою заглушок
    // безглузда, і розмір документа з екрана не видно.
    expect(screen.getAllByText(/^Table \d+$/)).toHaveLength(TablesPerSheet);

    // ⚠ Слот є на кожну таблицю (розмір сторінки видно) і кожен спостерігається.
    expect(document.querySelectorAll(`[${TableSlotAttribute}]`)).toHaveLength(TablesPerSheet);
    expect(probe.observed()).toHaveLength(TablesPerSheet);
  });

  it('кожен слот тримає висоту сітки — інакше всі 91 були б видні одразу', () => {
    renderSheet();

    const slots = [...document.querySelectorAll<HTMLElement>(`[${TableSlotAttribute}]`)];

    // ⛔ Це і є та сама «найпоширеніша помилка патерну»: заглушки нульової
    // висоти складаються в один екран, спостерігач повідомляє про всі 91
    // одразу — і лінива сторінка поводиться як нелінива, не подаючи знаку.
    expect(slots).not.toHaveLength(0);
    for (const slot of slots) {
      expect(slot.style.minHeight).toBe(TableSlotMinHeight);
    }
  });

  it('монтує рівно ті таблиці, які з\'явилися, і не чіпає решти', () => {
    renderSheet();

    probe.appear([idAt(0), idAt(1)]);

    expect(screen.getByTestId(`grid-${String(idAt(0))}`)).toBeDefined();
    expect(screen.getByTestId(`grid-${String(idAt(1))}`)).toBeDefined();
    expect(document.querySelectorAll('[data-testid^="grid-"]')).toHaveLength(2);

    // ⚠ Слот змонтованої таблиці більше не спостерігається: він своє відпрацював.
    expect(probe.observed()).toHaveLength(TablesPerSheet - 2);

    // ⛔ Рівно цими селекторами рахує змонтовані сітки замір на живому стенді
    // (`e2e/lazyTables.spec.ts`). React віддає булеве значення `data-*`
    // рядком — але якщо це колись зміниться, замір мовчки почав би рахувати
    // нуль і друкувати «лінивість ідеальна». Хай падає ТУТ і одразу.
    expect(document.querySelectorAll('[data-table-mounted="true"]')).toHaveLength(2);
    expect(document.querySelectorAll('[data-table-mounted="false"]')).toHaveLength(
      TablesPerSheet - 2,
    );
  });

  it('змонтована сітка лишається змонтованою, коли видно вже інші таблиці', () => {
    renderSheet();

    probe.appear([idAt(0)]);
    probe.appear([idAt(50)]);

    // ⛔ Перша сітка НЕ зникає. У ній живе те, чого немає більше ніде:
    // незбережені правки, підтверджені значення, історія Undo/Redo (≥50
    // кроків — `B21` §12), виділення. Розмонтування викинуло б це мовчки.
    expect(screen.getByTestId(`grid-${String(idAt(0))}`)).toBeDefined();
    expect(screen.getByTestId(`grid-${String(idAt(50))}`)).toBeDefined();
    expect(document.querySelectorAll('[data-testid^="grid-"]')).toHaveLength(2);
  });

  it('повторна поява вже змонтованої таблиці не перезапускає спостереження без кінця', () => {
    renderSheet();

    probe.appear([idAt(0)]);
    const afterFirst = probe.disconnects();

    // ⚠ Та сама таблиця вдруге: множина змонтованих не змінюється, отже не
    // змінюється і стан — а без цієї умови кожне спрацювання спостерігача
    // давало б новий `Set`, новий рендер і нового спостерігача по колу.
    probe.appear([idAt(0)]);

    expect(probe.disconnects()).toBe(afterFirst);
    expect(document.querySelectorAll('[data-testid^="grid-"]')).toHaveLength(1);
  });
});
