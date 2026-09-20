import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { EcrApiError } from '@/api/client';
import { DataTable, MaxColumns, type DataTableColumn } from '@/shared/ui/DataTable';
import { renderWithMantine } from '@/test/render';

/**
 * `DataTable` (директива №15 §2, Шар 3; `KIT.md` §6.5).
 *
 * ⛔ Перевіряється ПОВЕДІНКА, а не розмітка: кожне твердження тут описане так,
 * щоб зламати рівно одну властивість компонента й отримати червоне. Дослівні
 * мутації названі поруч із відповідними перевірками.
 */

interface Doc {
  readonly id: string;
  readonly code: string;
  readonly sheets: number;
  readonly owner: string | null;
}

/**
 * ⚠ Порядок навмисно НЕ збігається ні з одним із очікуваних: масив приходить
 * «як віддав сервер», і будь-яке сортування мусить його змінити.
 *
 * ⚠ Регістр так само навмисний: `alpha` малою, `BETA` великими. Порівняння за
 * кодами UTF-16 дало б `BETA, Gamma, alpha` — тобто рядок малими літерами
 * опинився б у кінці абетки. `Intl.Collator` дає `alpha, BETA, Gamma`.
 */
const docs: readonly Doc[] = [
  { id: '2', code: 'BETA', sheets: 7, owner: 'Петров' },
  { id: '1', code: 'alpha', sheets: 12, owner: null },
  { id: '3', code: 'Gamma', sheets: 3, owner: 'Іванов' },
];

const columns: readonly DataTableColumn<Doc>[] = [
  { key: 'code', label: 'Код' },
  { key: 'sheets', label: 'Аркушів', num: true },
  { key: 'owner', label: 'Власник' },
];

/** Стільки колонок, скільки дозволяє `L5`. */
function columnsOf(count: number): readonly DataTableColumn<Doc>[] {
  return Array.from({ length: count }, (_, index) => ({
    key: `c${String(index)}`,
    label: `Колонка ${String(index)}`,
  }));
}

/** Ключі рядків у тому порядку, у якому вони намальовані. */
function renderedOrder(): string[] {
  return Array.from(document.querySelectorAll('tbody tr')).map(
    (row) => row.getAttribute('data-row-key') ?? '',
  );
}

