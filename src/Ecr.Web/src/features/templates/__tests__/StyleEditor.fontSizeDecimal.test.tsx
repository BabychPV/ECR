import { useState, type JSX } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { saveColumn } from '../columnApi';
import { emptyStyleDraft } from '../style';

/**
 * Розмір шрифту стилю — `decimal` контракту (`SaveStyleDefRequest.fontSize`,
 * `Format: decimal` у `schema.d.ts`), тобто РЯДОК: `e470777a` перевів decimal у
 * рядок саме тому, що `JSON.parse` губить знаки беззворотно.
 *
 * ⛔ `d9e75a82` перевів чернетку (`StyleDraft.fontSize`) на `string | null`, але
 * саме поле лишилося `NumberInput`-ом із `String(value)` на виході. Тип рядка
 * був чесний, значення — ні.
 *
 * ⚠ Механізм втрати перевірено зондом на `@mantine/core` 7.15.2, а не взято з
 * документації, — і він виявився НЕ тим, про який пишуть сусідні коментарі. У
 * самому `onChange` втрати немає: `isValidNumber` віддає сирий рядок, щойно в
 * значенні ≥ 14 цифр. Ламає ВТРАТА ФОКУСА — `trimLeadingZeroesOnBlur`
 * (`NumberInput.mjs:324`) робить `parseFloat` і кладе в стан число. Тому
 * випадки нижче б'ють по полю `blur`-ом: без нього набір `NumberInput` пережив
 * би, і доказ лишався б зеленим на зламаному коді.
 */

const SeededStrings: Record<string, string> = {
  'columns.code': 'Code',
  'columns.codeHint': 'Latin letters, digits and underscore.',
  'columns.header': 'Header',
  'columns.headerHint': 'Shown to the person filling the document.',
  'columns.dataType': 'Data type',
  'columns.dataTypeHint': 'What kind of value this column holds.',
  'columns.ordinal': 'Ordinal',
  'columns.ordinalHint': 'Order among the columns of this table.',
  'columns.customStyle': 'Custom style',
  'columns.customStyleHint': 'Draw this column differently.',
  'columns.save': 'Save',
  'common.cancel': 'Cancel',
  'styles.legend': 'Style',
  'styles.code': 'Style code',
  'styles.codeHint': 'Unique within this template version.',
  'styles.fontName': 'Font name',
  'styles.fontNameHint': 'Leave empty for the workbook theme font.',
  'styles.fontSize': 'Font size',
  'styles.bold': 'Bold',
  'styles.italic': 'Italic',
  'styles.wrapText': 'Wrap text',
  'styles.foreground': 'Text color',
  'styles.background': 'Fill color',
  'styles.horizontalAlign': 'Horizontal align',
  'styles.verticalAlign': 'Vertical align',
  'styles.alignLeft': 'Left',
  'styles.alignCenter': 'Center',
  'styles.alignRight': 'Right',
  'styles.alignJustify': 'Justify',
  'styles.alignTop': 'Top',
  'styles.alignMiddle': 'Middle',
  'styles.alignBottom': 'Bottom',
  'styles.borderLegend': 'Border',
  'styles.borderTop': 'Top',
  'styles.borderRight': 'Right',
  'styles.borderBottom': 'Bottom',
  'styles.borderLeft': 'Left',
  'styles.borderNone': 'None',
  'styles.borderThin': 'Thin',
  'styles.borderMedium': 'Medium',
  'styles.borderThick': 'Thick',
  'styles.numberFormat': 'Number format',
  'styles.numberFormatHint': 'Excel number format.',
};

/**
 * Розмір, який `double` ГАРАНТОВАНО псує — і псує саме так, як це робив
 * `NumberInput` на втраті фокуса.
 *
 * ⚠ `Number('12345678901234.567')` === `12345678901234.566` (перевіряється
 * твердженням у випадку нижче, а не обіцяється коментарем): 17 значущих цифр
 * проти ~15–17 доступних, і останній знак зсувається ВНИЗ.
 *
 * ⛔ Знаків після коми тут навмисно ТРИ, а не шістнадцять, і це не косметика.
 * `trimLeadingZeroesOnBlur` чіпає лише значення з < 15 знаками після коми, тож
 * на «красивому» довгому хвості (`0.4535923700000001`, 16 знаків) `NumberInput`
 * ВИПАДКОВО нешкідливий — і доказ, узятий на такому вході, лишався б зеленим
 * після повернення `NumberInput`, тобто не перевіряв би нічого. Зонд на
 * 7.15.2 дав на цьому вході рівно `12345678901234.566`.
 */
const Precise = '12345678901234.567';

/** Те, у що його перетворює `double` — саме це й їхало б на сервер. */
const Corrupted = '12345678901234.566';

interface Recorded {
  readonly styleBodies: Record<string, unknown>[];
}

