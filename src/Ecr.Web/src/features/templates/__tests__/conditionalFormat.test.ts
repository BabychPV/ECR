import { describe, expect, it } from "vitest";
import {
  emptyRule,
  firstMatchingRule,
  fromWire,
  ruleMatches,
  toWire,
  whyRuleIncomplete,
  type ConditionalRule,
} from "../conditionalFormat";

/**
 * Умовне форматування (`ФВ-2.7`): коли правило спрацьовує і яке з кількох.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): `>` замість `>=` у `ge`
 * — червоніє межа; у `between` прибрати `value <= upper` — червоніє «вище
 * межі»; у `firstMatchingRule` прибрати перевірку `whyRuleIncomplete` —
 * червоніє «неповне правило»; `eq` без числового порівняння — червоніє
 * «1.0 = 1».
 */
const rule = (patch: Partial<ConditionalRule>): ConditionalRule => ({
  ...emptyRule("Q"),
  backgroundHex: "#ff0000",
  ...patch,
});

describe("toWire / fromWire", () => {
  it("'' ↔ null; зайві операнди не їдуть на сервер", () => {
    const wire = toWire(
      rule({ operator: "empty", value: "5", valueTo: "9", isBold: true }),
    );
    expect(wire).toEqual({
      columnCode: "Q",
      operator: "empty",
      value: null,
      valueTo: null,
      backgroundHex: "#ff0000",
      foregroundHex: null,
      isBold: true,
    });
    expect(fromWire(wire)).toEqual(rule({ operator: "empty", isBold: true }));
  });

  it("between зберігає обидві межі", () => {
    expect(
      toWire(rule({ operator: "between", value: " 1 ", valueTo: "9" })),
    ).toMatchObject({ value: "1", valueTo: "9" });
  });
});

describe("ruleMatches", () => {
  it("порівняння чисел, включно з межами", () => {
    expect(ruleMatches(rule({ operator: "gt", value: "10" }), "10")).toBe(
      false,
    );
    expect(ruleMatches(rule({ operator: "ge", value: "10" }), "10")).toBe(true);
    expect(ruleMatches(rule({ operator: "lt", value: "0" }), "-0.5")).toBe(
      true,
    );
    expect(ruleMatches(rule({ operator: "le", value: "0" }), "0.1")).toBe(
      false,
    );
  });

  it("між — обидві межі включно, поза ними ні", () => {
    const between = rule({ operator: "between", value: "1", valueTo: "5" });
    expect(ruleMatches(between, "1")).toBe(true);
    expect(ruleMatches(between, "5")).toBe(true);
    expect(ruleMatches(between, "5.01")).toBe(false);
    expect(ruleMatches(between, "0.99")).toBe(false);
  });

  it("дорівнює: числа як числа, текст як текст", () => {
    expect(ruleMatches(rule({ operator: "eq", value: "1" }), "1.0")).toBe(true);
    expect(ruleMatches(rule({ operator: "eq", value: "Так" }), "Так")).toBe(
      true,
    );
    expect(ruleMatches(rule({ operator: "ne", value: "Так" }), "Ні")).toBe(
      true,
    );
  });

  it("порожня комірка: лише «порожньо» спрацьовує", () => {
    expect(ruleMatches(rule({ operator: "empty" }), null)).toBe(true);
    expect(ruleMatches(rule({ operator: "empty" }), "  ")).toBe(true);
    expect(ruleMatches(rule({ operator: "notEmpty" }), null)).toBe(false);
    expect(ruleMatches(rule({ operator: "lt", value: "1" }), null)).toBe(false);
  });

  it("текст на числовому порівнянні не спрацьовує", () => {
    expect(ruleMatches(rule({ operator: "gt", value: "1" }), "abc")).toBe(
      false,
    );
  });
});

describe("whyRuleIncomplete", () => {
  it("називає, чого бракує", () => {
    expect(whyRuleIncomplete(rule({ columnCode: "" }))).toBe("Column");
    expect(whyRuleIncomplete(rule({ operator: "gt", value: "abc" }))).toBe(
      "Value",
    );
    expect(
      whyRuleIncomplete(rule({ operator: "between", value: "1", valueTo: "" })),
    ).toBe("ValueTo");
    expect(
      whyRuleIncomplete(
        rule({ operator: "between", value: "5", valueTo: "1" }),
      ),
    ).toBe("Range");
    expect(
      whyRuleIncomplete(rule({ operator: "empty", backgroundHex: "" })),
    ).toBe("Style");
    expect(
      whyRuleIncomplete(rule({ operator: "eq", value: "Так" })),
    ).toBeNull();
  });
});

describe("firstMatchingRule", () => {
  const red = rule({ operator: "gt", value: "100", backgroundHex: "#ff0000" });
  const yellow = rule({
    operator: "gt",
    value: "50",
    backgroundHex: "#ffff00",
  });

  it("перше за порядком перемагає", () => {
    expect(firstMatchingRule([red, yellow], "Q", "150")).toBe(red);
    expect(firstMatchingRule([red, yellow], "Q", "70")).toBe(yellow);
    expect(firstMatchingRule([red, yellow], "Q", "10")).toBeNull();
  });

  it("лише своя колонка", () => {
    expect(firstMatchingRule([red], "OTHER", "150")).toBeNull();
  });

  it("неповне правило не застосовується, навіть якщо умова збігається", () => {
    const noStyle = rule({ operator: "gt", value: "0", backgroundHex: "" });
    expect(firstMatchingRule([noStyle, yellow], "Q", "70")).toBe(yellow);
  });
});