function refusal(): EcrApiError {
  return new EcrApiError({
    title: 'Сервер не відповів',
    detail: 'Не вдалося отримати перелік.',
    status: 500,
    errorCode: 'HTTP-500',
    correlationId: 'cid-table-1',
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllEnvs();
});

describe('L5: понад сім колонок — виняток у розробці, мовчання у збірці', () => {
  it('межа — саме СІМ, а не «скільки написано в константі»', () => {
    /*
     * ⛔ Цей рядок з'явився після мутації, яка НЕ впала. `MaxColumns = 7` →
     * `MaxColumns = 99` — і весь набір лишився зеленим: решта тверджень тут
     * бере число з того самого модуля (`columnsOf(MaxColumns + 1)`), тобто
     * рухається разом із ним. Вони перевіряють ЗВ'ЯЗОК («понад межу — виняток»)
     * і роблять це чесно, але саму межу не тримає ніщо.
     *
     * ⚠ Наслідок був би тихий: правка однієї цифри знімає `L5` з усього
     * застосунку, жоден із семи гейтів не червоніє, і про це дізнається той,
     * хто відкриє перелік із дванадцятьма колонками. Число задане директивою
     * №15 (`L5`, «≤ 7 колонок; решта — у шухляду подробиць»), а не смаком
     * компонента, тож змінювати його можна лише разом із директивою — і цей
     * рядок змусить про неї згадати.
     */
    expect(MaxColumns).toBe(7);
  });

  it('сім колонок проходять', () => {
    renderWithMantine(
      <DataTable<Doc> columns={columnsOf(MaxColumns)} rows={docs} rowKey={(row) => row.id} />,
    );

    expect(screen.getAllByRole('columnheader')).toHaveLength(MaxColumns);
  });

  it('восьма колонка КИДАЄ виняток у режимі розробки', () => {
    /*
     * ⛔ Саме виняток, а не `console.error`: `src/test/setup.ts` консоль не
     * стереже (перевірено читанням файлу), тож попередження не побачив би ані
     * цей набір, ані жоден із семи гейтів.
     *
     * ⛔ Мутаційний доказ: приберіть `import.meta.env.DEV && …` разом із
     * `throw` у `DataTable.tsx` — рендер проходить, і цей `expect(...).toThrow`
     * падає з «received function did not throw».
     */
    expect(() =>
      renderWithMantine(
        <DataTable<Doc>
          columns={columnsOf(MaxColumns + 1)}
          rows={docs}
          rowKey={(row) => row.id}
        />,
      ),
    ).toThrow(/L5/);
  });

  it('у збірці для розгортання восьма колонка НЕ кидає — перелік лишається робочим', () => {
    // ⚠ Восьма колонка робить сторінку незручною, а не непрацездатною. Білий
    // екран замість переліку документів коштував би користувачеві дорожче за
    // саму ваду, яку правило ловить.
    vi.stubEnv('DEV', false);

    renderWithMantine(
      <DataTable<Doc> columns={columnsOf(MaxColumns + 1)} rows={docs} rowKey={(row) => row.id} />,
    );

    expect(screen.getAllByRole('columnheader')).toHaveLength(MaxColumns + 1);
    expect(renderedOrder()).toHaveLength(docs.length);
  });
});

describe('Сортування переставляє РЯДКИ, а не лише шапку', () => {
  function sortBy(label: string): void {
    fireEvent.click(screen.getByRole('button', { name: label }));
  }

  it('клац по шапці змінює порядок рядків; другий клац — розвертає', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    // Порядок сервера.
    expect(renderedOrder()).toEqual(['2', '1', '3']);

    sortBy('Код');

    /*
     * ⛔ Головне твердження файлу — саме ПОРЯДОК КЛЮЧІВ, а не клас чи
     * `aria-sort` у шапці.
     *
     * ⛔ Мутаційний доказ: у `DataTable.tsx` у `useMemo` для `sorted`
     * поверніть `rows` замість відсортованої копії — шапка далі отримує
     * `aria-sort="ascending"` і стрілку, тобто «на око» сортування працює, а
     * цей рядок падає.
     *
     * ⚠ `alpha` ПЕРШИЙ: порівняння за кодами UTF-16 поставило б рядок малими
     * літерами в кінець (`BETA, Gamma, alpha`).
     */
    expect(renderedOrder()).toEqual(['1', '2', '3']);

    sortBy('Код');
    expect(renderedOrder()).toEqual(['3', '2', '1']);
  });

  it('третій клац повертає порядок сервера', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    sortBy('Код');
    sortBy('Код');
    sortBy('Код');

    expect(renderedOrder()).toEqual(['2', '1', '3']);
  });

  it('числова колонка сортується числом, а не текстом', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    sortBy('Аркушів');

    // ⛔ Мутаційний доказ: у `compareKeys` приберіть гілку `typeof a ===
    // 'number'` — порівняння піде через `Intl.Collator`, і `12` стане меншим
    // за `3`, тобто очікується `['3','2','1']` замість цього рядка.
    expect(renderedOrder()).toEqual(['3', '2', '1']);
  });

  it('порожні значення лишаються в кінці й при спаданні', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    fireEvent.click(screen.getByRole('button', { name: 'Власник' }));
    expect(renderedOrder()).toEqual(['3', '2', '1']);

    fireEvent.click(screen.getByRole('button', { name: 'Власник' }));

    // ⛔ Мутаційний доказ: застосуйте знак напрямку ЗОВНІ `compareKeys`
    // (`cmp * sign`) — рядок без власника (`id=1`) підніметься на перше місце,
    // і цей рядок падає.
    expect(renderedOrder()).toEqual(['2', '3', '1']);
  });

  it('стан сортування оголошується через aria-sort, а не самою стрілкою', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    const head = screen.getByRole('columnheader', { name: /Код/ });
    expect(head.getAttribute('aria-sort')).toBe('none');

    fireEvent.click(screen.getByRole('button', { name: 'Код' }));
    expect(head.getAttribute('aria-sort')).toBe('ascending');

    // Ім'я кнопки НЕ змінюється зі стрілкою: інакше читалка перечитувала б
    // колонку заново на кожну зміну напрямку.
    expect(screen.getByRole('button', { name: 'Код' })).toBeDefined();
  });

  it('колонка `sortable: false` кнопки не отримує', () => {
    renderWithMantine(
      <DataTable<Doc>
        columns={[{ key: 'code', label: 'Код', sortable: false }]}
        rows={docs}
        rowKey={(row) => row.id}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Код' })).toBeNull();
    expect(screen.getByRole('columnheader').getAttribute('aria-sort')).toBeNull();
  });
});

