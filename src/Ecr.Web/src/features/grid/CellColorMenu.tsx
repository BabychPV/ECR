import { useRef, useState, type JSX } from "react";
import { Button, Popover } from "@mantine/core";
import { t } from "@/shared/i18n";
import "./cellColorMenu.css";
import {
  isHexColour,
  StandardColours,
  StyleScopes,
  ThemeColours,
  ThemeShades,
  type StyleScope,
} from "./userCellStyle";

/**
 * «Заливка» і «Колір тексту» у тулбарі сітки (L4) — палітра як у Excel: рядок
 * теми з відтінками, стандартні кольори, «Інші кольори» і скидання («Без
 * заливки» / «Автоматичний»).
 *
 * Меню лише ВИБИРАЄ колір; кому його ставити і чи є право — вирішує сітка
 * (`disabled`/`disabledHint`). Кожен зразок — кнопка з `aria-label` = hex, тобто
 * колір доступний і текстом, не лише плямою.
 */
export type ColourTarget = "fill" | "text";

interface Props {
  readonly onApply: (target: ColourTarget, hex: string | null) => void;
  readonly disabled: boolean;
  readonly disabledHint: string | null;
  /** Машинна причина недоступності (`readOnly` — аркуш закрито/немає права, `noSelection`). */
  readonly disabledReason?: string;
  /** Рядок стану недоступності для людини, що читає з екрана. */
  readonly pending?: boolean;
  /** Область: виділення / весь рядок / вся колонка / вся таблиця. */
  readonly scope?: StyleScope;
  readonly onScopeChange?: (scope: StyleScope) => void;
}

/** Літерали, а не `t(`…${value}`)`: сторож `EndpointCoverageTests` бачить лише літеральні ключі. */
function scopeLabels(): Record<StyleScope, string> {
  return {
    selection: t("grid.cellColor.scope.selection"),
    row: t("grid.cellColor.scope.row"),
    column: t("grid.cellColor.scope.column"),
    table: t("grid.cellColor.scope.table"),
  };
}

export function CellColorMenu({
  onApply,
  disabled,
  disabledHint,
  disabledReason,
  pending = false,
  scope = "selection",
  onScopeChange,
}: Props): JSX.Element {
  return (
    <div
      className="ecr-cell-color-menu"
      role="group"
      aria-label={t("grid.cellColor.group")}
      data-testid="cell-color-menu"
    >
      {onScopeChange === undefined ? null : (
        <select
          className="ecr-cell-color-scope"
          aria-label={t("grid.cellColor.scopeLabel")}
          data-testid="cell-color-scope"
          value={scope}
          onChange={(event) => onScopeChange(event.target.value as StyleScope)}
        >
          {StyleScopes.map((value) => (
            <option key={value} value={value}>
              {scopeLabels()[value]}
            </option>
          ))}
        </select>
      )}
      <ColourButton
        target="fill"
        onApply={onApply}
        disabled={disabled}
        hint={disabledHint}
        reason={disabledReason}
        pending={pending}
      />
      <ColourButton
        target="text"
        onApply={onApply}
        disabled={disabled}
        hint={disabledHint}
        reason={disabledReason}
        pending={pending}
      />
    </div>
  );
}

function ColourButton({
  target,
  onApply,
  disabled,
  hint,
  reason,
  pending,
}: {
  target: ColourTarget;
  onApply: Props["onApply"];
  disabled: boolean;
  hint: string | null;
  reason: Props["disabledReason"];
  pending: boolean;
}): JSX.Element {
  const [opened, setOpened] = useState(false);
  const custom = useRef<HTMLInputElement | null>(null);
  const label = t(
    target === "fill" ? "grid.cellColor.fill" : "grid.cellColor.text",
  );
  const resetLabel = t(
    target === "fill" ? "grid.cellColor.noFill" : "grid.cellColor.autoText",
  );

  const choose = (hex: string | null): void => {
    setOpened(false);
    onApply(target, hex);
  };

  return (
    <>
      <Popover
        opened={opened && !disabled}
        onChange={setOpened}
        position="bottom-start"
        withArrow
        shadow="md"
      >
        <Popover.Target>
          <Button
            variant="default"
            // ⛔ Не рідний `disabled`: кнопка лишається ДОСЯЖНОЮ з клавіатури, а причина читається через
            // `aria-describedby`; клік у недоступному стані нічого не робить.
            data-disabled={disabled || pending}
            title={disabled && hint !== null ? hint : undefined}
            aria-describedby={
              disabled && hint !== null ? `cell-color-${target}-why` : undefined
            }
            aria-disabled={disabled || pending || undefined}
            data-disabled-reason={disabled ? reason : undefined}
            aria-haspopup="dialog"
            aria-expanded={opened && !disabled}
            data-testid={`cell-color-${target}`}
            onClick={() => {
              if (!disabled && !pending) setOpened((value) => !value);
            }}
          >
            {label}
          </Button>
        </Popover.Target>
        <Popover.Dropdown
          className="ecr-cell-color-dropdown"
          aria-label={label}
        >
          <button
            type="button"
            className="ecr-cell-color-reset"
            data-testid={`cell-color-${target}-reset`}
            onClick={() => choose(null)}
          >
            {resetLabel}
          </button>

          <div
            className="ecr-cell-color-grid"
            role="group"
            aria-label={t("grid.cellColor.theme")}
          >
            {ThemeColours.map((hex) => (
              <Swatch key={hex} hex={hex} onChoose={choose} />
            ))}
          </div>
          {ThemeShades.map((row, index) => (
            <div
              key={index}
              className="ecr-cell-color-grid ecr-cell-color-shades"
            >
              {row.map((hex) => (
                <Swatch key={hex} hex={hex} onChoose={choose} />
              ))}
            </div>
          ))}

          <div className="ecr-cell-color-caption">
            {t("grid.cellColor.standard")}
          </div>
          <div
            className="ecr-cell-color-grid"
            role="group"
            aria-label={t("grid.cellColor.standard")}
          >
            {StandardColours.map((hex) => (
              <Swatch key={hex} hex={hex} onChoose={choose} />
            ))}
          </div>

          <button
            type="button"
            className="ecr-cell-color-reset"
            onClick={() => custom.current?.click()}
          >
            {t("grid.cellColor.more")}
          </button>
          <input
            ref={custom}
            type="color"
            className="ecr-cell-color-native"
            tabIndex={-1}
            aria-label={t("grid.cellColor.more")}
            data-testid={`cell-color-${target}-custom`}
            onChange={(event) => {
              const hex = event.target.value.toLowerCase();
              if (isHexColour(hex)) choose(hex);
            }}
          />
        </Popover.Dropdown>
      </Popover>
      {disabled && hint !== null ? (
        <span id={`cell-color-${target}-why`} className="ecr-cell-color-why">
          {hint}
        </span>
      ) : null}
    </>
  );
}

function Swatch({
  hex,
  onChoose,
}: {
  hex: string;
  onChoose: (hex: string) => void;
}): JSX.Element {
  return (
    <button
      type="button"
      className="ecr-cell-color-swatch"
      // ⚠ Колір зразка — дані вибору, не тема: ставиться напряму, а не `style`-пропом (ФВ-14.11).
      ref={(node) => node?.style.setProperty("background-color", hex)}
      aria-label={hex}
      title={hex}
      data-hex={hex}
      onClick={() => onChoose(hex)}
    />
  );
}
