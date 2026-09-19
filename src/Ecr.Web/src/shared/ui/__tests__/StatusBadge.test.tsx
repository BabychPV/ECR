import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { loadCatalog, resetMissingReports, t } from '@/shared/i18n';
import { theme } from '@/shared/theme/theme';
import {
  StatusBadge,
  isKnownStatus,
  statusKey,
  statusTable,
  statusTone,
  type StatusKind,
  type StatusTone,
} from '@/shared/ui/StatusBadge';

/**
 * `UI-04`: статусний бейдж.
 *
 * ⛔ Очікування нижче виписані ДОСЛІВНО, а не взяті з `statusTable` компонента.
 * Тест, що ітерує ту саму таблицю, яку перевіряє, лишається зеленим після
 * будь-якої її зміни — він доводить лише те, що таблиця дорівнює сама собі.
 *
 * ⛔ Джерело очікувань — КОД СЕРВЕРА, а не `KIT.md`: макет називає стани,
 * яких у домені немає (`sheet/Returned`, `job/Done`, `version/Archived`,
 * `severity/Critical`, увесь `user`). Кожен рядок нижче супроводжується
 * переліком, з якого він узятий.
 */

/**
 * Каталог рядків інтерфейсу — `09-seed.sql`, ЄДИНЕ джерело написів.
 *
 * ⛔ Рядки-коментарі відкидаються ДО розбору, і це не охайність. Перша
 * редакція цього файлу читала сирий текст — і мутаційна проба «закоментувати
 * рядок `status.period.Grace`» пройшла ЗЕЛЕНОЮ: вимкнений рядок каталогу
 * виглядав для тесту точнісінько як робочий. Тобто перевірка «кожна пара має
 * рядок» не перевіряла нічого, що можна вимкнути найприроднішим способом.
 *
 * ⚠ Відкидається саме РЯДОК, що ПОЧИНАЄТЬСЯ з `--`, а не будь-яке входження:
 * у значеннях каталогу дефіси трапляються (`ECR-AUTH-0401`), і вирізання
 * всього після першого `--` з'їло б їх разом із рядком.
 */
const seed = readFileSync(
  path.resolve(process.cwd(), '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql'),
  'utf8',
)
  .split('\n')
  .filter((line) => !/^\s*--/.test(line))
  .join('\n');

/** Рядок каталогу: `(N'ключ', N'en', N'текст', область)`. */
const SeedRowRegex = /\(N'([^']+)',\s*N'([a-z]{2})',\s*N'((?:[^']|'')*)',\s*(\d)\)/g;

/** Усі англійські рядки каталогу, ключ → текст. */
function seededStrings(): Map<string, { text: string; scope: string }> {
  const rows = new Map<string, { text: string; scope: string }>();

  for (const match of seed.matchAll(SeedRowRegex)) {
    if (match[2] !== 'en') continue;

    rows.set(match[1] ?? '', { text: (match[3] ?? '').replace(/''/g, "'"), scope: match[4] ?? '' });
  }

  return rows;
}

const catalog = seededStrings();

/**
 * ⚠ Вміст монтується у власний вузол-пробу: `MantineProvider` кладе поруч
 * `<style>` із усією темою, і `container.textContent` після цього містить
 * двадцять кілобайтів CSS, а не текст компонента.
 */
function show(node: ReactNode): HTMLElement {
  const { container } = render(
    <MantineProvider theme={theme}>
      <div data-probe="">{node}</div>
    </MantineProvider>,
  );

  const probe = container.querySelector<HTMLElement>('[data-probe]');
  if (probe === null) throw new Error('пробний вузол не змонтувався');

  return probe;
}

/**
 * Коренева розмітка бейджа.
 *
 * ⛔ Не `getByText`: Mantine малює підпис у ВКЛАДЕНОМУ `<span class="label">`,
 * тож знайдений за текстом вузол не має ні `data-*`, ні стилю тону — і тест,
 * що читав би атрибути з нього, завжди бачив би `null` і не відрізнив би
 * зламаний тон від правильного.
 */
function badge(probe: HTMLElement, state: string): HTMLElement {
  const root = probe.querySelector<HTMLElement>(`[data-status-state="${state}"]`);
  if (root === null) throw new Error(`бейдж зі станом «${state}» не змонтувався`);

  return root;
}

/**
 * Завантажує в застосунок САМЕ ті рядки, що лежать у `09-seed.sql`.
 *
 * ⛔ Не вигаданий словник. Підставивши свої рядки, тест довів би лише те, що
 * компонент уміє показувати передане йому — тобто рівно те, чого цей крок і
 * позбувається. Тут ланцюг перевіряється цілком: рядок у сіді → каталог →
 * `t(statusKey(...))` → текст на екрані.
 */
beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (!url.includes('/ui-strings/')) throw new Error(`Тест не очікував запиту: ${url}`);

      const strings = Object.fromEntries([...catalog].map(([key, row]) => [key, row.text]));

      return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  await loadCatalog('en', 'private');
});