describe('«Showing N of M · Show more» поверх курсорної пагінації', () => {
  it('показує N і M і кличе переданий обробник', () => {
    const more = vi.fn();

    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={docs}
        rowKey={(row) => row.id}
        total={112}
        onShowMore={more}
        showMoreLabel="Показати ще"
      />,
    );

    /*
     * ⛔ Мутаційний доказ на ЧИСЛА: замініть `formatNumber(total)` на
     * `formatNumber(shown)` — підсумок стане «3 / 3», і цей рядок падає. Саме
     * ці два числа й відрізняють чесний підсумок від заглушки.
     */
    expect(screen.getByText('3 / 112')).toBeDefined();

    // ⛔ Мутаційний доказ на ДІЮ: приберіть `onClick={onShowMore}` — кнопка
    // лишиться на місці й натискатиметься, а лічильник викликів лишиться нулем.
    fireEvent.click(screen.getByRole('button', { name: 'Показати ще' }));
    expect(more).toHaveBeenCalledOnce();
  });

  it('коли показано все — кнопки немає, підсумок лишається', () => {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={docs}
        rowKey={(row) => row.id}
        total={docs.length}
        onShowMore={vi.fn()}
        showMoreLabel="Показати ще"
      />,
    );

    // Кнопка, яка нічого не додає, — той самий тупиковий екран, що й
    // «повторити» на відмові в праві.
    expect(screen.queryByRole('button', { name: 'Показати ще' })).toBeNull();
    expect(screen.getByText('3 / 3')).toBeDefined();
  });

  it('без `total` підсумок не малюється зовсім (D15-06)', () => {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={docs}
        rowKey={(row) => row.id}
        onShowMore={vi.fn()}
        showMoreLabel="Показати ще"
      />,
    );

    // «3 / 3» на першій сторінці курсорної вибірки було б НЕПРАВДОЮ, а не
    // заглушкою: скільки всього — сервер ще не сказав.
    expect(document.querySelector('[data-table-count]')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Показати ще' })).toBeNull();
  });
});

