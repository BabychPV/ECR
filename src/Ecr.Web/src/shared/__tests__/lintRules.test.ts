import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, beforeAll } from 'vitest';
import { ESLint } from 'eslint';

/**
 * Доказ того, що правила лінтера СПРАЦЬОВУЮТЬ, а не просто присутні в конфігу.
 *
 * ⛔ Навіщо окремий набір. Поруч уже є `theme/__tests__/tokens.test.ts`, і він
 * перевіряє правила так: `expect(eslintConfig).toContain('ФВ-14.12')` — тобто
 * читає конфіг як ТЕКСТ і шукає підрядок. Такий тест лишиться зеленим, якщо
 * селектор написати з помилкою, якщо правило перевести в `warn`, якщо блок
 * потрапить під ширший `ignores` нижче за конфігом. Підрядок на місці —
 * перевірки немає. Тут замість цього запускається сам ESLint на навмисному
 * порушенні, і доказом є його діагностика.
 *
 * ⚠ Конфіг читається з диска той самий, що й у `npm run lint` (`overrideConfigFile`
 * не задано, `cwd` — корінь клієнта), тому тест не може розійтися з реальною
 * перевіркою: якщо розійдеться — він і впаде.
 */

const webRoot = path.resolve(process.cwd());

/*
 * ⚠ Один екземпляр на весь файл. Перший `lintText` розбирає конфіг і піднімає
 * парсер TypeScript — це ~12 с, і з новим екземпляром на кожен випадок перший
 * же `it` перевищував типову стелю 5 с.
 *
 * ⛔ **Холодний старт винесено в `beforeAll`, і це не косметика.** Раніше він
 * платився всередині ПЕРШОГО `it`, а стеля там стояла спільна — 30 с. На
 * теплій машині це вкладалося (увесь файл — 5 с), а на холодній, одразу після
 * `npm ci` і без кешу Vite, перший випадок валився за таймаутом: повний прогін
 * ішов 127 с замість 42 с, і `нативне <input type="date"> відхиляється`
 * червонів. Не через правило — через стелю.
 *
 * Це рівно той різновид червоного гейта, про який попереджає коментар у
 * `vitest.a11y.config.ts`: він падає НЕ з тієї причини, яку стереже, і навчає
 * читати «правила лінтера впали» як «машина повільна». Тепер ціна
 * ініціалізації має власну, названу стелю, а кожен випадок міряє рівно
 * спрацювання правила.
 */
let shared: ESLint | undefined;

function eslint(): ESLint {
  shared ??= new ESLint({ cwd: webRoot });

  return shared;
}

/*
 * ⚠ Прогрів робить САМЕ те, що роблять тести (`lintText` на фікстурі), а не
 * просто `new ESLint()`: конструктор дешевий, дорогий — перший розбір конфігу
 * й підняття парсера, а вони стаються на першому лінті.
 */
beforeAll(async () => {
  await lint('export const warm = 1;\n', 'src/features/probe/warm.ts');
}, 120_000);

async function lintResult(code: string, file: string): Promise<ESLint.LintResult | undefined> {
  const [result] = await eslint().lintText(code, {
    filePath: path.join(webRoot, file),
    warnIgnored: false,
  });

  return result;
}

async function lint(code: string, file: string): Promise<readonly string[]> {
  const result = await lintResult(code, file);

  return (result?.messages ?? []).map((m) => `${m.ruleId ?? '—'}: ${m.message}`);
}

function wrap(body: string): string {
  return `export function Probe(): JSX.Element {\n  return (\n${body}\n  );\n}\n`;
}

/*
 * ⚠ Стеля 10 с, а не 30: холодний старт уже сплачено в `beforeAll`, тож тут
 * міряється рівно один `lintText` на прогрітому екземплярі (~30 мс). Десять
 * секунд — це триста разів із запасом; якщо випадок їх вичерпає, це вже
 * справжня зміна, а не повільна машина.
 */
