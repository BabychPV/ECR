import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { MethodologyModesForm } from '@/features/methodologies/MethodologyContentPanels';
import { testTheme } from '@/test/render';

/**
 * Аудит 2026-09-16, §10.5 (High, тиха псування даних): форма режимів
 * показувала й могла зберегти режими НЕ ТІЄЇ версії.
 *
 * ⛔ Локальний стан (`numericMode`/`calendarMode`/`traceLevel`) наповнювався
 * лише один раз — початковим значенням `useState` при монтуванні. Форма
 * рендериться з пропу `version = selected` того самого списку
 * (`MethodologyVersionsPage.tsx`), і перемикання версії в списку МІНЯЄ ПРОП,
 * не перемонтовуючи компонент: у `Select`-ах лишалися значення попередньої
 * версії, візуально не відрізнити від справжніх.
 *
 * **Сценарій:** адмін відкриває версію A (Legacy/Actual/Off), клацає версію B
 * (Strict/Fixed365/Full) — Select-и досі показують A. «Зберегти режими»
 * надсилає `saveMethodologyModes(methodologyId, B.id, { …значення A })`, тихо
 * перезаписуючи режими B значеннями A. Ціна названа у власних коментарях
 * коду: обидва перші режими «тихо змінюють УСІ числа версії, не змінивши
 * жодної формули» (`ФВ-9.9`, `ФВ-16.11`).
 *
 * ✎ Тут `Select` підмінявся легким `<select>` «з тієї самої причини, що в
 * `CreateMappingModal.targetKindReset`» — тобто через нібито зависання
 * Mantine під jsdom. Причина зависання знайдена й усунена (рекурсія
 * jsdom ↔ nwsapi на станових псевдокласах — коментар у `src/test/setup.ts`),
 * тож режими читаються й міняються у справжніх `Select`-ах.
 */

function version(
  id: number,
  numericMode: 'Legacy' | 'Strict',
  calendarMode: 'Actual' | 'Fixed365' | 'Fixed360',
  traceLevel: 'Off' | 'ErrorsOnly' | 'Full',
): MethodologyDraftVersionDto {
  return {
    calendarMode,
    createdByUserId: 1,
    effectiveFrom: null,
    id,
    isEditable: true,
    level: 'Configuration',
    numericMode,
    status: 'Draft',
    traceLevel,
    versionNumber: `v${id}`,
  };
}

const VersionA = version(11, 'Legacy', 'Actual', 'Off');
const VersionB = version(22, 'Strict', 'Fixed365', 'Full');

/** Адреси й тіла надісланих `PUT …/modes`. */
const saved: { url: string; body: Record<string, unknown> }[] = [];

function mockServer(): void {
  saved.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT' && url.includes('/modes')) {
        saved.push({ url, body: JSON.parse(String(init.body)) as Record<string, unknown> });
      }

      return new Response(JSON.stringify(VersionB), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

const NumericLabel = '⟦methodologies.numericMode⟧';
const CalendarLabel = '⟦methodologies.calendarMode⟧';
const TraceLabel = '⟦methodologies.traceLevel⟧';

function show(): { switchToB: () => void } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  function Harness({ selected }: { selected: MethodologyDraftVersionDto }): JSX.Element {
    return (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MethodologyModesForm methodologyId={7} version={selected} editable />
        </QueryClientProvider>
      </MantineProvider>
    );
  }

  const view = render(<Harness selected={VersionA} />);

  // ⛔ Саме `rerender`, а не повторний `render`: перемикання версії в списку
  // МІНЯЄ ПРОП того самого змонтованого компонента. Новий `render` створив би
  // новий екземпляр і дефект не відтворився б узагалі.
  return { switchToB: () => view.rerender(<Harness selected={VersionB} />) };
}

/**
 * ✎ Борги 27.11 (п. 3): опції режимів — словами, як бейджі переліку (`versionModeLabels`), а збережене значення — те
 * саме перелічення. Поле `Select` показує ПІДПИС опції; значення перевіряються атрибутом `value` опцій і тілом `PUT`.
 */
