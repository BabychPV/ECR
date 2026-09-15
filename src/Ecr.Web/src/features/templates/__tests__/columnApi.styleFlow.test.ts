import { afterEach, describe, expect, it, vi } from 'vitest';
import { emptyColumnDraft } from '../column';
import { emptyStyleDraft } from '../style';
import { saveColumn } from '../columnApi';

/**
 * Директива `docs/build/directive-registry-lookup-and-cell-style.md`,
 * Частина B, PR B1: "готово коли" — створення/редагування стилю колонки
 * ПЕРСИСТИТЬ `StyleDef` і прив'язку до неї.
 *
 * ⚠ `saveColumn` — не сама persist-логіка (та на сервері, `StyleDefHandlers.cs`),
 * а ПОСЛІДОВНІСТЬ двох запитів клієнта. Довести можна саме її: стиль іде
 * ПЕРШИМ окремим `PUT …/styles/{code}`, і щойно повернений `id` — ТОЙ САМИЙ
 * `styleId`, що йде в тілі другого запиту (колонки). Переплутати порядок чи
 * забути підставити `id` означало б зберегти колонку без стилю, який щойно
 * "зберігся" на екрані.
 */

function mockServer(): { calls: { url: string; method: string; body: unknown }[] } {
  const calls: { url: string; method: string; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
      calls.push({ url, method, body });

      if (url.includes('/styles/')) {
        return new Response(
          JSON.stringify({
            id: 42,
            code: 'BoldStyle',
            fontName: null,
            fontSize: null,
            isBold: true,
            isItalic: false,
            foregroundArgb: null,
            backgroundArgb: null,
            borderJson: null,
            horizontalAlign: null,
            verticalAlign: null,
            wrapText: false,
            numberFormat: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(
        JSON.stringify({
          id: 1,
          code: 'C1',
          headerL10n: { values: { en: 'C1' } },
          ordinal: 0,
          dataType: 'Decimal',
          isRequired: false,
          isReadOnly: false,
          isHidden: false,
          precision: null,
          scale: null,
          defaultValue: null,
          displayFormat: null,
          styleId: 42,
          lookupRegistryDefId: null,
          lookupFilter: null,
          unitId: null,
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }),
  );

  return { calls };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('saveColumn: стиль зберігається ПЕРЕД колонкою, з отриманим id', () => {
  it('чернетка без стилю — рівно один запит, колонка з наявним styleId (або null)', async () => {
    const { calls } = mockServer();

    await saveColumn(1, 5, { ...emptyColumnDraft(1), code: 'C1', styleId: 7, style: null });

    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toContain('/columns/C1');
    expect((calls[0]?.body as { styleId?: number | null })?.styleId).toBe(7);
  });

  it('чернетка З власним стилем — ДВА запити: спершу стиль, потім колонка з ЙОГО id', async () => {
    const { calls } = mockServer();

    await saveColumn(1, 5, {
      ...emptyColumnDraft(1),
      code: 'C1',
      styleId: null,
      style: { ...emptyStyleDraft('BoldStyle'), isBold: true },
    });

    expect(calls).toHaveLength(2);

    expect(calls[0]?.url).toContain('/styles/BoldStyle');
    expect(calls[0]?.method).toBe('PUT');
    expect((calls[0]?.body as { isBold?: boolean })?.isBold).toBe(true);

    // ⛔ Мутаційний доказ: `styleId` другого запиту — це `id` ВІДПОВІДІ
    // першого (`42`), а НЕ будь-яке значення, яке форма могла тримати до
    // збереження (`draft.styleId` тут узагалі `null`).
    expect(calls[1]?.url).toContain('/columns/C1');
    expect(calls[1]?.method).toBe('PUT');
    expect((calls[1]?.body as { styleId?: number | null })?.styleId).toBe(42);
  });
});