describe('D15-09 — формат дат і чисел не залежить від браузера', { timeout: 10_000 }, () => {
  it.each(['date', 'datetime-local', 'time', 'month', 'week'])(
    'нативне <input type="%s"> відхиляється',
    async (type) => {
      const messages = await lint(
        wrap(`    <input type="${type}" />`),
        'src/features/probe/Probe.tsx',
      );

      expect(messages.join('\n')).toContain('Нативне поле дати/часу');
    },
  );

  /*
   * ⛔ Це — головний випадок, а не додатковий. У `src` немає жодного голого
   * `<input type="date">`: усі вісім місць написані через `TextInput`
   * Mantine. Тест лише на `<input>` лишався б зеленим і тоді, коли правило не
   * бачить ЖОДНОГО реального порушення — рівно та пастка, через яку перша
   * редакція правила пройшла `npm run lint` без єдиної скарги.
   */
  it.each(['TextInput', 'NumberInput', 'Input'])(
    '<%s type="date"> Mantine відхиляється так само — під ним той самий нативний input',
    async (component) => {
      const messages = await lint(
        wrap(`    <${component} label="Дата" type="date" />`),
        'src/features/probe/Probe.tsx',
      );

      expect(messages.join('\n')).toContain('Нативне поле дати/часу');
    },
  );

  it('поле іншого типу лишається дозволеним — правило не ловить усе підряд', async () => {
    const messages = await lint(
      wrap('    <input type="text" />'),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).not.toContain('Нативне поле дати/часу');
  });

  it('DateInput Mantine не заборонений — формат там задає код', async () => {
    const messages = await lint(
      wrap('    <DateInput label="Дата" />'),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).not.toContain('Нативне поле дати/часу');
  });

  it.each(['toLocaleString', 'toLocaleDateString', 'toLocaleTimeString'])(
    '%s() без аргументів відхиляється',
    async (method) => {
      const messages = await lint(
        `export const probe = (value: Date): string => value.${method}();\n`,
        'src/features/probe/probe.ts',
      );

      expect(messages.join('\n')).toContain('бере локаль БРАУЗЕРА');
    },
  );

  it('undefined першим аргументом — те саме порушення, не обхід', async () => {
    const messages = await lint(
      "export const probe = (value: Date): string => value.toLocaleDateString(undefined, { day: '2-digit' });\n",
      'src/features/probe/probe.ts',
    );

    expect(messages.join('\n')).toContain('це локаль БРАУЗЕРА');
  });

  it('явна локаль продукту проходить', async () => {
    const messages = await lint(
      "export const probe = (value: Date, locale: string): string => value.toLocaleDateString(locale, { day: '2-digit' });\n",
      'src/features/probe/probe.ts',
    );

    expect(messages.join('\n')).not.toContain('локаль БРАУЗЕРА');
  });

  /*
   * ⛔ Третій канал, і його довелося заводити ПІСЛЯ того, як він спрацював у
   * продукті. Два правила вище стерегли нативне поле й `toLocale*()` — а
   * `useCellPatch.clockLabel` обійшов обидва найтихішим способом: склав
   * `ГГ:ХХ` руками, з коментарем, який пояснював, чому це нібито єдиний вихід.
   * Наслідок був на екрані: 24-годинний запис там, де решта сторінки писала
   * 12-годинний.
   */
  it.each(['getHours', 'getMinutes', 'getSeconds'])(
    'час, складений руками через %s(), відхиляється',
    async (method) => {
      const messages = await lint(
        `export const probe = (value: Date): string => String(value.${method}());\n`,
        'src/features/probe/probe.ts',
      );

      expect(messages.join('\n')).toContain('Час доби не складається руками');
    },
  );

  it.each(['getFullYear', 'getMonth', 'getDate'])(
    '%s() лишається дозволеним — з нього будують МАШИННІ формати',
    async (method) => {
      /*
       * ⛔ Це не послаблення, а межа правила, і вона має власну причину в
       * коді: `AuditPage.isoDaysAgo` складає `YYYY-MM-DD` саме так і свідомо
       * НЕ через `toISOString()` — той переводить у UTC і ввечері зсуває дату
       * на добу назад. Заборонити й це означало б зробити правило шумом, який
       * вимикають цілком.
       */
      const messages = await lint(
        `export const probe = (value: Date): string => String(value.${method}());\n`,
        'src/features/probe/probe.ts',
      );

      expect(messages.join('\n')).not.toContain('Час доби не складається руками');
    },
  );

  it('formatTime зі спільного модуля проходить', async () => {
    const messages = await lint(
      "import { formatTime } from '@/shared/format';\nexport const probe = (value: Date): string => formatTime(value);\n",
      'src/features/probe/probe.ts',
    );

    expect(messages.join('\n')).not.toContain('Час доби не складається руками');
  });
});

