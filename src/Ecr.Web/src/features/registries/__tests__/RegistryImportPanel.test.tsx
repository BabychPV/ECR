import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { RegistryImportPanel } from '@/features/registries/RegistryImportPanel';
import { loadCatalog, resetMissingReports } from '@/shared/i18n';
import { renderWithQuery } from '@/test/render';

/**
 * Імпорт записів довідника з CSV (`BE-24`), з боку клієнта.
 *
 * ⛔ Що саме тут доводиться — і чим кожне твердження ламається:
 *   • вибір файлу надсилає лише `dryRun=true` і показує числа звіту
 *     ПОПАРНО зі своїми підписами. Переплутай місцями `added`/`updated` у
 *     розмітці — падає перший тест;
 *   • «Застосувати» шле ТОЙ САМИЙ об'єкт `File`, а не порожнє тіло і не
 *     новий файл. Забудь передати файл удруге — падає другий тест;
 *   • «Застосувати» недоступне, коли перегляд повернув хоч одну помилку.
 *     Зніми умову `disabled={blocked}` — падає третій тест;
 *   • `messageKey` рендериться текстом КАТАЛОГУ, а не сирим ключем. Виведи
 *     `error.messageKey` напряму — падає той самий третій тест.
 *
 * ⚠ Каталог тут ПІДНІМАЄТЬСЯ (`loadCatalog`), а не підставляється: інакше
 * `t()` завжди повертає позначений ключ (`⟦…⟧`), і перевірка «текст, а не
 * ключ» перевіряла б сама себе.
 */

const SlowEnvTimeout = 60_000;

/** Рядки каталогу, які потрібні саме цій панелі. */
const Strings: Record<string, string> = {
  'registry.import.pick': 'Import from CSV',
  'registry.import.title': 'Review the import',
  'registry.import.added': '{count} added',
  'registry.import.updated': '{count} updated',
  'registry.import.unchanged': '{count} unchanged',
  'registry.import.errorsCount': '{count} error(s)',
  'registry.import.blockedTitle': 'This file cannot be applied as it is',
  'registry.import.blockedHint': 'Fix the rows listed below and import the file again.',
  'registry.import.row': 'Row',
  'registry.import.entryKey': 'Code',
  'registry.import.field': 'Field',
  'registry.import.reason': 'Reason',
  'registry.import.apply': 'Apply',
  'registry.import.applied': '{added} added, {updated} updated, {unchanged} unchanged.',
  'common.cancel': 'Cancel',
  'err.ECR-REG-0422.unknownColumn': 'This column is not a known field of the registry.',
  'err.ECR-REG-0422.duplicateCode': 'This code appears earlier in the same file.',
};

interface ImportCall {
  readonly url: string;
  readonly fileName: string;
}

let imports: ImportCall[] = [];
let report: { errors: unknown[] } & Record<string, unknown>;
let fetchMock: ReturnType<typeof vi.fn>;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function reportOf(overrides: Record<string, unknown> = {}): { errors: unknown[] } & Record<string, unknown> {
  return {
    added: 3,
    updated: 2,
    unchanged: 9,
    errors: [],
    ...overrides,
  };
}

beforeEach(() => {
  imports = [];
  report = reportOf();

  fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (url.includes('/entries/import')) {
      const body = init?.body as FormData | undefined;
      const sent = body?.get('file');

      imports.push({ url, fileName: sent instanceof File ? sent.name : String(sent) });

      // ⚠ `applied` — обчислюється ТУТ, як і на сервері: `!dryRun &&
      // errors.length === 0`. Мокований звіт сам по собі кажучи «застосовано»
      // незалежно від `dryRun` приховав би дефект «забули передати
      // dryRun=false вдруге» — обидва виклики виглядали б застосованими.
      const dryRun = !url.includes('dryRun=false');
      return json({ ...report, applied: !dryRun && report.errors.length === 0 });
    }

    if (url.includes('/api/v1/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: Strings });
    }

    throw new Error(`неочікуваний запит у тесті: ${url}`);
  });

  vi.stubGlobal('fetch', fetchMock);
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

  renderWithQuery(<RegistryImportPanel registryCode="UNITS" />);
}

/** Кладе файл у прихований `input[type=file]` і повідомляє про вибір. */
function pick(name: string): void {
  const file = new File(['code,en\r\nKG,Kilogram\r\n'], name, { type: 'text/csv' });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');

  if (input === null) throw new Error('прихованого input[type=file] немає в DOM');

  // ⚠ `files` у jsdom має лише читача, тож значення ставиться дескриптором:
  // `fireEvent.change(input, { target: { files } })` мовчки не робить нічого.
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  fireEvent.change(input);
}

