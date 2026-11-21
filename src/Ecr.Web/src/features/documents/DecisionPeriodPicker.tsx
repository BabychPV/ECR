import type { JSX } from "react";
import { Button, Menu } from "@mantine/core";
import { periodLabel, type DecisionPeriod } from "@/features/workflow/SheetActions";
import { t } from "@/shared/i18n";

/**
 * ✎ D-6: ▾ поруч із «Approve: <місяць>» / «Return for edits: <місяць>» — вибір іншого місяця року, що чекає цієї дії
 * (той самий вигляд, що в `SubmitPeriodPicker`). Малюється лише коли місяців кілька.
 *
 * ⛔ Не `lazy()`: імпортується статично в `DocumentActionBar` з тієї ж причини, що й `SubmitPeriodPicker` (пізній зсув рядка дій).
 */
export function DecisionPeriodPicker({
  decision,
  action,
}: {
  readonly decision: DecisionPeriod;
  readonly action: "approve" | "reopen";
}): JSX.Element | null {
  if (decision.periodKey === null || decision.options.length < 2) return null;

  // Літерали умовою, а не ключ зі змінної: сторож Architecture не приймає складений ключ.
  const label = action === "approve" ? t("workflow.pickApprovePeriod") : t("workflow.pickReopenPeriod");

  return (
    <Menu shadow="md" position="bottom-end" withinPortal>
      <Menu.Target>
        <Button variant="default" aria-label={label} data-testid={`${action}-period-pick`}>
          <span aria-hidden="true">▾</span>
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{label}</Menu.Label>
        {decision.options.map((key) => (
          <Menu.Item
            key={key}
            data-decision-period={key}
            onClick={() => decision.pick(key)}
            aria-current={key === decision.periodKey ? "true" : undefined}
            rightSection={key === decision.periodKey ? <span aria-hidden="true">✓</span> : null}
          >
            {periodLabel(key)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
