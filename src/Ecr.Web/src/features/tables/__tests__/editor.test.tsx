import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';

/**
 * Зв'язки таблиць налаштовуються у веб-інтерфейсі (`ФВ-2.13`).
 *
 * ⛔ Перевіряється **ланка цілком**, а не окремо форма і окремо модуль
 * запитів. `ФВ-2.13` каже не «форма вміє скласти тіло запиту», а «зв'язки
 * налаштовуються у вебі»: доказом цього є те, що дії людини на сторінці
 * доїжджають до маршруту сервера. Тест, який кликав би `relationBody` або
 * `saveTableRelation` напряму, довів би лише, що вони працюють, — і мовчав би
 * про те, чи є на екрані шлях, яким до них узагалі можна дійти. Саме так
 * `cfg.TableRelationDef` прожила шість етапів: таблиця, сутність і жодного
 * способу завести зв'язок інакше, ніж `INSERT` руками.
 *
 * ⚠ Каталог рядків не завантажений, тому підписи приходять ключами в `⟦…⟧`
 * (той самий підхід, що й у перегляді мапінгу). Перевіряються не написи, а
 * запит, який пішов у мережу.
 */
const Structure = {
  templateVersionId: 7,
  presentationRevision: 0,
  sheets: [
    {
      id: 1,
      code: 'Water',
      nameL10n: { values: { en: 'Water' } },
      ordinal: 1,
      tables: [
        {
          id: 5,
          code: 'Main',
          layoutKind: 'Matrix',
          rowMode: 'Fixed',
          maxDynamicRows: null,
          columns: [],
          rows: [],
        },
        {
          id: 6,
          code: 'Consolidation',
          layoutKind: 'Matrix',
          rowMode: 'Fixed',
          maxDynamicRows: null,
          columns: [],
          rows: [],
        },
      ],
    },
  ],
};

const Me = {
  userId: 1,
  userName: 'admin',
  language: 'en',
  permissions: ['Template.View', 'Template.Edit'],
  denies: [],
  grants: {},
  isSimulation: false,
  simulatedForUserId: null,
  mustChangePassword: false,
};

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: string | null;
}

/** Замокнена мережа; повертає перелік того, що пішло на сервер. */
function network(relations: unknown[], isEditable = true): Call[] {
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ url, method, body: (init?.body as string | undefined) ?? null });

      const body = ((): unknown => {
        if (url.endsWith('/api/v1/me')) return Me;
        if (url.includes('/structure')) return Structure;
        if (url.includes('/relations') && method === 'GET') return { isEditable, relations };

        // Відповідь на запис: сервер повертає збережений зв'язок.
        return {
          id: 11,
          code: 'Rollup7',
          relationKind: 'Rollup',
          sourceTableDefId: 5,
          sourceTableCode: 'Main',
          targetTableDefId: 6,
          targetTableCode: 'Consolidation',
          matchJson: '{"by":"RowKey"}',
          mapJson: null,
          onSourceChange: 0,
          isActive: true,
        };
      })();

      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );

  return calls;
}

function show(): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/3/versions/7/relations']}>
          <Routes>
            <Route
              path="/admin/templates/:id/versions/:versionId/relations"
              element={<TableRelationsPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Редактор зв’язків між таблицями', () => {
  it('ФВ-2.13: зв’язок заводиться з форми — сторінка шле PUT на маршрут зв’язків версії', async () => {
    const calls = network([]);
    render(show());

    fireEvent.click(await screen.findByText('⟦tables.newRelation⟧'));

    fireEvent.change(await screen.findByLabelText('⟦tables.relationCode⟧'), {
      target: { value: 'Rollup7' },
    });
    fireEvent.change(screen.getByLabelText('⟦tables.sourceTable⟧'), { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText('⟦tables.targetTable⟧'), { target: { value: '6' } });
    fireEvent.change(screen.getByLabelText('⟦tables.matchJson⟧'), {
      target: { value: '{"by":"RowKey"}' },
    });

    fireEvent.click(screen.getByText('⟦tables.saveRelation⟧'));

    // ⛔ Головне твердження кроку: дія в браузері доїхала до маршруту сервера.
    const saved = await vi.waitFor(() => {
      const put = calls.find((call) => call.method === 'PUT');
      expect(put).toBeDefined();

      return put!;
    });

    expect(saved.url).toBe('/api/v1/template-versions/7/relations/Rollup7');
    expect(JSON.parse(saved.body ?? '{}')).toEqual({
      sourceTableDefId: 5,
      targetTableDefId: 6,
      relationKind: 'Rollup',
      matchJson: '{"by":"RowKey"}',
      mapJson: null,
      onSourceChange: 0,
      isActive: true,
    });
  });

  it('таблиці для вибору приходять зі структури версії, а не з окремого маршруту', async () => {
    const calls = network([]);
    render(show());

    fireEvent.click(await screen.findByText('⟦tables.newRelation⟧'));

    // ⚠ Обидві таблиці версії пропонуються, і кожна названа кодом аркуша:
    // без цього два `Main` у списку не розрізнити.
    const source = (await screen.findByLabelText('⟦tables.sourceTable⟧')) as HTMLSelectElement;
    await vi.waitFor(() => {
      expect([...source.options].map((option) => option.textContent)).toEqual([
        '⟦tables.pickTable⟧',
        'Water · Main',
        'Water · Consolidation',
      ]);
    });

    // ⛔ Другого переліку таблиць немає за побудовою: він розійшовся б зі
    // структурою на першій же зміні.
    expect(calls.some((call) => call.url.endsWith('/tables'))).toBe(false);
  });

  it('опублікована версія показує зв’язки без кнопок правки', async () => {
    network(
      [
        {
          id: 11,
          code: 'Rollup7',
          relationKind: 'Rollup',
          sourceTableDefId: 5,
          sourceTableCode: 'Main',
          targetTableDefId: 6,
          targetTableCode: 'Consolidation',
          matchJson: '{"by":"RowKey"}',
          mapJson: null,
          onSourceChange: 0,
          isActive: true,
        },
      ],

      // Стан версії каже СЕРВЕР; клієнт не виводить його зі `status` сам.
      false,
    );
    render(show());

    expect(await screen.findByText('Rollup7')).toBeDefined();
    expect(screen.queryByText('⟦tables.editRelation⟧')).toBeNull();
    expect(screen.queryByText('⟦tables.newRelation⟧')).toBeNull();
    expect(screen.getByText('⟦tables.readOnly⟧')).toBeDefined();
  });

  it('на опублікованій версії БЕЗ зв’язків кнопки «новий зв’язок» теж немає', async () => {
    // ⛔ Саме той випадок, заради якого відповідь — конверт. Версія без
    // зв'язків найчастіша (механізм опційний), і поелементний `isEditable`
    // тут не сказав би нічого: кнопка стояла б на замороженій версії, а
    // сервер відмовив би `ECR-TMPL-0409`.
    network([], false);
    render(show());

    expect(await screen.findByText('⟦tables.readOnly⟧')).toBeDefined();
    expect(screen.queryByText('⟦tables.newRelation⟧')).toBeNull();
  });
});
