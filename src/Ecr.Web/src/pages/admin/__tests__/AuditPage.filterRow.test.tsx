import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';
import { t } from '@/shared/i18n';

/**
 * `U-21`: у рядах фільтрів журналу всі поля мають ОДНАКОВУ будову.
 *
 * ⛔ Що було. Ряд вирівняний `align="end"`; «By user» і «Row key» мали
 * `description` під підписом, «Origin» і «Column» — ні. Поле з поясненням
 * вище, тож підписи стояли на двох рівнях і ряд читався як зсунутий. Той самий
 * дефект мала шапка («From», «To» — без, «Document» — з поясненням) і ряд
 * вкладки структурних змін («Entity type» — без, «By user» — з).
 *
 * ⛔ Предмет — БУДОВА, а не пікселі. Знімок ловив би шрифт і тему; будова
 * «підпис + поле, без пояснення під підписом» — це і є умова, за якої ряд
 * стоїть на одній лінії в обох темах.
 *
 * ⛔ **Мутаційний доказ.** Повернути `description={t('audit.authorHint')}`
 * будь-якому полю (у `AuditPage` чи `StructureChangesPanel`) — червоніє
 * «жодне поле не має пояснення під підписом» відповідної вкладки. Прибрати
 * `aria-describedby` — червоніє «пояснення досяжне з поля»: винести текст
 * з-під підпису й загубити його для читалки було б іншим дефектом.
 */
function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: null }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
}

function show(search: string): HTMLElement {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/admin/audit${search}`]}>
        <QueryClientProvider client={client}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  ).container;
}

/** Усі обгортки полів вводу на екрані, крім прихованих (`display: none`). */
function visibleFields(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>('.mantine-InputWrapper-root')].filter(
    (field) => field.style.display !== 'none',
  );
}

/**
 * ВИДИМА будова поля: є підпис і НЕМАЄ пояснення в потоці між підписом і полем.
 *
 * ⚠ Пояснення поза потоком (`position: absolute`, `readerOnlyDescription`)
 * висоти не додає — воно для читалки, і саме воно дає полю `aria-describedby`.
 */
function shape(field: HTMLElement): string {
  const label = field.querySelector('.mantine-InputWrapper-label')?.textContent ?? '∅';
  const description = field.querySelector<HTMLElement>('.mantine-InputWrapper-description');
  const inFlow = description !== null && description.style.position !== 'absolute';

  return `${label}:${inFlow ? 'з поясненням' : 'підпис+поле'}`;
}

/**
 * Текст, на який посилається `aria-describedby` поля з цим підписом.
 *
 * ⛔ Не декорація: перша версія виправлення ставила власний `aria-describedby`,
 * і Mantine мовчки затирав його своїм `undefined` — саме цей помічник це й
 * зловив.
 */
function describedBy(label: string): string {
  const input = screen.getByLabelText(label);
  const ids = (input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);

  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 60_000;

describe('U-21: ряди фільтрів журналу стоять на одній лінії', () => {
  it(
    'журнал комірок: жодне поле не має пояснення під підписом',
    async () => {
      mockFetch();
      const root = show('?from=2026-01-01&to=2026-01-08');

      await screen.findByLabelText(t('audit.author'), {}, { timeout: 30_000 });

      const shapes = visibleFields(root).map(shape);

      // ⚠ Спершу — що поля взагалі знайшлися: порожній перелік пройшов би
      // перевірку нижче й нічого не довів би.
      expect(shapes.length).toBeGreaterThanOrEqual(7);
      expect(shapes.filter((s) => !s.endsWith('підпис+поле'))).toStrictEqual([]);
    },
    SlowEnvTimeout,
  );

  it(
    'журнал комірок: пояснення досяжне з поля, до якого воно належить',
    async () => {
      mockFetch();
      show('?from=2026-01-01&to=2026-01-08');

      await screen.findByLabelText(t('audit.author'), {}, { timeout: 30_000 });

      expect(describedBy(t('audit.author'))).toBe(t('audit.authorHint'));
      expect(describedBy(t('audit.rowKey'))).toBe(t('audit.cellHint'));
      expect(describedBy(t('audit.columnDefId'))).toBe(t('audit.cellHint'));
      expect(describedBy(t('audit.document'))).toBe(t('audit.documentHint'));

      // ⚠ Видимий текст — під рядом, і для читалки прихований: вона вже
      // отримала його через поле, двічі читати не треба.
      const hints = document.querySelector<HTMLElement>('[data-filter-hints="true"]');
      expect(hints?.getAttribute('aria-hidden')).toBe('true');
      expect(hints?.textContent).toContain(t('audit.cellHint'));
    },
    SlowEnvTimeout,
  );

  it(
    'структурні зміни: та сама будова ряду і те саме пояснення',
    async () => {
      mockFetch();
      const root = show('?view=structure&from=2026-01-01&to=2026-01-08');

      await screen.findByLabelText(t('audit.author'), {}, { timeout: 30_000 });

      const shapes = visibleFields(root).map(shape);

      expect(shapes.length).toBeGreaterThanOrEqual(4);
      expect(shapes.filter((s) => !s.endsWith('підпис+поле'))).toStrictEqual([]);
      expect(describedBy(t('audit.author'))).toBe(t('audit.authorHint'));
    },
    SlowEnvTimeout,
  );
});