async function applyButton(): Promise<HTMLButtonElement> {
  return (await screen.findByRole(
    'button',
    { name: 'Apply' },
    { timeout: SlowEnvTimeout },
  )) as HTMLButtonElement;
}

describe('RegistryImportPanel: імпорт записів довідника з CSV', () => {
  it(
    'вибір файлу шле лише перевірку (dryRun=true) і показує числа звіту коректно',
    async () => {
      await show();
      fireEvent.click(await screen.findByRole('button', { name: 'Import from CSV' }));

      pick('units.csv');

      await applyButton();

      // ⛔ Мутаційний доказ (пункт 3 завдання): постав у виклику `dryRun=false`
      // на першому кроці — обидва твердження нижче падають одночасно.
      expect(imports).toHaveLength(1);
      expect(imports[0]?.url).toContain('dryRun=true');
      expect(imports.some((call) => call.url.includes('dryRun=false'))).toBe(false);

      // ⛔ Мутаційний доказ (пункт 2 завдання): переплутай місцями
      // `added`/`updated`/`unchanged` у розмітці — ці три твердження
      // розійдуться з мокованим звітом (3/2/9).
      expect(screen.getByText('3 added')).toBeTruthy();
      expect(screen.getByText('2 updated')).toBeTruthy();
      expect(screen.getByText('9 unchanged')).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    '«Застосувати» повторно шле ТОЙ САМИЙ файл із dryRun=false',
    async () => {
      await show();
      fireEvent.click(await screen.findByRole('button', { name: 'Import from CSV' }));

      pick('units.csv');

      const apply = await applyButton();
      expect(apply.disabled).toBe(false);

      fireEvent.click(apply);

      await waitFor(() => expect(imports).toHaveLength(2), { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ (пункт 3 завдання, «забути передати файл вдруге»):
      // якщо другий виклик надсилає тіло без файлу або новий порожній файл,
      // ім'я тут або відсутнє, або не збігається з першим викликом.
      expect(imports[1]?.url).toContain('dryRun=false');
      expect(imports[1]?.fileName).toBe('units.csv');
      expect(imports[0]?.fileName).toBe('units.csv');

      // Після успішного застосування (`applied: true` за замовчуванням
      // моку) діалог закривається і перелік перечитується.
      await waitFor(() => expect(screen.queryByText('Review the import')).toBeNull(), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );

  it(
    'є помилки рядків — «Застосувати» недоступне, причини показані текстом каталогу',
    async () => {
      report = reportOf({
        added: 0,
        updated: 0,
        errors: [
          { row: 2, key: 'XX', field: null, messageKey: 'err.ECR-REG-0422.unknownColumn' },
          { row: 5, key: 'KG', field: 'code', messageKey: 'err.ECR-REG-0422.duplicateCode' },
        ],
      });

      await show();
      fireEvent.click(await screen.findByRole('button', { name: 'Import from CSV' }));

      pick('broken.csv');

      const apply = await applyButton();

      // ⛔ Мутаційний доказ (пункт 1 завдання): зніми `disabled={blocked}` —
      // кнопка стає живою при двох відхилених рядках, хоча сервер не запише
      // нічого («усе-або-нічого»).
      expect(apply.disabled).toBe(true);

      // ⛔ Мутаційний доказ (пункт 4 завдання): виведи `error.messageKey`
      // напряму замість `t(error.messageKey)` — обидва речення нижче
      // зникнуть, а на екрані лишаться сирі ключі.
      expect(screen.getByText('This column is not a known field of the registry.')).toBeTruthy();
      expect(screen.getByText('This code appears earlier in the same file.')).toBeTruthy();
      expect(screen.queryByText('err.ECR-REG-0422.unknownColumn')).toBeNull();
      expect(screen.queryByText('err.ECR-REG-0422.duplicateCode')).toBeNull();

      // Причина блокування названа, і номери рядків на місці.
      expect(screen.getByText('This file cannot be applied as it is')).toBeTruthy();
      expect(screen.getByText('2')).toBeTruthy();
      expect(screen.getByText('5')).toBeTruthy();

      // Клік по недоступній кнопці нічого не надсилає — лишається один
      // виклик, і він перевірка.
      fireEvent.click(apply);
      expect(imports).toHaveLength(1);
      expect(imports[0]?.url).toContain('dryRun=true');
    },
    SlowEnvTimeout,
  );
});