function mockApi(): Recorded {
  const styleBodies: Record<string, unknown>[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      // ⚠ Потрібен `LocalizedInput` (поле заголовка колонки). Без цього мока
      // форма малює `ErrorAlert` — і твердження «іншого банера на екрані немає»
      // падало б не на своїй причині.
      if (url.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      if (/\/styles\/[^/]+$/.test(url) && method === 'PUT') {
        styleBodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
        return json({ id: 5, code: 'AmountStyle' });
      }

      if (/\/columns\/[^/]+$/.test(url) && method === 'PUT') {
        return json({ id: 3, code: 'Amount' });
      }

      // ⚠ R-07: форма колонки Decimal тепер показує вибір одиниці.
      if (url.endsWith('/api/v1/units')) {
        return json([]);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { styleBodies };
}

/** Колонка з увімкненим власним стилем; `styleId: null` — перелік стилів не потрібен. */
const styledDraft: ColumnDraft = {
  ...emptyColumnDraft(1),
  code: 'Amount',
  headerL10n: { en: 'Amount' },
  isNew: true,
  style: emptyStyleDraft('AmountStyle'),
};

/** Форма, змонтована КЕРОВАНО: інакше набране не доходить назад у `draft`. */
async function show(initial: ColumnDraft): Promise<() => ColumnDraft> {
  await loadCatalog('en', 'private');

  // ⚠ Імпорт ПІСЛЯ каталогу: `StyleEditor.tsx` рахує `BorderOptions` через
  // `t()` на рівні модуля, і при ранньому імпорті підписи стали б `⟦ключ⟧`.
  const { ColumnEditor } = await import('../ColumnEditor');

  let latest = initial;

  function Harness(): JSX.Element {
    const [draft, setDraft] = useState(initial);

    return (
      <ColumnEditor
        draft={draft}
        disabled={false}
        saving={false}
        templateVersionId={1}
        onChange={(next) => {
          latest = next;
          setDraft(next);
        }}
        onSubmit={() => {}}
        onCancel={() => {}}
      />
    );
  }

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return () => latest;
}

function saveButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement;
}

/*
 * ⛔ Прогрів `lazy()` — не оптимізація, а причина падінь під навантаженням.
 * `ColumnEditor` тягне `StyleEditor` через `lazy()`, і об'єкт `lazy` живе на
 * рівні модуля. Тому ЛИШЕ перший монтаж у файлі проходить шлях «Suspense →
 * запасний скелет → доїзд модуля → повторний рендер у задачі планувальника»,
 * і все це — всередині `findByLabelText('Font size')`, чия стеля 1000 мс.
 * Далі `lazy` уже розв'язаний, і форма малюється одним синхронним рендером.
 *
 * Виміряно на першому випадку поодинці (3 прогони кожен):
 *   • без прогріву: очікування поля 339–397 мс, випадок 778–837 мс;
 *   • з прогрівом:  очікування поля  28–64 мс, випадок 204–326 мс.
 * Під 24 процесами-навантажувачами на 12 ядрах перший випадок падав 5 із 5
 * («Unable to find a label with the text of: Font size» на 3.2–4.3 с), а три
 * наступні — ні: саме тому, що платив лише перший.
 *
 * ⚠ Попередній імпорт самих модулів НЕ допомагав (виміряно: 330–490 мс
 * очікування): розв'язується не модуль, а конкретний об'єкт `lazy`, і
 * зробити це можна лише рендером. Разова ціна переїжджає в хук зі стелею
 * `hookTimeout`; `timeout` тут — для цієї разової ціни, а не для випадків.
 */
beforeAll(async () => {
  mockApi();
  try {
    await show(styledDraft);
    // 8 с, а не 10: нижче `hookTimeout` (10 с), щоб відмова назвала поле, а не хук.
    await screen.findByLabelText('Font size', undefined, { timeout: 8_000 });
  } finally {
    cleanup();
    vi.unstubAllGlobals();
  }
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('StyleEditor: розмір шрифту доходить до тіла запиту тими самими знаками', () => {
  it('17 значущих цифр не проходять через число дорогою до PUT …/styles/{code}', async () => {
    const { styleBodies } = mockApi();
    const draft = await show(styledDraft);

    const field = await screen.findByLabelText('Font size');

    // ⛔ Вхід підібраний так, щоб `double` його справді псував: твердження, а не
    // обіцянка в коментарі — сусідня сесія вже спіймала на цьому хибнозелений
    // доказ (`String(Number('0.4535923700000001'))` повертає ТОЙ САМИЙ рядок).
    expect(String(Number(Precise))).toBe(Corrupted);

    fireEvent.change(field, { target: { value: Precise } });

    /*
     * ⛔ Саме `blur`, і він тут ОБОВ'ЯЗКОВИЙ. `NumberInput` переживає набір
     * такого значення без втрат (≥ 14 цифр — `isValidNumber` віддає сирий
     * рядок) і губить знак рівно на втраті фокуса
     * (`trimLeadingZeroesOnBlur` → `parseFloat`). Без цього рядка випадок
     * лишався б зеленим із поверненим `NumberInput`, тобто не доводив би
     * нічого — перша редакція цього тесту саме так і промахнулася.
     */
    fireEvent.blur(field);

    await saveColumn(1, 2, draft());

    /*
     * ⛔ Мутаційний доказ, дві незалежні мутації дають ТОЙ САМИЙ результат:
     * поверніть перетворення на шляху запису (`String(Number(next))`) або
     * поверніть сам `NumberInput` — сюди приїде `'12345678901234.566'`.
     * Перевіряється ТІЛО запиту, а не факт виклику: `toHaveBeenCalled`
     * лишилося б зеленим і з загубленим знаком.
     *
     * ⛔ Літерал РЯДКОМ. Очікування числом (`toBe(12345678901234.567)`) було б
     * хибнозеленим після тієї ж мутації: обидва боки порівняння пройшли б через
     * ту саму втрату й зійшлися б.
     */
    expect(styleBodies).toHaveLength(1);
    expect(styleBodies[0]?.['fontSize']).toBe(Precise);

    /*
     * ⚠ І лише як доповнення — вид поля. Ролі тут НЕ розрізняють `TextInput`
     * і `NumberInput`: Mantine 7 будує другий на `react-number-format`, тобто
     * на тому самому `<input type="text">` з `role="textbox"`, і
     * `queryByRole('spinbutton')` порожній В ОБОХ випадках (перевірено
     * зондом). Твердження про роль було б хибнозеленим сторожем; `inputmode`
     * різниться (`decimal` проти `numeric`), але головний доказ — вище.
     */
    expect(field.getAttribute('inputmode')).toBe('decimal');
    expect(screen.queryByRole('spinbutton', { name: 'Font size' })).toBeNull();
  });

  it('порожнє поле — це `null` («розмір теми»), а НЕ нуль і не «0»', async () => {
    const { styleBodies } = mockApi();
    const draft = await show(styledDraft);

    const field = await screen.findByLabelText('Font size');

    fireEvent.change(field, { target: { value: '14' } });
    fireEvent.change(field, { target: { value: '' } });

    await saveColumn(1, 2, draft());

    /*
     * ⚠ Тут порожнє поле МАЄ значення: контракт називає `null` «розмір теми за
     * замовчуванням». Правдоподібною неправдою був би саме `0` чи `'0'` —
     * шрифт нульового розміру, якого ніхто не просив.
     */
    expect(styleBodies[0]?.['fontSize']).toBeNull();
    expect(styleBodies[0]?.['fontSize']).not.toBe(0);
    expect(styleBodies[0]?.['fontSize']).not.toBe('0');
  });
});

describe('StyleEditor: «не число» блокує збереження, і причину видно', () => {
  it('нерозбірний розмір вимикає «Save» і показує причину', async () => {
    mockApi();
    await show(styledDraft);

    const field = await screen.findByLabelText('Font size');

    // ⚠ Спершу дзеркало: на коректному значенні кнопка ДОСТУПНА. Без нього
    // «полагодити» перевірку можна було б назавжди вимкненою кнопкою.
    fireEvent.change(field, { target: { value: '11.5' } });
    expect(saveButton().disabled).toBe(false);
    expect(screen.queryByRole('alert')).toBeNull();

    /*
     * ⛔ Мутаційний доказ: приберіть гілку `StyleFontSize` у
     * `whyCannotSaveColumn` (`column.ts`) — і кнопка лишиться доступною, тобто
     * `'12pt'` поїде в `PUT …/styles/{code}` і поверне сиру відмову сервера.
     * А `saveColumn` шле стиль ПЕРШИМ, тож і колонка не збережеться.
     */
    fireEvent.change(field, { target: { value: '12pt' } });
    expect(saveButton().disabled).toBe(true);

    // ⚠ Причина на екрані. Текст поки що — сам код причини: рядка
    // `columns.errStyleFontSize` у каталозі ще немає (як немає й
    // `columns.errStyleCode`, який поводиться так само з `d9e75a82`), і просити
    // неіснуючий ключ означало б червоний гейт `test`. Названо у звіті.
    expect(screen.getByRole('alert').textContent ?? '').toContain('StyleFontSize');

    // ⚠ І саме поле позначене помилковим — без цього причина була б лише
    // внизу форми, а зіпсоване значення виглядало б звичайним.
    expect(field.getAttribute('aria-invalid')).toBe('true');
  });

  it('порожнє поле збереження НЕ блокує — «розмір теми» це заповнений стан', async () => {
    mockApi();
    await show(styledDraft);

    const field = await screen.findByLabelText('Font size');

    fireEvent.change(field, { target: { value: '' } });

    // ⛔ Друге дзеркало, і воно не зайве: якби перевірка розміру забороняла ще
    // й `null`, стиль без явного розміру шрифту стало б неможливо зберегти
    // взагалі — тобто «полагоджено» коштувало б робочого випадку.
    expect(saveButton().disabled).toBe(false);

    // ⚠ `'false'`, не відсутність атрибута: Mantine пише `aria-invalid` завжди.
    expect(field.getAttribute('aria-invalid')).toBe('false');
  });
});
