import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { UiStringsCsvPanel } from '@/features/localization/UiStringsCsvPanel';
import { loadCatalog, resetMissingReports } from '@/shared/i18n';
import { renderWithQuery } from '@/test/render';

/**
 * Обмін перекладом інтерфейсу через CSV (`BE-13` ч.2), з боку клієнта.
 *
 * ⛔ Що саме тут доводиться — і чим кожне твердження ламається:
 *   • вибір файлу надсилає `dryRun=true` і НІЧОГО більше. Прибери перегляд —
 *     падає перший тест;
 *   • «Застосувати» шле ТОЙ САМИЙ об'єкт `File` із `dryRun=false`. Збери
 *     новий `File` замість збереженого — падає другий;
 *   • «Застосувати» недоступне при непорожньому `errors`. Зніми умову —
 *     падає третій;
 *   • причина відмови показана текстом КАТАЛОГУ. Виведи `messageKey` як є —
 *     падає третій-таки;
 *   • без права `System.ManageLocalization` панелі немає. Прибери перевірку —
 *     падає четвертий.
 *
 * ⚠ Каталог тут ПІДНІМАЄТЬСЯ (`loadCatalog`), а не підставляється: інакше
 * `t()` віддає позначений ключ, і перевірка «текст, а не ключ» перевіряла б
 * сама себе. Що ці рядки є в `09-seed.sql` — стереже
 * `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт_є_в_каталозі`, і
 * саме тому `rowErrorText` перелічує їх літералами.
 */

const SlowEnvTimeout = 400_000;

/**
 * Рядки каталогу, які потрібні саме цьому екранові.
 *
 * ⚠ П'ять `uiStrings.exportCsv`/`uiStrings.import*` у `09-seed.sql` з'являться
 * НАСТУПНИМ комітом — сід заводить інтегратор. Доти
 * `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт_є_в_каталозі` і гейт
 * `a11y` червоні, і це очікуваний стан, а не те, що цей набір має замаскувати.
 * Тут рядки підставлені, бо набір перевіряє ІНШЕ: що напис береться з
 * КАТАЛОГУ, а не з коду. Питання «чи є цей рядок у сіді» — робота сторожа, і
 * відповідати на неї звідси означало б зробити його хибнозеленим.
 */
const Strings: Record<string, string> = {
  'uiStrings.language': 'Language',
  'uiStrings.key': 'Key',
  'uiStrings.saved': 'Saved; the catalogue is now at revision {revision}.',
  'uiStrings.exportCsv': 'Export CSV',
  'uiStrings.importCsv': 'Import CSV…',
  'uiStrings.importCounts': 'added {added}, updated {updated}, unchanged {unchanged}',
  'uiStrings.importBlockedHint':
    'Nothing has been written: fix the rows listed below and pick the file again.',
  'uiStrings.importReady': 'The file is valid: nothing to fix.',
  'import.title': 'Review the import',
  'import.row': 'Row',
  'import.reason': 'Reason',
  'import.apply': 'Apply',
  'import.rejected': '{count} rejected',
  'import.blockedTitle': 'This file cannot be applied as it is',
  'common.cancel': 'Cancel',
  'err.ECR-REQ-0422': 'Invalid request parameter',
  'err.ECR-REQ-0422.uiStringUnknownKey': 'This key does not exist in the default language.',
  'err.ECR-REQ-0422.uiStringEmptyValue': 'The translation is empty.',
  'err.ECR-REQ-0422.uiStringTooLong': 'The translation is longer than 1000 characters.',
  'err.ECR-REQ-0422.uiStringDuplicateKey': 'This key already appears earlier in the file.',
  'err.ECR-REQ-0422.placeholderMismatch':
    'The placeholders of "{key}" differ from the default language.',
};

/** Мови реєстру: еталон і дві мови перекладу — як у сіді. */
const Languages = [
  { code: 'en', nameNative: 'English', isDefault: true },
  { code: 'ru', nameNative: 'Русский', isDefault: false },
  { code: 'kz', nameNative: 'Қазақша', isDefault: false },
];

interface ImportCall {
  readonly url: string;
  readonly fileName: string;
}

let permissions: string[] = [];
let imports: ImportCall[] = [];
let report: unknown = null;
let exportUrls: string[] = [];
let fetchMock: ReturnType<typeof vi.fn>;
const downloads: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function reportOf(overrides: Record<string, unknown> = {}): unknown {
  return {
    added: 2,
    updated: 1,
    unchanged: 5,
    errors: [],
    applied: false,
    revision: 7,
    ...overrides,
  };
}

