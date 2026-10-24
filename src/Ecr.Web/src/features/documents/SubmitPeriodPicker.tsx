import type { JSX } from "react";
import { Button, Menu, Text } from "@mantine/core";
import {
  periodLabel,
  type SheetActionsModel,
} from "@/features/workflow/SheetActions";
import { t } from "@/shared/i18n";

/**
 * Поруч із «Подати: <період>» (D-441): ▾ вибір іншого неподаного відкритого періоду і попередження
 * «наступний — …». Обидва потрібні лише коли неподаних періодів кілька.
 *
 * ⛔ Не `lazy()`: імпортується статично в `DocumentActionBar` (там і чому — пізній зсув рядка дій губив клік «More»).
 */
export function SubmitPeriodPicker({
  submit,
}: {
  readonly submit: NonNullable<SheetActionsModel["submit"]>;
}): JSX.Element {
  return (
    <>
      {/* ✎ D-441: поки ціль — підставна відповідь попереднього аркуша/періоду (`submit.checking`), ▾ на місці
          (ширина рядка та сама), але меню не відкривається: вибирати серед чужих періодів не можна. */}
      <Menu shadow="md" position="bottom-end" withinPortal disabled={submit.checking}>
        <Menu.Target>
          <Button
            variant="default"
            aria-label={t("workflow.pickSubmitPeriod")}
            data-testid="submit-period-pick"
            data-disabled={submit.checking}
            aria-disabled={submit.checking || undefined}
          >
            <span aria-hidden="true">▾</span>
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{t("workflow.pickSubmitPeriod")}</Menu.Label>
          {submit.options.map((key) => (
            <Menu.Item
              key={key}
              data-submit-period={key}
              onClick={() => submit.pick(key)}
              aria-current={key === submit.periodKey ? "true" : undefined}
              rightSection={
                key === submit.periodKey ? (
                  <span aria-hidden="true">✓</span>
                ) : null
              }
            >
              {periodLabel(key)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
      {submit.nextPeriodKey !== null && (
        <Text
          size="xs"
          c="dimmed"
          role="status"
          data-testid="submit-next-period"
        >
          {t("workflow.submitNextPeriod", {
            period: periodLabel(submit.nextPeriodKey),
          })}
        </Text>
      )}
    </>
  );
}
