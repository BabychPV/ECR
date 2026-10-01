import type { JSX } from "react";
import { Badge } from "@mantine/core";
import { Hint } from "@/shared/ui/Hint";
import { toneFills } from "@/shared/ui/StatusBadge";
import { t } from "@/shared/i18n";

/**
 * Позначка «застарілий» біля статусу зрізу (ФВ-10.5).
 *
 * Сервер віддає `ReportSnapshotSummary.isStale`: після побудови зрізу
 * актуальним став прогін розрахунку того самого проєкту й періоду, тож числа
 * в системі вже можуть відрізнятися від чисел зрізу. Сам зріз не змінюється —
 * поданий лишається таким, яким його подали, — тому це позначка, а не статус.
 *
 * ⚠ `warning`, а не `danger`: дефекту немає, є дія — побудувати новий зріз,
 * якщо потрібні поточні числа. Свіжий зріз не малюється зовсім: позначка
 * «актуальний» на кожному рядку — шум (той самий принцип, що й у
 * `SnapshotFormatBadge`).
 *
 * ⚠ Підказка — `Hint focusable`, а не `title`: так її чують екранний читач і
 * клавіатура.
 */
export function SnapshotStaleBadge(props: {
  stale: boolean | undefined;
}): JSX.Element | null {
  if (props.stale !== true) return null;

  const fill = toneFills.warning;

  return (
    <Hint label={t("snapshots.staleHint")} focusable>
      <Badge
        size="sm"
        miw="fit-content"
        variant="default"
        bg={fill.bg}
        c={fill.text}
        data-snapshot-stale="true"
      >
        {t("snapshots.stale")}
      </Badge>
    </Hint>
  );
}
