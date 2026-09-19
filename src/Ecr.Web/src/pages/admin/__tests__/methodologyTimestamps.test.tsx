import type { ReactElement } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { formatDate } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: дата набуття чинності версії методології — читабельна на екрані,
 * точна в розмітці.
 *
 * ⛔ Навіщо окремо від `shared/ui/__tests__/Timestamp.test.tsx`. Той доводить
 * поведінку КОМПОНЕНТА і лишиться зеленим, якщо жодна сторінка його не
 * викличе — рівно в цьому стані модуль `shared/format` і прожив увесь час
 * (написаний, протестований, без жодного споживача, сімнадцять місць друкували
 * сирий ISO). Тут перевіряється інше твердження: конкретний екран показує
 * момент через набір.
 *
 * ⚠ Обидві половини обов'язкові, і поодинці кожна порожня:
 *   • сама лише рівність із `formatDate(...)` лишилася б зеленою на
 *     форматувальнику, що повертає вхід;
 *   • саме лише «текст не дорівнює входу» лишилося б зеленим на чому завгодно,
 *     що зіпсувало значення.
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а НЕ пишеться літералом:
 * літерал був би перевіркою версії ICU у Node і червонів би від оновлення
 * середовища, нічого не зламавши.
 */

/*
 * ⛔ Панелі вмісту версії тут вимкнені — той самий прийом, що в
 * `MethodologyVersionsPage.publish.test.tsx`. Предмет цього набору — одна
 * колонка таблиці версій; з живими панелями до неї домішалися б сім чужих
 * запитів, і падіння читалося б як дефект дати.
 */
vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyConstantsPanel: () => null,
  MethodologyOutputsPanel: () => null,
  MethodologyRulesPanel: () => null,
  MethodologyRequiredInputsPanel: () => null,
  MethodologyTestsPanel: () => null,
  MethodologyBindingsPanel: () => null,
  MethodologyModesForm: () => null,
}));

const Session = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Calculation.View'],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

/** Дата набуття чинності опублікованої версії — календарна (`Format: date`). */
const EffectiveFrom = '2026-03-01';

/** Версія конфігуратора (`MethodologyDraftVersionDto`). */
const PublishedDraftVersion = {
  id: 10,
  versionNumber: '1.0',
  status: 'Published',
  isEditable: false,
  effectiveFrom: EffectiveFrom,
  numericMode: 'Strict',
  calendarMode: 'Actual',
  traceLevel: 'Off',
  createdByUserId: 1,
  level: 'Configuration',
};

/** Чернетка: дати чинності ще НЕМАЄ — це не «безстроково», це «нема чого». */
const DraftVersion = {
  ...PublishedDraftVersion,
  id: 11,
  versionNumber: '1.1',
  status: 'Draft',
  isEditable: true,
  effectiveFrom: null,
};

/**
 * Заглушка мережі.
 *
 * ⚠ `endsWith('/api/v1/me')`, а не `includes`: `/api/v1/methodologies` містить
 * підрядок `/api/v1/me` (перші літери «me»thodologies) — та сама пастка, що
 * вже описана в `MethodologyVersionsPage.publish.test.tsx`.
 *
 * ⚠ Форми відповідей звірені з викликами сторінок, а не вгадані:
 * `MethodologiesPage` читає `apiFetch<MethodologyDto[]>('/api/v1/methodologies')`
 * — МАСИВ, не `{ items: [...] }`; `methodologyVersions()` — теж масив.
 */
function respond(routes: readonly (readonly [string, unknown])[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.endsWith('/api/v1/me')) return json(Session);

      for (const [needle, body] of routes) {
        if (url.includes(needle)) return json(body);
      }

      return json(null);
    }),
  );
}