afterEach(() => {
  cleanup();
  resetMissingReports();
});

/**
 * Усі пари `kind × state`, виписані вручну з переліків домену.
 *
 * Джерела: `Ecr.Domain/Enums/Enums.cs` (`DocumentStatus`, `PeriodState`,
 * `TemplateVersionStatus`, `ProjectStatus`, `ValidationSeverity`),
 * `Ecr.Application/Integration/IntegrationHandlers.cs` (`KnownStates`),
 * `Ecr.Infrastructure/Jobs/QuartzJobScheduler.cs` (`Unknown`/`Unavailable`),
 * `Ecr.Api/Health/HealthReportDto.cs`, `Adapters.PiAf/CollectionRunner.cs`.
 */
const expected: readonly (readonly [StatusKind, string, StatusTone])[] = [
  ['sheet', 'Draft', 'neutral'],
  ['sheet', 'Submitted', 'info'],
  ['sheet', 'Approved', 'neutral'],
  ['sheet', 'Rejected', 'danger'],

  ['period', 'Scheduled', 'muted'],
  ['period', 'Open', 'neutral'],
  ['period', 'Grace', 'warning'],
  ['period', 'Closed', 'neutral'],

  ['job', 'Queued', 'neutral'],
  ['job', 'Running', 'info'],
  ['job', 'Succeeded', 'neutral'],
  ['job', 'Failed', 'danger'],
  ['job', 'Cancelled', 'muted'],
  ['job', 'Unknown', 'warning'],
  ['job', 'Unavailable', 'warning'],

  ['version', 'Draft', 'neutral'],
  ['version', 'Published', 'neutral'],
  ['version', 'Deprecated', 'muted'],

  ['project', 'Draft', 'neutral'],
  ['project', 'Active', 'neutral'],
  ['project', 'Archived', 'muted'],

  ['health', 'Healthy', 'neutral'],
  ['health', 'Degraded', 'warning'],
  ['health', 'Unhealthy', 'danger'],

  ['severity', 'Info', 'neutral'],
  ['severity', 'Warning', 'warning'],
  ['severity', 'Error', 'danger'],

  ['collectionRun', 'Succeeded', 'neutral'],
  ['collectionRun', 'Degraded', 'warning'],
  ['collectionRun', 'Failed', 'danger'],
];

describe('StatusBadge: стан → тон', () => {
  it.each(expected)('%s/%s — тон «%s»', (kind, state, tone) => {
    expect(statusTone(kind, state)).toBe(tone);
  });

  /*
   * ⛔ Контроль самого набору очікувань, у ДВА боки. Без нього мутація
   * «прибрати рядок із таблиці компонента» пройшла б непоміченою рівно тоді,
   * коли й тут забули рядок: два переліки розійшлися б, а тест лишився б
   * зеленим на тому, що від них лишилося.
   */
  it('перелік вичерпний: 30 пар, і таблиця компонента не має жодної зайвої', () => {
    expect(expected).toHaveLength(30);
    expect(expected.every(([kind, state]) => isKnownStatus(kind, state))).toBe(true);

    const inComponent = Object.entries(statusTable).flatMap(([kind, states]) =>
      Object.keys(states).map((state) => `${kind}/${state}`),
    );

    expect([...inComponent].sort()).toEqual(
      expected.map(([kind, state]) => `${kind}/${state}`).sort(),
    );
  });

  /*
   * ⛔ Мутаційний контроль у самому наборі: якби тон перестав розрізняти
   * стани (напр. `statusTone` завжди повертав би одне значення), цей рядок
   * показав би, що набір це ловить. Він же не дає звести таблицю до одного
   * тону «щоб було простіше».
   */
  it('таблиця РОЗРІЗНЯЄ стани: чотири змістовні тони, не один', () => {
    const loud = expected.filter(([, , tone]) => tone !== 'neutral');

    expect(loud.length).toBeGreaterThanOrEqual(12);

    // info · warning · danger · muted.
    expect(new Set(loud.map(([, , tone]) => tone)).size).toBe(4);
  });
});

