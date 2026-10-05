import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ColumnDto, TableSliceDto } from "@/api/types";
import { cancelAutosave } from "../autosave";
import { resetPending } from "../pendingStore";
import { DocumentGrid } from "../DocumentGrid";

/**
 * T4-02 (P1), покриття гейтом: Ctrl+V після закриття редактора комірки.
 *
 * ⛔ Чому v1 (`e9028f0c`) пройшов усі сім гейтів, хоча мав дірку з ризиком
 * псування даних («остання активна сітка» не скидалась ніколи — Ctrl+V зі
 * стороннього буфера після кліку повз сітку летів у сітку):
 *  - `bodyPaste.test.ts` v1 перевіряв `installBodyPasteRedirect` ізольовано й
 *    лише сценарії «має вставити» та «поле вводу / модалка не чіпається»;
 *    сценарію «клікнув повз сітку → чужий Ctrl+V НЕ пише» не було;
 *  - `e2e/gridPasteAfterEditor.spec.ts` v1 мав ті самі позитивні сценарії, а
 *    без стенда (`tools/e2e-stand.ps1`) у CI не виконується взагалі;
 *  - жоден тест не з'єднував слухач із СПРАВЖНІМ `onPaste` сітки, тож
 *    наслідок (PATCH у чужі комірки) не був видимий на рівні vitest.
 *
 * Тут — той самий шлях, що в застосунку: справжній `DocumentGrid`, справжній
 * `installBodyPasteRedirect`, справжній `trackSelection`, а мірило — тіла
 * `PATCH` на сервер. Заглушка RevoGrid шле справжню `CustomEvent('focuscell')`
 * так, як `revogr-overlay-selection` (див. `DocumentGrid.pasteAnchor.test.tsx`).
 *
 * ⚠ Що лишається за e2e (`e2e/gridPasteAfterEditor.spec.ts`, у CI не ганяється
 * без стенда): там `pasteAtSelection` сам обирає ціль події, тобто правило
 * Chrome «target за виділенням тексту» (першопричина T4-02) ще ніхто
 * автоматично не перевіряє. Потрібен сценарій зі СПРАВЖНІМ Ctrl+V:
 * `context.grantPermissions(['clipboard-read', 'clipboard-write'])`,
 * `navigator.clipboard.writeText('77')`, редактор комірки Enter/5/Esc,
 * `page.keyboard.press('Control+V')` -> рівно один PATCH r/C зі значенням 77;
 * потім клік у заголовок і знову `Control+V` -> 0 PATCH.
 *
 * ⚠ Без «0 PATCH за таймаут»: після кожної сторонньої вставки робиться
 * ЛЕГІТИМНА вставка всередину сітки (React-`onPaste`), і перевіряється, що в
 * PATCH поїхало лише її значення — тобто детерміновано, без годинника.
 */
vi.mock("@revolist/react-datagrid", () => ({
  RevoGrid: () => (
    <div data-testid="revogrid-stub" tabIndex={0}>
      <button
        type="button"
        onClick={(event) =>
          event.currentTarget.dispatchEvent(
            new CustomEvent("focuscell", {
              bubbles: true,
              composed: true,
              detail: {
                rowType: "rgRow",
                colType: "rgCol",
                focus: { x: 1, y: 2 },
                end: { x: 1, y: 2 },
                range: { x: 1, y: 2, x1: 1, y1: 2 },
              },
            }),
          )
        }
      >
        focus-r3-c2
      </button>
    </div>
  ),
}));

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: "Decimal",
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column("C1", 0), column("C2", 1), column("C3", 2)],
    rows: ["r1", "r2", "r3", "r4"].map((rowKey, index) => ({
      cells: { C1: `${rowKey}-1`, C2: `${rowKey}-2`, C3: `${rowKey}-3` },
      isOrphaned: false,
      label: null,
      ordinal: index,
      rowKey,
      rowVersion: `v${index}`,
      rowKind: "Item" as const,
    })),
  };
}

type PatchedRow = {
  rowKey: string;
  cells: { columnCode: string; value: unknown }[];
};
const patched: PatchedRow[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    "fetch",
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === "PATCH") {
        patched.push(
          ...(JSON.parse(String(init.body)) as { rows: PatchedRow[] }).rows,
        );

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }),
  );
}

