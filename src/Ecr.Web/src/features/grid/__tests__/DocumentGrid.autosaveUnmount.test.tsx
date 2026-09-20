import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingCount, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Правка не гине разом із сіткою (`D14-12`, `W-02`).
 *
 * ⛔ ЦЕЙ ФАЙЛ СТВЕРДЖУВАВ ПРОТИЛЕЖНЕ, і підміну треба назвати вголос. Доти
 * перший тут тест звався «правка перед розмонтуванням НЕ йде на сервер
 * запитом від мертвого компонента» і вимагав `expect(patched).toHaveLength(0)`.
 * Він захищав справжню властивість: `PATCH`, надісланий таймером уже
 * розмонтованої сітки, нікому показати — ні конфлікт `409`, ні відмову
 * валідації, ні банер «не збережено»; весь цей стан належав зниклому дереву.
 *
 * ⛔ Але ціна тієї властивості була не названа в самому тесті: щоб її
 * витримати, `DocumentGrid` у cleanup робив `autosaveDebouncer.current.cancel()`
 * — і правка молодша за 500 мс зникала МОВЧКИ при перемиканні аркуша. Тобто
 * зелений тест охороняв ПІВПРАВДИ: «не слати від мертвого» він доводив, а
 * «нічого не втратити» — ні, і саме цей бік коштував даних оператора (`W-02`,
 * серйозність Б).
 *
 * ⚠ Після `D14-12` обидва твердження стали сумісні, тому підміна — не
 * послаблення:
 *   — ЩО ЗАЛИШИЛОСЯ: розмонтована сітка й далі нічого не надсилає сама. Вона
 *     лише знімає себе з реєстру зберігачів (`registerSliceSaver`);
 *   — ЩО З'ЯВИЛОСЯ: правка лежить у сховищі ДОКУМЕНТА (`pendingStore.ts`), і
 *     запит по ній робить рівень документа (`useDocumentPending`), який живий
 *     і має куди показати відмову.
 * Старе очікування `toHaveLength(0)` після цього стало б доказом дефекту, а не
 * поведінки, — тому воно замінене, а не пом'якшене.
 *
 * ⛔ Директива відхиляє обидва «дешеві» способи отримати той самий зелений
 * результат, і жодного з них тут немає: `flush()` у cleanup сітки (повертає
 * рівно той дефект — результат нікому показати) і заборона перемикати аркуш
 * при `pending > 0` (карає користувача за швидкість).
 *
 * ⚠ Очікування справжнє (не фальшиві таймери) і навмисно ДОВШЕ за 500 мс.
 * Помилитися воно може лише в один бік: під навантаженням реального часу мине
 * ще більше — тобто «зелено через те, що не встигло» тут неможливе.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' },
          })
        }
      >
        simulate-edit
      </button>
    </div>
  ),
}));

const DocumentId = 1;
const Period = 202609;

/** Аркуш 1 і аркуш 2: у документі це РІЗНІ екземпляри таблиць. */
const FirstSheetTable = 1;
const SecondSheetTable = 2;

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: FirstSheetTable,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'C1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: 1 },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