describe('кожна пара має рядок у каталозі (директива №15 §2)', () => {
  it.each(expected)('%s/%s — рядок `status.<kind>.<state>` у 09-seed.sql є', (kind, state) => {
    const key = statusKey(kind, state);
    const row = catalog.get(key);

    // ⛔ Названа причина, а не мовчазний `undefined`: «рядка немає» не повинно
    // прочитатися як «рядок правильний».
    expect(row, `немає рядка ${key} у 09-seed.sql`).toBeDefined();
    expect(row?.text.trim().length ?? 0, `рядок ${key} порожній`).toBeGreaterThan(0);

    // Область приватна: статуси видно лише після входу.
    expect(row?.scope, `рядок ${key} має бути приватним (1)`).toBe('1');
  });

  /*
   * ⛔ Зворотний бік: у каталозі немає рядків `status.*` під стани, яких
   * компонент не знає. Інакше туди тихо заїхали б підписи з макета
   * (`status.sheet.Returned`, `status.user.Invited`) — мертві рядки, які
   * ніхто ніколи не покаже, і які виглядали б як доказ, що стан підтримано.
   */
  it('зайвих рядків `status.*` у каталозі немає', () => {
    const known = new Set(expected.map(([kind, state]) => statusKey(kind, state)));
    const orphans = [...catalog.keys()].filter((key) => key.startsWith('status.') && !known.has(key));

    expect(orphans).toEqual([]);
  });

  it('підпис на екрані — саме той рядок каталогу, а не код стану', () => {
    const probe = show(<StatusBadge kind="period" state="Scheduled" />);

    // `Not open yet` у сіді; код стану — `Scheduled`. Якби компонент малював
    // код (як `DocumentsPage` сьогодні), цей рядок упав би.
    expect(badge(probe, 'Scheduled').textContent).toBe(catalog.get('status.period.Scheduled')?.text);
    expect(badge(probe, 'Scheduled').textContent).not.toBe('Scheduled');
  });

  it('каталог справді завантажено — інакше все вище перевіряло б ⟦ключі⟧', () => {
    expect(t('status.sheet.Draft')).toBe('Draft');
    expect(t('status.sheet.Draft')).not.toContain('⟦');
  });
});

describe('StatusBadge: невідомий стан', () => {
  /*
   * ⚠ Це не гіпотетичний випадок: половина словників доходить до клієнта
   * нетипізованою — `sheetStates` як `{ [key: string]: string }`,
   * `JobStatus.state` і `HealthReportDto.status` як `string`.
   */
  it('тон — ПОПЕРЕДЖЕННЯ, не нейтральний: тихий читався б як «усе гаразд»', () => {
    expect(statusTone('job', 'Superseded')).toBe('warning');
    expect(statusTone('sheet', 'Returned')).toBe('warning');

    // ⛔ І не відмова: невідомий стан не є проблемою самого документа.
    expect(statusTone('job', 'Superseded')).not.toBe('danger');
  });

  it('помітність має три канали, і колір — лише один із них', () => {
    const root = badge(show(<StatusBadge kind="job" state="Superseded" />), 'Superseded');

    // 1. Тон.
    expect(root.getAttribute('data-status-tone')).toBe('warning');
    expect(root.getAttribute('style') ?? '').toContain('var(--ecr-warning)');

    // 2. Підпис: рядка в каталозі немає, тож `t()` віддає позначений ключ
    //    (`D-138`) — пропуск видно з першого погляду, а не лише в DevTools.
    expect(root.textContent).toBe('⟦status.job.Superseded⟧');

    // 3. Розмітка — для тестів і прогонів e2e.
    expect(root.getAttribute('data-status-known')).toBe('false');
  });

  it('відомий стан позначений як відомий — атрибут не константа', () => {
    const root = badge(show(<StatusBadge kind="job" state="Failed" />), 'Failed');

    expect(root.getAttribute('data-status-known')).toBe('true');
    expect(root.textContent).toBe('Failed');
  });

  it('невідомий стан НЕ ламає сторінку — бейдж малюється', () => {
    const probe = show(<StatusBadge kind="period" state="Frozen" />);

    expect(badge(probe, 'Frozen')).toBeDefined();
  });
});

