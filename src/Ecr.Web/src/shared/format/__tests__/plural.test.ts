import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { loadCatalog, resetMissingReports, setLanguage } from '@/shared/i18n';
import { formatCount, pluralCategory } from '@/shared/format';

/**
 * Множина (`D15-08`): `Intl.PluralRules` + ключі `.one/.few/.many/.other`.
 *
 * ⛔ Англійська логіка «1 або решта» ламається на `ru` не в екзотичному
 * випадку, а на другому ж числі: 1 строка, 2 строкИ, 5 строК, 21 строКА.
 * Число 21 тут важливіше за решту — воно повертається до форми ОДИНИЦІ, тобто
 * валить навіть обережне «1 — окремо, все інше — разом».
 *
 * ⚠ Казахська перевіряється окремо і з ІНШИМ твердженням: форм у ній дві
 * (`one`/`other`), рівно як в англійській. Тому доказ для `kk` — не кількість
 * форм, а те, що на 21 вона розходиться з російською; а доказ «локаль взято
 * правильно» для казахської живе в `locale.test.ts`, на числах і датах.
 */

/** Каталог рядків, як його віддає сервер. */
function catalog(languageCode: string, strings: Record<string, string>): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"public-${languageCode}-1"` },
  });
}

const Catalogs: Record<string, Record<string, string>> = {
  ru: {
    'rows.one': '{count} строка',
    'rows.few': '{count} строки',
    'rows.many': '{count} строк',
    'rows.other': '{count} строки дробных',
  },
  kz: {
    'rows.one': '{count} жол',
    'rows.other': '{count} жол (көпше)',
  },
};

beforeAll(async () => {
  // ⚠ Обидва каталоги завантажуються ДО тестів, а не всередині кожного:
  // `loadCatalog` тримає їх у модульній мапі, і повторне завантаження тієї ж
  // мови нічого б не додало, зате зробило б тести залежними від порядку.
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      for (const [lang, strings] of Object.entries(Catalogs)) {
        if (url.includes(`/ui-strings/${lang}`)) return Promise.resolve(catalog(lang, strings));
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  await loadCatalog('ru', 'public');
  await loadCatalog('kz', 'public');

  vi.unstubAllGlobals();
});

afterEach(() => {
  localStorage.clear();
  resetMissingReports();
});

describe('ru: 1 / 2 / 5 / 21 дають РІЗНІ форми', () => {
  it('категорії такі, як у CLDR, а не «одне або решта»', () => {
    expect(pluralCategory(1, 'ru')).toBe('one');
    expect(pluralCategory(2, 'ru')).toBe('few');
    expect(pluralCategory(5, 'ru')).toBe('many');
    expect(pluralCategory(21, 'ru')).toBe('one');
  });

  it('formatCount бере з каталогу саме ту форму', () => {
    setLanguage('ru');

    expect(formatCount(1, 'rows')).toBe('1 строка');
    expect(formatCount(2, 'rows')).toBe('2 строки');
    expect(formatCount(5, 'rows')).toBe('5 строк');
    expect(formatCount(21, 'rows')).toBe('21 строка');
  });

  it('1, 2 і 5 — три РІЗНІ написи, а 21 повертається до форми одиниці', () => {
    // ⛔ Це твердження, а не повтор попереднього: воно падає навіть тоді, коли
    // хтось «спростить» каталог, зробивши всі чотири форми однаковими, —
    // тобто стереже саме РОЗРІЗНЕННЯ, заради якого `D15-08` і існує.
    setLanguage('ru');

    const forms = [formatCount(1, 'rows'), formatCount(2, 'rows'), formatCount(5, 'rows')];

    expect(new Set(forms).size).toBe(3);
    expect(formatCount(21, 'rows').endsWith('строка')).toBe(true);
  });
});

describe('kk: форм дві, і на 21 вона розходиться з ru', () => {
  it('21 у казахській — other, у російській — one', () => {
    expect(pluralCategory(21, 'kz')).toBe('other');
    expect(pluralCategory(21, 'ru')).toBe('one');
  });

  it('formatCount казахською бере форми з казахського каталогу', () => {
    setLanguage('kz');

    expect(formatCount(1, 'rows')).toBe('1 жол');
    expect(formatCount(2, 'rows')).toBe('2 жол (көпше)');
    expect(formatCount(5, 'rows')).toBe('5 жол (көпше)');
    expect(formatCount(21, 'rows')).toBe('21 жол (көпше)');
  });

  /*
   * ⛔ Чого тут НЕМАЄ і чому. Твердження «категорію взято саме з `kk`, а не з
   * локалі середовища» на множині НЕ будується: категорії `kk` і `en`
   * збігаються повністю, а середовище прогону англійське — отже підміна
   * `kk → en` дала б рівно ті самі відповіді. Написати такий тест означало б
   * отримати зелене, яке нічого не доводить. Доказ для казахської живе там,
   * де розбіжність справді є, — `locale.test.ts`, числа й дати.
   */
});

describe('{count} підставляється відформатованим', () => {
  it('російське речення отримує число з роздільником тисяч, а не голе', () => {
    // ⛔ `String(count)` дав би «1234 строки» — напис, локалізований наполовину.
    setLanguage('ru');

    expect(formatCount(1234, 'rows').replace(/\s/g, ' ')).toBe('1 234 строки');
  });
});

describe('нескінченність і NaN не валять вибір форми', () => {
  it.each([Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY])(
    '%j дає other',
    (value) => {
      expect(pluralCategory(value, 'ru')).toBe('other');
    },
  );
});
