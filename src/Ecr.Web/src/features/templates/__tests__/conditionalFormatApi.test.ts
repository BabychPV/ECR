import { beforeEach, describe, expect, it, vi } from "vitest";
import { EcrApiError } from "@/api/client";
import {
  getConditionalFormats,
  isStaleConditionalFormats,
  saveConditionalFormats,
} from "../conditionalFormatApi";

/**
 * Транспорт правил умовного форматування (ФВ-2.6/2.7).
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `If-Match` з
 * `saveConditionalFormats` — червоніє «PUT несе If-Match»; брати `ETag` не із
 * заголовка — червоніють обидва «версія з ETag»; у `isStaleConditionalFormats`
 * прибрати перевірку `details.version` — червоніє «заморожена версія — не
 * застарілий набір».
 */
const apiFetchResponse = vi.hoisted(() => vi.fn());
vi.mock("@/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/client")>()),
  apiFetchResponse,
}));

const rules = [
  {
    columnCode: "VOL",
    operator: "gt",
    value: "10",
    valueTo: null,
    backgroundHex: "#ff0000",
    foregroundHex: null,
    isBold: true,
  },
  {
    columnCode: "VOL",
    operator: "empty",
    value: null,
    valueTo: null,
    backgroundHex: null,
    foregroundHex: null,
    isBold: false,
  },
];

function respond(body: unknown, etag: string): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json", ETag: etag },
  });
}

function conflict(extensions2?: Record<string, unknown>): EcrApiError {
  return new EcrApiError({
    title: "t",
    status: 409,
    errorCode: "ECR-TMPL-0409",
    correlationId: "c",
    ...(extensions2 === undefined ? {} : { extensions2 }),
  });
}

describe("conditionalFormatApi (ФВ-2.6/2.7)", () => {
  beforeEach(() => {
    apiFetchResponse.mockReset();
  });

  it("читає правила версії з GET …/conditional-formats і версію з ETag", async () => {
    apiFetchResponse.mockResolvedValue(respond(rules, '"A1"'));

    const set = await getConditionalFormats(42);

    expect(apiFetchResponse).toHaveBeenCalledWith(
      "/api/v1/template-versions/42/conditional-formats",
    );
    expect(set).toEqual({ rules, etag: '"A1"' });
  });

  it("PUT несе If-Match і тіло { rules } у порядку списку; нова версія з ETag", async () => {
    apiFetchResponse.mockResolvedValue(respond(rules, '"B2"'));

    const saved = await saveConditionalFormats(42, rules, '"A1"');

    const [path, init] = apiFetchResponse.mock.calls[0] as [
      string,
      { method: string; body: string; headers: Record<string, string> },
    ];
    expect(path).toBe("/api/v1/template-versions/42/conditional-formats");
    expect(init.method).toBe("PUT");
    expect(init.headers["If-Match"]).toBe('"A1"');
    expect(JSON.parse(init.body)).toEqual({ rules });
    expect(saved.etag).toBe('"B2"');
  });

  it("без прочитаної версії заголовок не вигадується", async () => {
    apiFetchResponse.mockResolvedValue(respond([], '"C3"'));

    await saveConditionalFormats(42, [], null);

    const [, init] = apiFetchResponse.mock.calls[0] as [
      string,
      { headers: Record<string, string> },
    ];
    expect("If-Match" in init.headers).toBe(false);
  });

  it("застарілий набір — 409 з details.version; заморожена версія — ні", () => {
    expect(isStaleConditionalFormats(conflict({ version: "ABC" }))).toBe(true);
    expect(isStaleConditionalFormats(conflict())).toBe(false);
    expect(isStaleConditionalFormats(new Error("x"))).toBe(false);
  });
});
