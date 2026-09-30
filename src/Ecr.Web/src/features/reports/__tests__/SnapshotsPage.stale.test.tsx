import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SnapshotsPage } from "@/pages/admin/SnapshotsPage";
import { testTheme } from "@/test/render";

/**
 * Позначка «застарілий» у переліку зрізів (ФВ-10.5).
 *
 * Сервер віддає `isStale`: після побудови зрізу перераховано його проєкт і
 * період. Зріз не змінюється, тож читач мусить побачити, що числа старі.
 *
 * ⚠ Імпортується лише сторінка: тест доводить, що позначку малює САМЕ перелік.
 * Мутація: прибрати `<SnapshotStaleBadge …/>` зі сторінки — червоніє перший тест;
 * малювати позначку завжди — червоніє другий.
 */
const project = { id: 42, code: "KASH_2026", status: "Active" as const };

function snapshot(
  id: number,
  isStale: boolean | undefined,
): Record<string, unknown> {
  return {
    id,
    builtAt: "2026-01-15T10:00:00Z",
    contentHash: `hash${String(id)}`,
    isCurrent: false,
    periodKey: 202601,
    projectId: 42,
    reportVersionId: 1,
    rowCount: 10,
    status: "Submitted",
    hashFormat: "current",
    ...(isStale === undefined ? {} : { isStale }),
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

function mockFetch(list: Record<string, unknown>[]): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes("/api/v1/me")) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: "en",
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: "tester",
        });
      }

      if (url.includes("/api/v1/reports/snapshots")) return json(list);
      if (url.includes("/api/v1/reports")) return json([]);

      if (url.includes("/api/v1/projects")) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

const SlowEnvTimeout = 400_000;

async function show(list: Record<string, unknown>[]): Promise<void> {
  mockFetch(list);

  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={["/admin/snapshots"]}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );

  // Таблиця домальована: остання контрольна сума на екрані.
  await screen.findByText(
    `hash${String(list.length)}`,
    {},
    { timeout: SlowEnvTimeout },
  );
}

/** Рядок таблиці зрізу з цією контрольною сумою. */
function rowOf(id: number): HTMLElement {
  const row = screen.getByText(`hash${String(id)}`).closest("tr");

  if (row === null) throw new Error(`рядка зрізу ${String(id)} немає`);

  return row;
}

function staleBadgeIn(row: HTMLElement): HTMLElement | null {
  return row.querySelector<HTMLElement>("[data-snapshot-stale]");
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("SnapshotsPage: позначка застарілого зрізу", () => {
  it(
    "застарілий — позначка з підписом і підказкою з каталогу біля статусу",
    async () => {
      await show([snapshot(1, true)]);

      const badge = staleBadgeIn(rowOf(1));

      expect(badge, "у рядку застарілого зрізу немає позначки").not.toBeNull();
      // Каталог не завантажено: `t()` віддає позначений ключ (`D-138`).
      expect(badge?.textContent).toBe("⟦snapshots.stale⟧");
      expect(badge?.hasAttribute("title")).toBe(false);
      expect(
        screen.queryAllByRole("generic", {
          description: "⟦snapshots.staleHint⟧",
        }),
      ).toContain(badge);
      expect(
        badge?.closest("td")?.querySelector('[data-status-kind="snapshot"]'),
      ).not.toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    "свіжий і без поля — позначки немає; кожен рядок свій",
    async () => {
      await show([
        snapshot(1, false),
        snapshot(2, undefined),
        snapshot(3, true),
      ]);

      expect(staleBadgeIn(rowOf(1))).toBeNull();
      expect(staleBadgeIn(rowOf(2))).toBeNull();
      expect(staleBadgeIn(rowOf(3))).not.toBeNull();
      expect(document.querySelectorAll("[data-snapshot-stale]")).toHaveLength(
        1,
      );
    },
    SlowEnvTimeout,
  );
});
