import { useRef, useState, type JSX } from "react";
import { Button, Popover } from "@mantine/core";
import { t } from "@/shared/i18n";
import "./cellColorMenu.css";
import { colourNameId } from "@/shared/theme/cellPalette";
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
              <Swatch key={hex} hex={hex} name={colourName(hex)} onChoose={choose} />
            ))}
          </div>
          {ThemeShades.map((row, index) => (
            <div
              key={index}
              className="ecr-cell-color-grid ecr-cell-color-shades"
            >
              {row.map((hex, column) => (
                <Swatch
                  key={hex}
                  hex={hex}
                  name={shadeName(ThemeColours[column], index)}
                  onChoose={choose}
                />
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
              <Swatch key={hex} hex={hex} name={colourName(hex)} onChoose={choose} />
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

/**
 * CL-23 (B-9): людська назва кольору для читалки. Літерали, а не `t(`…${hex}`)` (сторож
 * `EndpointCoverageTests`). Невідомий hex (палітра розширилась) → `null`: зразок лишається
 * з одним `aria-label` = hex, а не з вигаданою назвою.
 */
function colourName(hex: string): string | null {
  switch (colourNameId(hex)) {
    case "white": return t("grid.cellColor.name.white");
    case "black": return t("grid.cellColor.name.black");
    case "lightGray": return t("grid.cellColor.name.lightGray");
    case "blueGray": return t("grid.cellColor.name.blueGray");
    case "blue": return t("grid.cellColor.name.blue");
    case "orangeHue": return t("grid.cellColor.name.orangeHue");
    case "gray": return t("grid.cellColor.name.gray");
    case "amber": return t("grid.cellColor.name.amber");
    case "skyBlue": return t("grid.cellColor.name.skyBlue");
    case "mediumGreen": return t("grid.cellColor.name.mediumGreen");
    case "darkRed": return t("grid.cellColor.name.darkRed");
    case "redHue": return t("grid.cellColor.name.redHue");
    case "yellow": return t("grid.cellColor.name.yellow");
    case "lightGreen": return t("grid.cellColor.name.lightGreen");
    case "green": return t("grid.cellColor.name.green");
    case "cyan": return t("grid.cellColor.name.cyan");
    case "strongBlue": return t("grid.cellColor.name.strongBlue");
    case "darkBlue": return t("grid.cellColor.name.darkBlue");
    case "purple": return t("grid.cellColor.name.purple");
    default: return null;
  }
}
/** Відтінок теми: «база, світліший на 80 %» — порядок рядків той самий, що в `ThemeShades`. */
function shadeName(base: string | undefined, row: number): string | null {
  const name = base === undefined ? null : colourName(base);
  if (name === null) return null;
  const shades = [
    t("grid.cellColor.shade.lighter80"),
    t("grid.cellColor.shade.lighter60"),
    t("grid.cellColor.shade.lighter40"),
    t("grid.cellColor.shade.darker25"),
    t("grid.cellColor.shade.darker50"),
  ];
  const shade = shades[row];
  return shade === undefined ? name : t("grid.cellColor.shadeName", { name, shade });
}

function Swatch({
  hex,
  name,
  onChoose,
}: {
  hex: string;
  /** Людська назва для читалки; `aria-label` лишається hex (e2e і тести шукають зразок за ним). */
  name: string | null;
  onChoose: (hex: string) => void;
}): JSX.Element {
  return (
    <button
      type="button"
      className="ecr-cell-color-swatch"
      // ⚠ Колір зразка — дані вибору, не тема: ставиться напряму, а не `style`-пропом (ФВ-14.11).
      ref={(node) => node?.style.setProperty("background-color", hex)}
      aria-label={hex}
      // CL-23: ім'я (accessible name) = hex, як і було; назва кольору - опис, тож
      // `getByRole('button', { name: '#c00000' })` у e2e не ламається.
      {...(name === null ? {} : { "aria-description": name })}
      title={name === null ? hex : `${name} ${hex}`}
      data-hex={hex}
      onClick={() => onChoose(hex)}
    />
  );
}