function show(node: ReactElement, path: string, url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route path={path} element={node} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Вузол `<time>` із точно цим `dateTime`. */
function timeNode(iso: string): HTMLElement | null {
  return document.querySelector(`time[datetime="${iso}"]`);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('MethodologyVersionsPage: дата набуття чинності версії', () => {
  it(
    'читабельна на екрані, точна в розмітці — і без вигаданої години',
    async () => {
      respond([
        ['/api/v1/methodologies/1/versions', [PublishedDraftVersion, DraftVersion]],
        ['/api/v1/units', []],
      ]);
      show(
        <MethodologyVersionsPage />,
        '/admin/methodologies/:id/versions',
        '/admin/methodologies/1/versions',
      );

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const node = timeNode(EffectiveFrom);

      expect(node, 'дата чинності не пройшла через набір').not.toBeNull();
      expect(node?.textContent).toBe(formatDate(EffectiveFrom));

      // ⛔ Друга половина: точне значення нікуди не діло́ся — воно в `dateTime`.
      expect(node?.getAttribute('title')).toBe(EffectiveFrom);

      // ⛔ Перша половина: видимий текст НЕ дорівнює сирому входу.
      expect(node?.textContent).not.toBe(EffectiveFrom);

      /*
       * ⚠ `dateOnly`: календарна дата не має години взагалі, і приписати їй
       * «12:00 AM» означало б показати точність, якої в даних немає.
       */
      expect(node?.textContent).not.toMatch(/\d{1,2}:\d{2}/);
    },
    SlowEnvTimeout,
  );

  it(
    'чернетка без дати чинності — видиме тире, а не «безстроково» і не порожня комірка',
    async () => {
      respond([
        ['/api/v1/methodologies/1/versions', [DraftVersion]],
        ['/api/v1/units', []],
      ]);
      show(
        <MethodologyVersionsPage />,
        '/admin/methodologies/:id/versions',
        '/admin/methodologies/1/versions',
      );

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      /*
       * ⚠ Тут дефолт `Timestamp` доречний, на відміну від вікна дії константи:
       * `effectiveFrom: null` у `MethodologyDraftVersionDto` означає «чернетка,
       * ще не опублікована» — значення справді немає.
       */
      expect(document.querySelector('[data-timestamp="none"]')?.textContent).toBe('—');
      expect(document.querySelector('time')).toBeNull();
    },
    SlowEnvTimeout,
  );
});

describe('MethodologiesPage: дата чинності в бейджі версії', () => {
  const methodology = {
    id: 1,
    code: 'M1',
    group: null,
    nameL10n: { values: { en: 'Метан' } },
    versions: [
      {
        id: 10,
        versionNumber: '1.0',
        status: 'Published',
        level: 'Configuration',
        numericMode: 'Strict',
        calendarMode: 'Actual',
        traceLevel: 'Off',
        effectiveFrom: EffectiveFrom,
        effectiveTo: null,
      },
    ],
  };

  it(
    'бейдж розкладено на вузли: момент читабельний, точне значення лишилось у розмітці',
    async () => {
      respond([['/api/v1/methodologies', [methodology]]]);
      show(<MethodologiesPage />, '/admin/methodologies', '/admin/methodologies');

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const node = timeNode(EffectiveFrom);

      /*
       * ⛔ Саме це твердження й відрізняє обраний варіант від `formatDate(...)`
       * у шаблонному рядку: там текст теж став би читабельним, але точного
       * значення не лишилося б ДЕ — рядок не має атрибутів. Тут воно є.
       */
      expect(node, 'дата чинності не пройшла через набір').not.toBeNull();
      expect(node?.textContent).toBe(formatDate(EffectiveFrom));
      expect(node?.textContent).not.toBe(EffectiveFrom);
      expect(node?.textContent).not.toMatch(/\d{1,2}:\d{2}/);

      // ⚠ Роздільник не з'їдено розкладанням: бейдж і далі читається одним
      // рядком «1.0 · Configuration · Mar 1, 2026».
      expect(node?.parentElement?.textContent).toContain(`· ${formatDate(EffectiveFrom)}`);
    },
    SlowEnvTimeout,
  );
});