describe('StatusBadge: тон доходить до розмітки', () => {
  it('відмова малюється токеном відмови, а не нейтральним', () => {
    const probe = show(<StatusBadge kind="sheet" state="Rejected" />);
    const style = badge(probe, 'Rejected').getAttribute('style') ?? '';

    // ⛔ Саме токен, а не «якийсь колір»: мутація «завжди нейтральний» дала б
    // тут `var(--ecr-text)`, і рядок нижче впав би з видимою різницею.
    expect(style).toContain('var(--ecr-danger)');
    expect(style).not.toContain('var(--ecr-text)');
  });

  it('нейтральний бере власну пару токенів — текст і тло', () => {
    const probe = show(<StatusBadge kind="sheet" state="Draft" />);
    const style = badge(probe, 'Draft').getAttribute('style') ?? '';

    expect(style).toContain('var(--ecr-text)');
    expect(style).toContain('var(--ecr-sunken)');
  });

  it('«чекає дії» і «в роботі» — один тон info, з акцентними токенами', () => {
    const probe = show(
      <>
        <StatusBadge kind="sheet" state="Submitted" />
        <StatusBadge kind="job" state="Running" />
      </>,
    );

    for (const state of ['Submitted', 'Running']) {
      const style = badge(probe, state).getAttribute('style') ?? '';

      expect(style, state).toContain('var(--ecr-accent-text)');
      expect(style, state).toContain('var(--ecr-accent-soft)');
    }
  });

  it('бляклий відрізняється від нейтрального — інакше «архів» і «в роботі» однакові', () => {
    const probe = show(
      <>
        <StatusBadge kind="version" state="Deprecated" />
        <StatusBadge kind="version" state="Published" />
      </>,
    );

    expect(badge(probe, 'Deprecated').getAttribute('style') ?? '').toContain('var(--ecr-muted)');
    expect(badge(probe, 'Published').getAttribute('style') ?? '').toContain('var(--ecr-text)');
  });

  it('quiet прибирає заливку, лишаючи текст (щільні таблиці)', () => {
    const root = badge(show(<StatusBadge kind="job" state="Failed" quiet />), 'Failed');
    const style = root.getAttribute('style') ?? '';

    expect(root.getAttribute('data-variant')).toBe('transparent');
    expect(style).toContain('var(--ecr-danger)');
    expect(style).not.toContain('var(--ecr-sunken)');
  });

  it('за замовчуванням заливка є — рамка й тло з токенів теми', () => {
    const root = badge(show(<StatusBadge kind="job" state="Failed" />), 'Failed');

    expect(root.getAttribute('data-variant')).toBe('default');
    expect(root.getAttribute('style') ?? '').toContain('var(--ecr-sunken)');
  });
});

/**
 * Підпис не обрізається до першої літери (UI-аудит, lane 8).
 *
 * ⛔ Вимір живий, не здогад: `table-layout: auto` бере ширину стовпця з того,
 * що РЕНДЕРИТЬСЯ, а власний `overflow:hidden` у `.mantine-Badge-label` дозволяє
 * бейджу «поміститись» у будь-яку ширину замість того, щоб увімкнути
 * горизонтальну прокрутку таблиці. На ~554px «Scheduled» ставало «S…»:
 * `clientWidth` мітки 9px проти `scrollWidth` 65px.
 *
 * ⚠ Доказ структурний — jsdom не рахує розкладку взагалі
 * (`getBoundingClientRect` завжди нулі), тож будь-яке «виміряне» твердження
 * тут було б вигадкою. Перевіряється САМЕ той інлайн-стиль, який лікує дефект,
 * і саме на корені бейджа.
 *
 * ⛔ Перевіряється на КІЛЬКОХ різновидах і в ОБОХ варіантах заливки. Тест на
 * одному `period/Scheduled` лишався б зеленим і тоді, коли стиль повернули б
 * на сторінку — тобто доводив би не властивість набору, а один виклик; а тест
 * лише на `default` пропустив би `quiet`, де інша гілка складає пропи.
 */
describe('StatusBadge: підпис не стискається нижче власного тексту', () => {
  it.each([
    ['period', 'Scheduled'],
    ['job', 'Unavailable'],
    ['health', 'Degraded'],
    ['collectionRun', 'Failed'],
  ] as const)('%s/%s — min-width: fit-content на корені бейджа', (kind, state) => {
    const root = badge(show(<StatusBadge kind={kind} state={state} />), state);

    expect(root.style.minWidth, `${kind}/${state}`).toBe('fit-content');
  });

  it('`quiet` не втрачає мінімальної ширини — саме щільні таблиці її й потребують', () => {
    const root = badge(show(<StatusBadge kind="period" state="Scheduled" quiet />), 'Scheduled');

    expect(root.getAttribute('data-variant')).toBe('transparent');
    expect(root.style.minWidth).toBe('fit-content');
  });
});

describe('StatusBadge: один словник на різновид', () => {
  /*
   * ⛔ Те саме слово в різних різновидах — РІЗНІ словники. `Degraded` у
   * `health` і в `collectionRun` збігається випадково, а `Succeeded` у
   * `collectionRun` не має нічого спільного з `health`. Звести їх означало б,
   * що зміна одного мовчки перефарбує інший.
   */
  it('«Degraded» відомий обом різновидам, але кожен має власний рядок', () => {
    expect(isKnownStatus('health', 'Degraded')).toBe(true);
    expect(isKnownStatus('collectionRun', 'Degraded')).toBe(true);

    expect(statusKey('health', 'Degraded')).toBe('status.health.Degraded');
    expect(statusKey('collectionRun', 'Degraded')).toBe('status.collectionRun.Degraded');
  });

  it('«Healthy» належить лише health, «Succeeded» — лише job і collectionRun', () => {
    expect(isKnownStatus('collectionRun', 'Healthy')).toBe(false);
    expect(isKnownStatus('health', 'Succeeded')).toBe(false);
    expect(isKnownStatus('job', 'Succeeded')).toBe(true);
  });
});