beforeEach(() => {
  permissions = ['System.ManageLocalization'];
  imports = [];
  exportUrls = [];
  downloads.length = 0;
  report = reportOf();

  fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (/\/api\/v1\/me(\?|$)/.test(url)) {
      return json({ userId: 1, userName: 'a', language: 'en', permissions });
    }

    if (url.includes('/api/v1/languages')) return json(Languages);

    if (url.includes('/api/v1/ui-strings/import')) {
      const body = init?.body as FormData;
      const sent = body.get('file');

      imports.push({ url, fileName: sent instanceof File ? sent.name : String(sent) });

      return json(report);
    }

    if (url.includes('/api/v1/ui-strings/export.csv')) {
      exportUrls.push(url);

      return new Response('key,scope,en,ru,updatedAt\r\n', {
        status: 200,
        headers: {
          'Content-Type': 'text/csv',
          'Content-Disposition': 'attachment; filename=ui-strings-ru.csv',
        },
      });
    }

    if (url.includes('/api/v1/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: Strings });
    }

    throw new Error(`неочікуваний запит у тесті: ${url}`);
  });

  vi.stubGlobal('fetch', fetchMock);

  let counter = 0;
  URL.createObjectURL = vi.fn(() => `blob:test/${String(++counter)}`);
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
    this: HTMLAnchorElement,
  ) {
    downloads.push(this.download);
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetMissingReports();
  localStorage.clear();
});

/** Піднімає каталог і малює панель. */
async function show(): Promise<void> {
  await act(async () => {
    await loadCatalog('en', 'private');
  });

  renderWithQuery(<UiStringsCsvPanel />);
}

/** Кладе файл у прихований `input[type=file]` і повідомляє про вибір. */
function pick(name: string): void {
  const file = new File(['key,ru\r\na.key,Альфа\r\n'], name, { type: 'text/csv' });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');

  if (input === null) throw new Error('прихованого input[type=file] немає в DOM');

  // ⚠ `files` у jsdom має лише читача, тож значення ставиться дескриптором:
  // `fireEvent.change(input, { target: { files } })` мовчки не робить нічого.
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  fireEvent.change(input);
}

async function importButton(): Promise<HTMLButtonElement> {
  return (await screen.findByRole(
    'button',
    { name: /Import CSV/ },
    { timeout: SlowEnvTimeout },
  )) as HTMLButtonElement;
}

async function applyButton(): Promise<HTMLButtonElement> {
  return (await screen.findByRole(
    'button',
    { name: 'Apply' },
    { timeout: SlowEnvTimeout },
  )) as HTMLButtonElement;
}