/**
 * Четвертий канал `D15-09` — мить, надрукована в розмітці БЕЗ форматування.
 *
 * ⛔ Чому окремий `describe`, а не ще один випадок вище. Три правила стережуть
 * те, ЯК дату форматують; це — те, що її не форматують узагалі. Порушення тут
 * не «неправильний виклик», а ВІДСУТНІСТЬ виклику, і жодне з трьох правил його
 * не бачило: сімнадцять місць друкували `2026-09-20T08:15:42.1234567Z` при
 * зеленому `npm run lint`.
 *
 * ⚠ Правило завелося лише тепер, коли тих місць нуль (`UI-07` закрито). Доти
 * воно зупиняло б збірку на першому ж і було б заведене з придушеннями — тобто
 * вимкненим. Лічильник придушень нижче це й доводить: він не зрушив.
 */
describe('D15-09 — сира мить у розмітці', { timeout: 10_000 }, () => {
  const Raw = 'Мить із сервера надрукована в розмітці як є';

  it.each(['createdAt', 'publishedAt', 'validFrom', 'effectiveTo'])(
    '{row.%s} у тілі елемента відхиляється',
    async (field) => {
      const messages = await lint(
        wrap(`    <Text>{row.${field}}</Text>`),
        'src/features/probe/Probe.tsx',
      );

      expect(messages.join('\n')).toContain(Raw);
    },
  );

  it('права частина && друкує так само — і так само заборонена', async () => {
    // ⛔ Найчастіший обхід прямого друку: обгорнути в перевірку на наявність.
    // Мить від цього читабельнішою не стає.
    const messages = await lint(
      wrap('    <Text>{row.createdAt !== null && row.createdAt}</Text>'),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).toContain(Raw);
  });

  it.each([
    ['гілка «так»', "{row.createdAt ? row.createdAt : '—'}"],
    ['гілка «ні»', "{row.createdAt === null ? '—' : row.createdAt}"],
  ])('тернарник, %s, друкує мить — заборонено', async (_case, body) => {
    const messages = await lint(wrap(`    <Text>${body}</Text>`), 'src/features/probe/Probe.tsx');

    expect(messages.join('\n')).toContain(Raw);
  });

  /*
   * ⛔ Нижче — МЕЖІ правила, і вони важливіші за випадки вище. Заборона, що
   * ловить і правильний спосіб теж, не переживе тижня: її вимкнуть цілком.
   */
  it('<Timestamp value={row.createdAt} /> — ПРАВИЛЬНИЙ спосіб, і він проходить', async () => {
    const messages = await lint(
      wrap('    <Timestamp value={row.createdAt} />'),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).not.toContain(Raw);
  });

  it('той самий проп усередині map — найчастіший взірець переліку — теж проходить', async () => {
    /*
     * ⛔ Саме цей випадок вимагає `>` (прямий нащадок) у селекторі замість
     * нащадка взагалі. З «нащадком» контейнер `{rows.map(…)}` містив би
     * `row.createdAt` із вкладеного пропа — і правило червоніло б на КОЖНІЙ
     * таблиці застосунку, де дату показують ПРАВИЛЬНО.
     */
    const messages = await lint(
      wrap(
        '    <Table>{rows.map((row) => (\n' +
          '      <Table.Tr key={row.id}><Table.Td><Timestamp value={row.createdAt} /></Table.Td></Table.Tr>\n' +
          '    ))}</Table>',
      ),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).not.toContain(Raw);
  });

  it('перевірка на наявність ліворуч від && — не друк, і не заборона', async () => {
    const messages = await lint(
      wrap('    <Text>{row.createdAt && <Timestamp value={row.createdAt} />}</Text>'),
      'src/features/probe/Probe.tsx',
    );

    expect(messages.join('\n')).not.toContain(Raw);
  });

  it('поле, що не є миттю, не чіпається — правило не ловить усе підряд', async () => {
    const messages = await lint(wrap('    <Text>{row.code}</Text>'), 'src/features/probe/Probe.tsx');

    expect(messages.join('\n')).not.toContain(Raw);
  });
});

