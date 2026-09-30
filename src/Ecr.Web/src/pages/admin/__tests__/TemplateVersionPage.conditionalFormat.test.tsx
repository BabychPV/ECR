import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { theme } from "@/shared/theme/theme";
import { loadCatalog } from "@/shared/i18n";
import { TemplateVersionPage } from "@/pages/admin/TemplateVersionPage";

/**
 * Умовне форматування на сторінці версії (`ФВ-2.7`): правила читаються з
 * `GET …/conditional-formats`, «Зберегти» шле `PUT` цілого набору версії.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `others` зі
 * злиття в `saveConditionalFormatsMutation` — червоніє «правила інших таблиць
 * не губляться»; у `toWire` віддавати `''` замість `null` — червоніє
 * порівняння тіла PUT; зняти `disabled` для `readOnly` — червоніє «заморожена».
 */

const column = (id: number, code: string, ordinal: number): unknown => ({
  id,
  code,
  dataType: "Decimal",
  displayFormat: null,
  headerL10n: { values: { en: code } },
  isHidden: false,
  isReadOnly: false,
  isRequired: false,
  ordinal,
  unitSymbol: null,
  formulaExpression: null,
  formulaDialect: null,
});

const structure = {
  isEditable: true,
  presentationRevision: 0,
  groupRules: [],
  templateVersionId: 1,
  sheets: [
    {
      id: 1,
      code: "SHEET",
      nameL10n: { values: { en: "Sheet" } },
      isMandatory: true,
      isVisible: true,
      ordinal: 1,
      sheetGroup: null,
      tables: [
        {
          id: 10,
          code: "T10",
          nameL10n: { values: { en: "Table 10" } },
          layoutKind: "Static",
          maxDynamicRows: null,
          ordinal: 1,
          rowMode: "Fixed",
          columns: [column(100, "AAA", 0), column(101, "BBB", 1)],
          rows: [],
        },
      ],
    },
  ],
};

const stored = [
  {
    columnCode: "AAA",
    operator: "gt",
    value: "100",
    valueTo: null,
    backgroundHex: "#ff0000",
    foregroundHex: null,
    isBold: false,
  },
  {
    columnCode: "ZZZ",
    operator: "empty",
    value: null,
    valueTo: null,
    backgroundHex: null,
    foregroundHex: "#00ff00",
    isBold: true,
  },
];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

let puts: { rules: unknown[] }[] = [];

function stubFetch(versionStatus: string): void {
  puts = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes("/conditional-formats")) {
        if (init?.method === "PUT") {
          const body = JSON.parse(String(init.body)) as { rules: unknown[] };
          puts.push(body);
          return json(body.rules);
        }
        return json(stored);
      }
      if (url.includes("/ui-strings/"))
        return json({ languageCode: "en", revision: 1, strings: {} });
      if (url.includes("/me")) {
        return json({
          userId: 0,
          userName: "test",
          language: "en",
          permissions: ["Template.View", "Template.Edit"],
          isSimulation: false,
        });
      }
      if (url.includes("/structure")) return json(structure);
      if (url.includes("/versions?limit=")) {
        return json({
          items: [
            {
              id: 1,
              version: "1.0.0.0",
              status: versionStatus,
              presentationRevision: 0,
              clonedFromVersionId: null,
              publishedAt: null,
            },
          ],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

async function openEditor(versionStatus: string): Promise<void> {
  stubFetch(versionStatus);
  await loadCatalog("en", "public");
  await loadCatalog("en", "private");

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider
        client={
          new QueryClient({ defaultOptions: { queries: { retry: false } } })
        }
      >
        <MemoryRouter initialEntries={["/admin/templates/1/versions/1"]}>
          <Routes>
            <Route
              path="/admin/templates/:id/versions/:versionId"
              element={<TemplateVersionPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(
    await screen.findByRole("button", { name: /Sheet \(SHEET\)/ }),
  );
  await screen.findByText("(AAA)");
  await waitFor(() =>
    expect(screen.queryAllByRole("table").length).toBeGreaterThan(0),
  );
  fireEvent.click(
    screen.getByRole("button", { name: /conditionalFormat\.title/ }),
  );
}

beforeEach(() => {
  vi.stubGlobal(
    "ResizeObserver",
    class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("TemplateVersionPage — умовне форматування (ФВ-2.7)", () => {
  it("підтягує збережені правила й шле PUT; правила інших таблиць не губляться, порожнє — null", async () => {
    await openEditor("Draft");

    const value = await screen.findByRole("textbox", {
      name: /conditionalFormat\.value(?!To)/,
    });
    expect((value as HTMLInputElement).value).toBe("100");

    fireEvent.change(value, { target: { value: "200" } });
    fireEvent.click(
      screen.getByRole("button", { name: /conditionalFormat\.save/ }),
    );

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.rules).toEqual([
      stored[1],
      {
        columnCode: "AAA",
        operator: "gt",
        value: "200",
        valueTo: null,
        backgroundHex: "#ff0000",
        foregroundHex: null,
        isBold: false,
      },
    ]);
  });

  it("заморожена версія: збереження вимкнене", async () => {
    await openEditor("Published");

    await screen.findByRole("textbox", {
      name: /conditionalFormat\.value(?!To)/,
    });
    const save = screen.getByRole("button", {
      name: /conditionalFormat\.save/,
    }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });
});