const Label = {
  Legacy: '⟦methodologies.mode.numeric.Legacy⟧',
  Strict: '⟦methodologies.mode.numeric.Strict⟧',
  Actual: '⟦methodologies.mode.calendar.Actual⟧',
  Fixed365: '⟦methodologies.mode.calendar.Fixed365⟧',
  Fixed360: '⟦methodologies.mode.calendar.Fixed360⟧',
  Off: '⟦methodologies.mode.trace.Off⟧',
  ErrorsOnly: '⟦methodologies.mode.trace.ErrorsOnly⟧',
  Full: '⟦methodologies.mode.trace.Full⟧',
} as const;

/** Показаний підпис поля (Mantine `Select` показує підпис вибраної опції). */
function valueOf(label: string): string {
  return (screen.getByLabelText(label) as HTMLInputElement).value;
}

/** Обирає опцію в справжньому `Select`: розкрити список і клацнути опцію. */
async function pick(label: string, option: string): Promise<void> {
  fireEvent.click(screen.getByLabelText(label));
  // ⚠ Опції рендеряться в порталі поза формою — звідси `screen`.
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyModesForm: перемикання версії перемальовує режими (§10.5)', () => {
  it('після переходу з версії A на B у полях стоять режими B', () => {
    mockServer();
    const { switchToB } = show();

    expect(valueOf(NumericLabel)).toBe(Label.Legacy);
    expect(valueOf(CalendarLabel)).toBe(Label.Actual);
    expect(valueOf(TraceLabel)).toBe(Label.Off);

    switchToB();

    // ⛔ Мутаційний доказ (RED до фіксу): у полях лишалися Legacy/Actual/Off —
    // режими ЧУЖОЇ версії, візуально не відрізнити від справжніх.
    expect(valueOf(NumericLabel)).toBe(Label.Strict);
    expect(valueOf(CalendarLabel)).toBe(Label.Fixed365);
    expect(valueOf(TraceLabel)).toBe(Label.Full);
  });

  it('«Зберегти режими» після переходу надсилає режими B, а не A', async () => {
    mockServer();
    const { switchToB } = show();

    switchToB();
    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.saveModes⟧' }));

    await waitFor(() => expect(saved).toHaveLength(1));

    // Адреса завжди була правильною — і саме тому дефект був тихим: запит ішов
    // у версію B зі значеннями A.
    expect(saved[0]?.url).toContain('/versions/22/modes');
    expect(saved[0]?.body).toEqual({
      numericMode: 'Strict',
      calendarMode: 'Fixed365',
      traceLevel: 'Full',
    });
  });

  it('правка, зроблена ПІСЛЯ переходу, не втрачається через синхронізацію', async () => {
    mockServer();
    const { switchToB } = show();

    switchToB();
    await pick(TraceLabel, Label.ErrorsOnly);

    // ⚠ Зворотний бік фіксу: синхронізація мусить спрацювати РІВНО на зміну
    // версії, а не на кожен рендер — інакше вона затирала б власний вибір
    // адміна за мить після кліку.
    expect(valueOf(TraceLabel)).toBe(Label.ErrorsOnly);

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.saveModes⟧' }));
    await waitFor(() => expect(saved).toHaveLength(1));

    expect(saved[0]?.body).toEqual({
      numericMode: 'Strict',
      calendarMode: 'Fixed365',
      traceLevel: 'ErrorsOnly',
    });
  });

  it('опції — словами (як бейджі переліку), значення опцій — перелічення сервера', async () => {
    mockServer();
    show();

    const cases: readonly (readonly [string, readonly (keyof typeof Label)[]])[] = [
      [NumericLabel, ['Legacy', 'Strict']],
      [CalendarLabel, ['Actual', 'Fixed365', 'Fixed360']],
      [TraceLabel, ['Off', 'ErrorsOnly', 'Full']],
    ];
    for (const [field, values] of cases) {
      fireEvent.click(screen.getByRole('textbox', { name: field }));
      for (const value of values) {
        const option = await screen.findByRole('option', { name: Label[value] });
        expect(option.getAttribute('value')).toBe(value);

        // Сирого значення на кшталт «ErrorsOnly» серед підписів немає.
        expect(screen.queryByRole('option', { name: value })).toBeNull();
      }
    }
  });
});