/** Сторінка: абзац над сіткою (сюди Chrome шле `paste` за виділенням тексту) і кнопка поза сіткою. */
async function show(): Promise<{
  stub: HTMLElement;
  paragraph: HTMLElement;
  outsideButton: HTMLElement;
}> {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <p data-testid="page-paragraph">Заголовок документа</p>
        <button type="button">поза-сіткою</button>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return {
    stub: await screen.findByTestId("revogrid-stub"),
    paragraph: screen.getByTestId("page-paragraph"),
    outsideButton: screen.getByRole("button", { name: "поза-сіткою" }),
  };
}

/** `paste` у ціль, як її обирає браузер; повертає, чи хтось викликав `preventDefault`. */
function pasteInto(target: Element, text: string): boolean {
  return !fireEvent.paste(target, {
    clipboardData: { getData: () => text, setData: vi.fn() },
  });
}

const values = (): unknown[] =>
  patched.flatMap((row) => row.cells.map((cell) => cell.value));

/** Легітимна вставка всередину сітки — контрольна точка «PATCH уже пішов». */
async function controlPaste(stub: HTMLElement): Promise<void> {
  pasteInto(stub, "5\n");
  await waitFor(() => expect(values()).toContain("5"));
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  (document.activeElement as HTMLElement | null)?.blur?.();
});

describe("DocumentGrid + bodyPaste: Ctrl+V поза обгорткою сітки (T4-02)", () => {
  it("після фокуса комірки й фокуса на <body> paste в абзац поза сіткою пише у ВИДІЛЕНУ комірку", async () => {
    mockServer();
    const { paragraph } = await show();

    fireEvent.click(screen.getByRole("button", { name: "focus-r3-c2" }));
    // Редактор закрито Esc: фокус на `<body>`, виділення тексту — в абзаці.
    expect(document.activeElement).toBe(document.body);

    // ⛔ Мутаційний доказ: без `installBodyPasteRedirect` у `DocumentGrid`
    // (стан до T4-02) подію бачить лише document, PATCH не йде — тест червоний.
    expect(pasteInto(paragraph, "77\n")).toBe(true);

    await waitFor(() =>
      expect(patched.some((row) => row.rowKey === "r3")).toBe(true),
    );
    const row = patched.find((r) => r.rowKey === "r3");
    expect(row?.cells.map((cell) => [cell.columnCode, cell.value])).toEqual([
      ["C2", "77"],
    ]);
  });

  it("v1-дірка: клік повз сітку (абзац) -> сторонній Ctrl+V НЕ пише в сітку і не ковтає подію", async () => {
    mockServer();
    const { stub, paragraph } = await show();

    fireEvent.click(screen.getByRole("button", { name: "focus-r3-c2" }));
    fireEvent.pointerDown(paragraph);
    fireEvent.mouseDown(paragraph);

    // ⛔ Мутаційний доказ: з `bodyPaste.ts` v1 (`e9028f0c`, розозброєння немає)
    // `defaultPrevented === true` і '77' їде в r3/C2 — тест червоний.
    expect(pasteInto(paragraph, "77\n")).toBe(false);

    await controlPaste(stub);
    expect(values()).not.toContain("77");
  });

  it("v1-дірка: Tab на кнопку поза сіткою, потім фокус знову на <body> -> Ctrl+V НЕ пише в сітку", async () => {
    mockServer();
    const { stub, paragraph, outsideButton } = await show();

    fireEvent.click(screen.getByRole("button", { name: "focus-r3-c2" }));
    outsideButton.focus();
    // Кнопка зникла/втратила фокус: фокус знову на `<body>`, як у випадку T4-02.
    // Захищає лише розозброєння на `focusin`, а не перевірка `activeElement`.
    outsideButton.blur();
    expect(document.activeElement).toBe(document.body);

    // ⛔ Мутаційний доказ: без слухача `focusin` у `bodyPaste.ts` сітка лишається
    // озброєною, і '77' їде в r3/C2 — тест червоний.

    expect(pasteInto(paragraph, "77\n")).toBe(false);

    await controlPaste(stub);
    expect(values()).not.toContain("77");
  });

  it("сітку озброєно клавішею, але виділення немає -> НЕ пише в кут (0,0)", async () => {
    mockServer();
    const { stub, paragraph } = await show();

    // Активність у сітці без `focuscell`: `selection.current === null`.
    fireEvent.keyDown(stub, { key: "Shift", code: "ShiftLeft" });

    // ⛔ Мутаційний доказ: `hasSelection` у `DocumentGrid` замінено на
    // `() => true` -> `onPaste` бере `TableCornerAnchor` і '77' лягає в r1/C1.
    expect(pasteInto(paragraph, "77\n")).toBe(false);

    await controlPaste(stub);
    expect(values()).not.toContain("77");
  });
});