describe('UiStringsCsvPanel: обмін перекладом через CSV', () => {
  it(
    'вибір файлу надсилає ЛИШЕ перевірку — dryRun=true, жодного запису',
    async () => {
      await show();
      await importButton();

      pick('ui-strings-ru.csv');

      await screen.findByTestId('ui-strings-import-report', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: постав у виклику `dryRun: false` (або прапорець зі
      // стану, який на першому кроці ще `false`) — обидва твердження нижче
      // падають одночасно.
      expect(imports).toHaveLength(1);
      expect(imports[0]?.url).toContain('dryRun=true');
      expect(imports.some((call) => call.url.includes('dryRun=false'))).toBe(false);
      expect(imports[0]?.url).toContain('lang=ru');
    },
    SlowEnvTimeout,
  );

  it(
    'без помилок — «Застосувати» доступне і шле ТОЙ САМИЙ файл із dryRun=false',
    async () => {
      await show();
      await importButton();

      pick('translation.csv');

      const apply = await applyButton();

      // Дзеркало до наступного тесту: порожній `errors` — кнопка жива, і на
      // екрані сказано, що виправляти нічого.
      expect(apply.disabled).toBe(false);
      expect(screen.getByText(/nothing to fix/i)).toBeTruthy();
      expect(screen.queryByText('This file cannot be applied as it is')).toBeNull();

      fireEvent.click(apply);

      await waitFor(() => expect(imports).toHaveLength(2), { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: збери новий `File` замість збереженого — ім'я
      // розійдеться; лиши `dryRun=true` вдруге — запису не буде взагалі.
      expect(imports[1]?.url).toContain('dryRun=false');
      expect(imports[1]?.fileName).toBe('translation.csv');
      expect(imports[0]?.fileName).toBe('translation.csv');

      // Звіт закривається лише після успішного запису.
      await waitFor(() => expect(screen.queryByTestId('ui-strings-import-report')).toBeNull(), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );

  it(
    'є помилки стрічок — «Застосувати» недоступне, причини показані текстом каталогу',
    async () => {
      report = reportOf({
        added: 0,
        updated: 0,
        errors: [
          { row: 2, key: 'no.such.key', messageKey: 'err.ECR-REQ-0422.uiStringUnknownKey' },
          { row: 5, key: 'a.key', messageKey: 'err.ECR-REQ-0422.uiStringDuplicateKey' },
        ],
      });

      await show();
      await importButton();

      pick('broken.csv');

      const apply = await applyButton();

      // ⛔ Мутаційний доказ №1: зніми `!isApplicable(report)` з `disabled` —
      // кнопка стає живою при двох відхилених стрічках, і клієнт обіцяє
      // часткове застосування, якого сервер не робить ніколи.
      expect(apply.disabled).toBe(true);

      // ⛔ Мутаційний доказ №2: покажи `failure.messageKey` замість
      // `rowErrorText(...)` — обидва речення зникнуть, а на екрані лишиться
      // `err.ECR-REQ-0422.uiStringUnknownKey`.
      expect(screen.getByText('This key does not exist in the default language.')).toBeTruthy();
      expect(screen.getByText('This key already appears earlier in the file.')).toBeTruthy();
      expect(screen.queryByText('err.ECR-REQ-0422.uiStringUnknownKey')).toBeNull();
      expect(screen.queryByText('err.ECR-REQ-0422.uiStringDuplicateKey')).toBeNull();

      // Причина «нічого не записано» названа, і номери стрічок на місці.
      expect(screen.getByText('This file cannot be applied as it is')).toBeTruthy();
      expect(screen.getByText('2')).toBeTruthy();
      expect(screen.getByText('5')).toBeTruthy();

      // ⛔ І головне: натискання нічого не надсилає — лишається один виклик,
      // і він перевірка.
      fireEvent.click(apply);
      expect(imports).toHaveLength(1);
      expect(imports[0]?.url).toContain('dryRun=true');
    },
    SlowEnvTimeout,
  );

  it(
    'без права System.ManageLocalization панелі немає зовсім',
    async () => {
      permissions = ['Document.View'];

      await show();

      // ⚠ Чекаємо саме на відповідь `/me`: без очікування «кнопок немає» було
      // б правдою просто тому, що профіль ще не доїхав.
      await waitFor(
        () => {
          expect(
            fetchMock.mock.calls.some(([input]) => /\/api\/v1\/me(\?|$)/.test(String(input))),
          ).toBe(true);
        },
        { timeout: SlowEnvTimeout },
      );

      // ⛔ Мутаційний доказ: прибери `if (!can(...)) return null` — обидві
      // кнопки з'являться тому, кому сервер відповість `403`.
      await waitFor(
        () => {
          expect(screen.queryByRole('button', { name: /Import CSV/ })).toBeNull();
          expect(screen.queryByRole('button', { name: /Export CSV/ })).toBeNull();
        },
        { timeout: SlowEnvTimeout },
      );

      expect(document.querySelector('input[type="file"]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'мови-еталона серед цілей немає: сервер відмовляє їй і в експорті, і в імпорті',
    async () => {
      await show();

      const field = (await screen.findByLabelText(
        'Language',
        {},
        { timeout: SlowEnvTimeout },
      )) as HTMLInputElement;

      // Перша мова перекладу з реєстру, а не еталон.
      expect(field.value).toBe('Русский');

      fireEvent.click(field);

      const options = await screen.findAllByRole('option', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: віддай із `translationLanguages` перелік як є —
      // серед варіантів з'явиться English, і кожен експорт нею дасть 422
      // `uiStringCsvLanguage`.
      expect(options.map((option) => option.textContent)).toEqual(['Русский', 'Қазақша']);
    },
    SlowEnvTimeout,
  );

  it(
    'експорт віддає файл з іменем, яке назвав сервер',
    async () => {
      await show();

      fireEvent.click(
        await screen.findByRole('button', { name: /Export CSV/ }, { timeout: SlowEnvTimeout }),
      );

      await waitFor(() => expect(downloads).toHaveLength(1), { timeout: SlowEnvTimeout });

      expect(downloads[0]).toBe('ui-strings-ru.csv');
      expect(exportUrls[0]).toContain('lang=ru');
    },
    SlowEnvTimeout,
  );
});
