import { describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConditionalFormatPanel } from "../ConditionalFormatPanel";
import { emptyRule, type ConditionalRule } from "../conditionalFormat";
import { renderWithMantine } from "@/test/render";

/**
 * Редактор умовного форматування (`ФВ-2.7`).
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати
 * `hasIncomplete` з `disabled` — червоніє «неповне правило блокує збереження»;
 * у перегляді замість `firstMatchingRule` поставити `null` — червоніє «перегляд
 * застосовує правило».
 */
const columns = [
  { code: "Q", label: "Quantity" },
  { code: "N", label: "Note" },
];

const complete: ConditionalRule = {
  ...emptyRule("Q"),
  value: "100",
  isBold: true,
};

describe("ConditionalFormatPanel", () => {
  it("«Зберегти» віддає набір правил; неповне правило блокує збереження", async () => {
    const onSave = vi.fn();
    renderWithMantine(
      <ConditionalFormatPanel
        columns={columns}
        initialRules={[complete]}
        onSave={onSave}
      />,
    );
    const user = userEvent.setup();

    await user.click(
      screen.getByRole("button", { name: /conditionalFormat\.save/ }),
    );
    expect(onSave).toHaveBeenCalledWith([complete]);

    await user.click(
      screen.getByRole("button", { name: /conditionalFormat\.add/ }),
    );
    expect(
      (
        screen.getByRole("button", {
          name: /conditionalFormat\.save/,
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });

  it("readOnly вимикає збереження", () => {
    renderWithMantine(
      <ConditionalFormatPanel
        columns={columns}
        initialRules={[complete]}
        onSave={vi.fn()}
        readOnly
      />,
    );

    expect(
      (
        screen.getByRole("button", {
          name: /conditionalFormat\.save/,
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });

  it("перегляд застосовує правило до значення-прикладу", async () => {
    renderWithMantine(
      <ConditionalFormatPanel
        columns={columns}
        initialRules={[complete]}
        onSave={vi.fn()}
      />,
    );
    const user = userEvent.setup();

    const preview = document.querySelector("[data-conditional-preview]");
    await user.type(
      screen.getByRole("textbox", { name: /conditionalFormat\.sample/ }),
      "150",
    );
    expect(preview?.getAttribute("data-conditional-preview")).toBe("match");
    expect((preview as HTMLElement | null)?.style.fontWeight).toBe("700");

    await user.clear(
      screen.getByRole("textbox", { name: /conditionalFormat\.sample/ }),
    );
    await user.type(
      screen.getByRole("textbox", { name: /conditionalFormat\.sample/ }),
      "50",
    );
    expect(preview?.getAttribute("data-conditional-preview")).toBe("none");
  });

  it("правило додається й видаляється", async () => {
    renderWithMantine(
      <ConditionalFormatPanel
        columns={columns}
        initialRules={[complete]}
        onSave={vi.fn()}
      />,
    );
    const user = userEvent.setup();

    await user.click(
      screen.getByRole("button", { name: /conditionalFormat\.add/ }),
    );
    expect(
      screen.getAllByRole("group", { name: /conditionalFormat\.rule/ }),
    ).toHaveLength(2);

    await user.click(
      screen.getByRole("button", { name: /conditionalFormat\.remove.*1/ }),
    );
    expect(
      screen.getAllByRole("group", { name: /conditionalFormat\.rule/ }),
    ).toHaveLength(1);
  });
});