describe('борг D15-09 обмежений і може лише скорочуватися', () => {
  /*
   * ⛔ Вісім наявних місць `<TextInput type="date">` не переводяться на
   * `DateInput` у цьому ж PR: там значення зі `string` стає `Date`, а разом із
   * ним — стан п'яти сторінок і їхні тести. Це окрема зміна поведінки, і
   * робити її разом із заведенням правила означало б змішати foundation з
   * функціональною правкою (CLAUDE.md §4).
   *
   * ⚠ Але «поки що вимкнено» без стелі перетворюється на «вимкнено назавжди»:
   * наступний автор допише дев'яте місце тим самим коментарем, і ніхто не
   * помітить. Тому число зафіксоване. Дев'яте придушення валить цей тест;
   * прибране — теж (число треба зменшити свідомо, а не «випадково зійшлося»).
   */
  const ExpectedSuppressions = 8;

  function sourceFiles(dir: string): readonly string[] {
    return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) return entry.name === 'node_modules' ? [] : sourceFiles(full);

      return /\.tsx?$/.test(entry.name) ? [full] : [];
    });
  }

  const suppressions = sourceFiles(path.join(webRoot, 'src'))
    .flatMap((file) =>
      readFileSync(file, 'utf8')
        .split('\n')
        .map((line, index) => ({ file, line: index + 1, text: line }))
        /*
         * ⚠ Саме КОМЕНТАР на початку рядка, а не будь-яка згадка підрядка:
         * перша редакція шукала `includes(...)` і знайшла сама себе — цей
         * файл згадує директиву в коді пошуку, і лічильник показав дев'ять.
         */
        .filter(({ text }) => /^\s*\/\/\s*eslint-disable-next-line\s+no-restricted-syntax/.test(text)),
    );

  it(`придушень рівно ${ExpectedSuppressions}`, () => {
    expect(
      suppressions.map((s) => `${path.relative(webRoot, s.file)}:${s.line}`),
    ).toHaveLength(ExpectedSuppressions);
  });

  it('кожне придушення називає причину й рішення, а не мовчить', () => {
    for (const s of suppressions) {
      expect(s.text, `${path.relative(webRoot, s.file)}:${s.line}`).toContain('--');
      expect(s.text, `${path.relative(webRoot, s.file)}:${s.line}`).toContain('D15-09');
    }
  });
});

describe('правила лінтера мають силу помилки, а не поради', { timeout: 10_000 }, () => {
  it('порушення зупиняє npm run lint', async () => {
    const result = await lintResult(
      wrap('    <input type="date" />'),
      'src/features/probe/Probe.tsx',
    );

    // `warningCount` лишив би білд зеленим — саме тому перевіряється errorCount.
    expect(result?.errorCount ?? 0).toBeGreaterThan(0);
    expect(result?.warningCount ?? 0).toBe(0);
  });
});
