import { useRef, useState, type JSX } from 'react';
import { Button, Popover } from '@mantine/core';
import { t } from '@/shared/i18n';
import './cellColorMenu.css';
import { isHexColour, StandardColours, ThemeColours, ThemeShades } from './userCellStyle';

/**
 * «Заливка» і «Колір тексту» у тулбарі сітки (L4) — палітра як у Excel: рядок
 * теми з відтінками, стандартні кольори, «Інші кольори» і скидання («Без
 * заливки» / «Автоматичний»).
 *
 * Меню лише ВИБИРАЄ колір; кому його ставити і чи є право — вирішує сітка
 * (`disabled`/`disabledHint`). Кожен зразок — кнопка з `aria-label` = hex, тобто
 * колір доступний і текстом, не лише плямою.
 */
export type ColourTarget = 'fill' | 'text';

interface Props {
  readonly onApply: (target: ColourTarget, hex: string | null) => void;
  readonly disabled: boolean;
  readonly disabledHint: string | null;
  /** Рядок стану недоступності для людини, що читає з екрана. */
  readonly pending?: boolean;
}

export function CellColorMenu({ onApply, disabled, disabledHint, pending = false }: Props): JSX.Element {
  return (
    <div className="ecr-cell-color-menu" role="group" aria-label={t('grid.cellColor.group')} data-testid="cell-color-menu">
      <ColourButton target="fill" onApply={onApply} disabled={disabled} hint={disabledHint} pending={pending} />
      <ColourButton target="text" onApply={onApply} disabled={disabled} hint={disabledHint} pending={pending} />
    </div>
  );
}

function ColourButton({
  target,
  onApply,
  disabled,
  hint,
  pending,
}: {
  target: ColourTarget;
  onApply: Props['onApply'];
  disabled: boolean;
  hint: string | null;
  pending: boolean;
}): JSX.Element {
  const [opened, setOpened] = useState(false);
  const custom = useRef<HTMLInputElement | null>(null);
  const label = t(target === 'fill' ? 'grid.cellColor.fill' : 'grid.cellColor.text');
  const resetLabel = t(target === 'fill' ? 'grid.cellColor.noFill' : 'grid.cellColor.autoText');

  const choose = (hex: string | null): void => {
    setOpened(false);
    onApply(target, hex);
  };

  return (
    <Popover opened={opened && !disabled} onChange={setOpened} position="bottom-start" withArrow shadow="md">
      <Popover.Target>
        <Button
          variant="default"
          disabled={disabled || pending}
          title={disabled && hint !== null ? hint : undefined}
          aria-haspopup="dialog"
          aria-expanded={opened && !disabled}
          data-testid={`cell-color-${target}`}
          onClick={() => setOpened((value) => !value)}
        >
          {label}
        </Button>
      </Popover.Target>
      <Popover.Dropdown className="ecr-cell-color-dropdown" aria-label={label}>
        <button type="button" className="ecr-cell-color-reset" data-testid={`cell-color-${target}-reset`} onClick={() => choose(null)}>
          {resetLabel}
        </button>

        <div className="ecr-cell-color-grid" role="group" aria-label={t('grid.cellColor.theme')}>
          {ThemeColours.map((hex) => (
            <Swatch key={hex} hex={hex} onChoose={choose} />
          ))}
        </div>
        {ThemeShades.map((row, index) => (
          <div key={index} className="ecr-cell-color-grid ecr-cell-color-shades">
            {row.map((hex) => (
              <Swatch key={hex} hex={hex} onChoose={choose} />
            ))}
          </div>
        ))}

        <div className="ecr-cell-color-caption">{t('grid.cellColor.standard')}</div>
        <div className="ecr-cell-color-grid" role="group" aria-label={t('grid.cellColor.standard')}>
          {StandardColours.map((hex) => (
            <Swatch key={hex} hex={hex} onChoose={choose} />
          ))}
        </div>

        <button type="button" className="ecr-cell-color-reset" onClick={() => custom.current?.click()}>
          {t('grid.cellColor.more')}
        </button>
        <input
          ref={custom}
          type="color"
          className="ecr-cell-color-native"
          tabIndex={-1}
          aria-label={t('grid.cellColor.more')}
          data-testid={`cell-color-${target}-custom`}
          onChange={(event) => {
            const hex = event.target.value.toLowerCase();
            if (isHexColour(hex)) choose(hex);
          }}
        />
      </Popover.Dropdown>
    </Popover>
  );
}

function Swatch({ hex, onChoose }: { hex: string; onChoose: (hex: string) => void }): JSX.Element {
  return (
    <button
      type="button"
      className="ecr-cell-color-swatch"
      // ⚠ Колір зразка — дані вибору, не тема: ставиться напряму, а не `style`-пропом (ФВ-14.11).
      ref={(node) => node?.style.setProperty('background-color', hex)}
      aria-label={hex}
      title={hex}
      data-hex={hex}
      onClick={() => onChoose(hex)}
    />
  );
}