/** Усі `PATCH`, що дійшли до мережі, у порядку надходження. */
const patched: { url: string; body: string }[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patched.push({ url: String(path), body: String(init.body) });

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

/**
 * Документ із одним аркушем на екрані — рівно та композиція, що в застосунку.
 *
 * ⛔ Сітки аркуша рендеряться з `key={tableInstanceId}` (`SheetTables.tsx`),
 * тож перемикання аркуша РОЗМОНТОВУЄ попередні. Кнопка нижче робить саме це,
 * а не «змінює проп»: інакше сценарій `W-02` не відтворюється взагалі.
 *
 * ⚠ `useDocumentPending` — той самий хук, яким користується `DocumentPage`, не
 * тестова заглушка. Без нього документа немає, і безхазяйний зріз нікому
 * зберігати — саме цю межу тест і перевіряє.
 */
function DocumentHost(): JSX.Element {
  useDocumentPending(DocumentId);
  const [table, setTable] = useState(FirstSheetTable);

  return (
    <>
      <button type="button" onClick={() => setTable(SecondSheetTable)}>
        switch-sheet
      </button>

      <DocumentGrid
        key={table}
        documentId={DocumentId}
        tableInstanceId={table}
        periodKey={Period}
        readOnly={false}
        allowsDynamicRows={false}
        maxDynamicRows={null}
      />
    </>
  );
}

function show(): { unmount: () => void } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentHost />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Чекає `ms` справжнього часу, даючи React домалювати наслідки. */
async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Пропускає повний інтервал дебаунсу (500 мс) із запасом. */
function passDebounceWindow(): Promise<void> {
  return wait(900);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('D14-12: перемикання аркуша не губить правку', () => {
  it('ввести значення → за 100 мс перемкнути аркуш → PATCH усе одно йде', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // ⚠ Сто мілісекунд — дослівно з доказу `D14-12`: п'ята частина вікна
    // дебаунсу, тобто момент, коли зберігати ще НІЧОГО не почали.
    await wait(100);
    fireEvent.click(screen.getByRole('button', { name: 'switch-sheet' }));

    /*
     * ⛔ Перша половина доказу, і саме вона падає при поверненні стану в
     * сітку (`useState` замість сховища): правка пережила компонент, який її
     * зробив. Доти в цю мить вона вже не існувала ніде.
     */
    expect(pendingCount()).toBe(1);

    await passDebounceWindow();

    /*
     * ⛔ Друга половина: запит справді пішов — на маршрут ДОКУМЕНТА і з тим
     * значенням, яке ввів оператор. Раніше тут стояло `toHaveLength(0)`:
     * сітка в cleanup скасовувала дебаунс, і правку не надсилав ніхто й
     * ніколи (`W-02`).
     */
    expect(patched).toHaveLength(1);
    expect(patched[0]?.url).toBe(`/api/v1/documents/${String(DocumentId)}/cells`);
    expect(patched[0]?.body).toContain('"rowKey":"r1"');
    // ✎ 2026-09-21: було `'"value":5'`. Після `e470777a` десяткове їде РЯДКОМ,
    // тож у тілі запиту стоїть `"value":"5"` — і саме рядок доводить, що
    // `Number` на цьому шляху більше немає.
    expect(patched[0]?.body).toContain('"value":"5"');

    // ⚠ І сервер підтвердив: незбереженого в документі більше немає. Без
    // цього рядка тест був би зелений і тоді, коли правка надсилається по
    // колу, залишаючись «незбереженою» назавжди.
    expect(pendingCount()).toBe(0);
  });

  it('розмонтована сітка не надсилає нічого САМА — за неї це робить документ', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));
    fireEvent.click(screen.getByRole('button', { name: 'switch-sheet' }));

    /*
     * ⚠ Те, що вціліло від попередньої редакції файлу, і воно не самоочевидне:
     * зникла сітка не має права зберігати сама, бо показати результат
     * (`409`, відмову валідації, банер) їй нема де. Вона знімає себе з
     * реєстру зберігачів, і зріз дістається документові — з ЄДИНИМ запитом,
     * а не двома.
     *
     * ⛔ Мутаційний доказ саме цього рядка: якщо `registerSliceSaver` не
     * знімати в cleanup, збережуть обидва — і мертва сітка, і документ; тут
     * стане 2.
     */
    await passDebounceWindow();

    expect(patched).toHaveLength(1);
  });

  it('доки сітка змонтована, зберігає саме вона — і дебаунс таки спрацьовує', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // ⚠ Зворотний бік усього механізму: якби документ підбирав зрізи завжди
    // (а не лише безхазяйні), автозбереження живої сітки перестало б бути її
    // справою — і разом із ним зник би банер помилки, який показує тільки
    // вона. Тут сітка на місці, і `PATCH` має бути рівно один.
    await passDebounceWindow();

    expect(patched).toHaveLength(1);
    expect(patched[0]?.body).toContain('"rowKey":"r1"');
    expect(pendingCount()).toBe(0);
  });
});