describe('L10: порожньо ≠ фільтр нічого не знайшов ≠ помилка', () => {
  /** Видимий текст усього подання. */
  function shownText(): string {
    return document.body.textContent ?? '';
  }

  function showEmpty(): string {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={[]}
        rowKey={(row) => row.id}
        emptyTitle="Документів ще немає"
        emptyHint="Створіть перший документ періоду."
      />,
    );

    const text = shownText();
    cleanup();

    return text;
  }

  function showFiltered(): string {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={[]}
        rowKey={(row) => row.id}
        filtered
        emptyTitle="Документів ще немає"
        emptyHint="Створіть перший документ періоду."
        noMatchTitle="Фільтр нічого не знайшов"
        noMatchHint="Спробуйте зняти фільтр за періодом."
        onClearFilters={vi.fn()}
        clearFiltersLabel="Зняти фільтри"
      />,
    );

    const text = shownText();
    cleanup();

    return text;
  }

  function showError(): string {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={undefined}
        rowKey={(row) => row.id}
        error={refusal()}
        emptyTitle="Документів ще немає"
      />,
    );

    const text = shownText();
    cleanup();

    return text;
  }

  it('три стани дають три РІЗНІ екрани', () => {
    const empty = showEmpty();
    const filtered = showFiltered();
    const failed = showError();

    /*
     * ⛔ Мутаційний доказ: у `DataTable.tsx` замініть
     * `kind === 'filtered' ? noMatchTitle : emptyTitle` на самий `emptyTitle`
     * (те саме для `hint` і `action`) — `empty` і `filtered` стануть
     * дослівно однаковими, і перший же `not.toBe` падає.
     */
    expect(empty).not.toBe(filtered);
    expect(filtered).not.toBe(failed);
    expect(empty).not.toBe(failed);
  });

  it('порожньо: пояснення без кнопки скидання фільтрів', () => {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={[]}
        rowKey={(row) => row.id}
        emptyTitle="Документів ще немає"
        emptyHint="Створіть перший документ періоду."
        onClearFilters={vi.fn()}
        clearFiltersLabel="Зняти фільтри"
      />,
    );

    expect(screen.getByText('Документів ще немає')).toBeDefined();

    // ⛔ Кнопка скидання тут була б брехнею: фільтра не застосовано, знімати
    // нічого. Мутаційний доказ: зробіть `stateAction` безумовно `clearButton`
    // — цей рядок падає.
    expect(screen.queryByRole('button', { name: 'Зняти фільтри' })).toBeNull();

    // І це НЕ помилка: `role="alert"` — саме те, чим вони розрізняються для
    // читалки (`AsyncBoundary.states.test.tsx`).
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('фільтр нічого не знайшов: власний текст і робоча кнопка скидання', () => {
    const clear = vi.fn();

    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={[]}
        rowKey={(row) => row.id}
        filtered
        emptyTitle="Документів ще немає"
        noMatchTitle="Фільтр нічого не знайшов"
        onClearFilters={clear}
        clearFiltersLabel="Зняти фільтри"
      />,
    );

    expect(screen.getByText('Фільтр нічого не знайшов')).toBeDefined();
    expect(screen.queryByText('Документів ще немає')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Зняти фільтри' }));
    expect(clear).toHaveBeenCalledOnce();
  });

  it('помилка: код і кореляція, кнопка «повторити», і НЕ «даних немає»', () => {
    const retry = vi.fn();

    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={undefined}
        rowKey={(row) => row.id}
        error={refusal()}
        onRetry={retry}
        emptyTitle="Документів ще немає"
      />,
    );

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('HTTP-500');
    expect(alert.textContent).toContain('cid-table-1');

    /*
     * ⛔ Саме це й ловить `ФВ-14.22`: невдалий запит теж лишає перелік
     * порожнім, і перевірка порожнечі раніше за помилку показала б «даних
     * немає» там, де сервер відмовив.
     */
    expect(screen.queryByText('Документів ще немає')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦common.retry⟧' }));
    expect(retry).toHaveBeenCalledOnce();
  });

  it('403 лишається окремим четвертим станом — без кнопки «повторити»', () => {
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={undefined}
        rowKey={(row) => row.id}
        error={
          new EcrApiError({
            title: 'Немає права на цей ресурс',
            detail: 'You do not have permission for this action.',
            status: 403,
            errorCode: 'ECR-AUTH-0403',
            correlationId: 'cid-table-403',
          })
        }
        onRetry={vi.fn()}
      />,
    );

    expect(screen.getByRole('alert').textContent).toContain('cid-table-403');
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('Розмітка таблиці', () => {
  it('шапка закріплена класом набору, а не власним стилем', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    // ⚠ Той самий клас, що вже стоїть на двадцяти таблицях застосунку
    // (`shared/theme/motion.css`). Друге закріплення, написане тут,
    // розійшлося б із ним на першій же зміні токена тла.
    expect(screen.getByRole('table').classList.contains('ecr-sticky-head')).toBe(true);
  });

  it('D15-06: порожнє значення лишає клітинку порожньою — без прочерку', () => {
    renderWithMantine(<DataTable<Doc> columns={columns} rows={docs} rowKey={(row) => row.id} />);

    const row = document.querySelector('[data-row-key="1"]');
    const owner = row?.querySelectorAll('td')[2];

    // ⛔ `KIT.md` §6.5 приписує тут «—»; директива №15 це знімає: прочерк —
    // це твердження «тут порожньо», яке в половині випадків неправда.
    expect(owner?.textContent).toBe('');
  });

  it('клац по рядку кличе обробник із самим рядком', () => {
    const open = vi.fn();

    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={docs}
        rowKey={(row) => row.id}
        onRowClick={open}
        rowLabel={(row) => `Документ ${row.code}`}
      />,
    );

    fireEvent.click(screen.getByRole('row', { name: 'Документ Gamma' }));
    expect(open).toHaveBeenCalledWith(docs[2]);
  });

  it('rowLabel повернув null — рядок лишається БЕЗ aria-label, а не з порожнім', () => {
    /*
     * ⛔ Цей випадок з'явився після ПЕРШОГО справжнього споживача
     * (`SourcesPage`). Із сигнатурою `=> string` єдиним способом «не ставити
     * підпис цьому рядку» був порожній рядок — а `aria-label=""` це теж
     * атрибут, і на `<tr>` він ЗАМІНЮЄ собою читання клітинок: рядок лишився
     * б узагалі без доступного імені. Тобто спроба НЕ зіпсувати доступність
     * псувала б її сильніше, ніж підпис на кожному рядку.
     *
     * ⚠ Перевіряється саме ВІДСУТНІСТЬ атрибута, а не його значення:
     * `getAttribute` поверне `''` і для `aria-label=""` теж, і жодне
     * порівняння рядків цих двох випадків не розрізнить.
     */
    renderWithMantine(
      <DataTable<Doc>
        columns={columns}
        rows={docs}
        rowKey={(row) => row.id}
        rowLabel={(row) => (row.code === 'Gamma' ? `Документ ${row.code}` : null)}
      />,
    );

    const named = document.querySelector('[data-row-key="3"]');
    const plain = document.querySelector('[data-row-key="1"]');

    expect(named?.getAttribute('aria-label')).toBe('Документ Gamma');
    expect(plain?.hasAttribute('aria-label')).toBe(false);

    // ⚠ І дзеркало: рядок без власного імені читається своїми клітинками.
    expect(plain?.textContent ?? '').toContain('alpha');
  });
});
