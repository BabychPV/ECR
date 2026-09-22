import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyRulesPanel } from '@/features/methodologies/MethodologyContentPanels';
import type { MethodologyRuleDto } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Запобіжник, що деградував у бік ДОЗВОЛУ.
 *
 * Діалог правила відбору має два попередження про конфлікт
 * (`catchAllNotLowest`, `shadowedByCatchAll`), і обидва рахуються з переліку
 * `otherRules`, зібраного через `(rules.data ?? [])`. Кнопка «Додати правило»
 * стоїть ПОЗА `AsyncBoundary`, тож при відмові `GET …/rules`:
 *
 * - таблиця показує банер відмови (це було й до фікса),
 * - але діалог усе одно відкривається, і `otherRules` порожній,
 * - обидва попередження зникають (`maxOtherPriority === null`,
 *   `blockingCatchAll === undefined`),
 * - і catch-all (`{}`) із пріоритетом 1 зберігається МОВЧКИ, перекривши всі
 *   точніші правила назавжди.
 *
 * ⛔ Тобто рівно тоді, коли клієнт не знає, які правила вже є, він повідомляє,
 * що конфлікту немає. Той самий клас, що вже полагоджено в
 * `pages/admin/PeriodsPage.tsx` (архівація): коли стан невідомий, запобіжник
 * мусить деградувати в бік ЗАБОРОНИ, і причина має бути ВИДИМОЮ.
 *
 * Три випадки, і другий обов'язковий:
 *
 * - **А** — `GET …/rules` відмовляє: у діалозі видно причину з кодом відмови,
 *   кнопка збереження НЕДОСТУПНА;
 * - **Б** (дзеркало) — правила приїхали: кнопка ДОСТУПНА, банера немає. Без
 *   цього випадку «полагодити» можна було б назавжди заблокованою кнопкою;
 * - **В** — правила приїхали й чернетка конфліктує: попередження ВИДНО. Це
 *   наявна поведінка, і випадок тримає її межу — саме її й з'їдала відмова.
 */

const Strings: Record<string, string> = {
  'methodologies.rules': 'Rules',
  'methodologies.addRule': 'Add rule',
  'methodologies.code': 'Code',
  'methodologies.priority': 'Priority',
  'methodologies.priorityHint': 'The lower the number, the higher the priority',
  'methodologies.matchJson': 'Match',
  'methodologies.matchJsonHint': 'An empty object matches the whole table',
  'methodologies.active': 'Active',
  'methodologies.save': 'Save',
  'methodologies.editFormula': 'Edit',
  'methodologies.noRules': 'No rules yet',
  'methodologies.noRulesHint': 'Add the first one',
  'methodologies.catchAllNotLowestTitle': 'Catch-all is not the last rule',
  'methodologies.catchAllNotLowestWarning': 'Such a rule must have the lowest priority',
  'methodologies.shadowedByCatchAllTitle': 'This rule will never fire',
  'methodologies.shadowedByCatchAllWarning': 'Shadowed by {code}',
  'state.errorTitle': 'The request failed',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
  'common.loading': 'Loading…',
};

/**
 * ⚠ `messageKey` обов'язковий: без нього `problemText` ховає `detail`, і
 * твердження про текст відмови було б зеленим на будь-якому коді — банер
 * просто не мав би що показати. Прив'язуємось усе одно до КОДУ й кореляції:
 * вони показуються завжди й не залежать від каталогу.
 */
const RulesRefusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік правил прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-rules-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function rule(overrides: Partial<MethodologyRuleDto>): MethodologyRuleDto {
  return {
    id: overrides.id ?? 1,
    code: overrides.code ?? 'SPECIFIC',
    matchJson: overrides.matchJson ?? '{"tableCode":"T1"}',
    priority: overrides.priority ?? 1,
    isActive: overrides.isActive ?? true,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

/**
 * Сервер, у якому `…/rules` або відмовляє, або віддає перелік.
 *
 * ⚠ Ендпоінт заглушається саме у `fetch`: `vi.mock` на самому модулі панелей
 * тут не допоміг би — панелі приходять через `lazyPanel` → `import()`.
 */
function mockApi(rules: MethodologyRuleDto[] | 'refuse'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.includes('/rules')) {
        return rules === 'refuse' ? json(RulesRefusal, 500) : json(rules);
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyRulesPanel methodologyId={1} versionId={2} editable />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Стеля тесту: панель у jsdom піднімається за секунди. */
const SlowEnvTimeout = 30_000;

/**
 * Стеля очікування САМОГО твердження — навмисно нижча за стелю тесту: інакше
 * `waitFor` не встигає скласти свою відмову, і причина падіння зникає за
 * «Test timed out».
 */
const AssertTimeout = 10_000;

async function openDialog(): Promise<HTMLElement> {
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add rule' }, { timeout: SlowEnvTimeout }),
  );

  return screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
}

/**
 * Заповнює код чернетки.
 *
 * ⛔ Без цього кнопка «Зберегти» недоступна САМА ПО СОБІ
 * (`editing.code.trim().length === 0`), і твердження «недоступна» було б
 * зеленим на будь-якому коді, включно з тим, що мав дефект.
 */
function fillCode(dialog: HTMLElement, code: string): void {
  fireEvent.change(within(dialog).getByLabelText('Code'), { target: { value: code } });
}

function saveButton(dialog: HTMLElement): HTMLElement {
  return within(dialog).getByRole('button', { name: 'Save' });
}

/**
 * Банер відмови шукається за СТАБІЛЬНИМ КОДОМ, а не за роллю: `role="alert"`
 * у діалозі може поставити й Mantine `Alert` іншого призначення.
 */
function refusalBanner(dialog: HTMLElement): HTMLElement | undefined {
  return within(dialog)
    .queryAllByRole('alert')
    .find((element) => (element.textContent ?? '').includes('ECR-SYS-0500'));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyRulesPanel: невідомий перелік правил блокує збереження', () => {
  it(
    'А: `GET …/rules` відмовив — причина з кодом у діалозі, «Зберегти» НЕДОСТУПНА',
    async () => {
      mockApi('refuse');
      await show();

      const dialog = await openDialog();

      const banner = await waitFor(
        () => {
          const found = refusalBanner(dialog);
          if (found === undefined) {
            throw new Error('у діалозі немає банера з кодом відмови ECR-SYS-0500');
          }
          return found;
        },
        { timeout: AssertTimeout },
      );

      expect(banner.textContent ?? '').toContain('cid-rules-1');
      expect(banner.textContent ?? '').toContain('перелік правил прочитати не вдалося');

      // ⛔ Головне: код і матч заповнені, тобто базові умови збереження
      // виконані — і кнопка все одно недоступна, бо перелік НЕВІДОМИЙ.
      fillCode(dialog, 'NEW');
      expect(within(dialog).getByLabelText('Code')).toHaveProperty('value', 'NEW');
      expect(saveButton(dialog)).toHaveProperty('disabled', true);
    },
    SlowEnvTimeout,
  );

  it(
    'Б: правила приїхали — «Зберегти» ДОСТУПНА, банера відмови немає',
    async () => {
      mockApi([rule({ id: 1, code: 'SPECIFIC', priority: 1 })]);
      await show();

      // ⚠ Спершу дочекатись ДАНИХ: доти перелік так само невідомий, і обидва
      // твердження нижче були б зеленими на будь-якому коді.
      await screen.findByText('SPECIFIC', {}, { timeout: SlowEnvTimeout });

      const dialog = await openDialog();
      fillCode(dialog, 'NEW');

      await waitFor(
        () => {
          expect(saveButton(dialog)).toHaveProperty('disabled', false);
        },
        { timeout: AssertTimeout },
      );

      expect(refusalBanner(dialog)).toBeUndefined();
    },
    SlowEnvTimeout,
  );

  it(
    'В: правила приїхали — попередження про конфлікт catch-all ВИДНО',
    async () => {
      // Наявне точніше правило на пріоритеті 1; чернетка нового правила —
      // catch-all (`{}`) теж на 1. Саме цей конфлікт і зникав при відмові.
      mockApi([rule({ id: 1, code: 'SPECIFIC', priority: 1 })]);
      await show();

      await screen.findByText('SPECIFIC', {}, { timeout: SlowEnvTimeout });

      const dialog = await openDialog();

      expect(
        await within(dialog).findByText(
          'Catch-all is not the last rule',
          {},
          { timeout: AssertTimeout },
        ),
      ).not.toBeNull();
    },
    SlowEnvTimeout,
  );
});
