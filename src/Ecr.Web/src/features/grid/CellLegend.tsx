import { useState, type JSX } from 'react';
import { Popover, UnstyledButton } from '@mantine/core';
import { t } from '@/shared/i18n';
import './gridKeyHint.css';

/** Рядок легенди: класи справжньої комірки (`cell-states.css`), зразок значення і пояснення словами. */
export interface LegendEntry {
  readonly classes: string;
  readonly sample: string;
  readonly what: string;
}

/*
 * ⚠ Кожен підпис — окремим викликом із ЛІТЕРАЛОМ (сторож каталогу розбирає лише так).
 * ⛔ Класи — ТІ САМІ, що ставить сітка (`DocumentGrid.gridColumns`, `cellStateClass`): зразок малює той самий CSS, тож
 * легенда не розійдеться з комірками. Тест `CellLegend.test.tsx` звіряє: кожен клас стану з `cell-states.css` є тут.
 */
export function legendEntries(): LegendEntry[] {
  return [
    { classes: 'ecr-cell ecr-cell--dirty', sample: '12.5', what: t('grid.legend.dirty') },
    { classes: 'ecr-cell ecr-cell--calculated', sample: '40.2', what: t('grid.legend.calculated') },
    { classes: 'ecr-cell ecr-cell-formula-manual', sample: '38', what: t('grid.legend.manual') },
    { classes: 'ecr-cell ecr-cell--calculated ecr-cell-calc-error', sample: '#DIV/0', what: t('grid.legend.calcError') },
    { classes: 'ecr-cell ecr-cell--calculated ecr-cell-calc-stale', sample: '40.2', what: t('grid.legend.calcStale') },
    { classes: 'ecr-cell ecr-cell-needs-input ecr-cell-needs-input--block', sample: '', what: t('grid.legend.requiredBlock') },
    { classes: 'ecr-cell ecr-cell-needs-input ecr-cell-needs-input--warn', sample: '', what: t('grid.legend.requiredWarn') },
    { classes: 'ecr-cell ecr-cell-save-error', sample: 'abc', what: t('grid.legend.saveError') },
    { classes: 'ecr-cell ecr-cell--rounded', sample: '12.35', what: t('grid.legend.rounded') },
    { classes: 'ecr-cell ecr-cell--orphaned', sample: 'KZ-1', what: t('grid.legend.orphaned') },
    { classes: 'ecr-cell ecr-cell--read-only', sample: '7', what: t('grid.legend.readOnly') },
    { classes: 'ecr-cell ecr-cell-out-of-window', sample: '9', what: t('grid.legend.outOfWindow') },
  ];
}

/**
 * ✎ DOCUX (доповнення координатора 26.11, схвалено людиною): легенда позначок комірок — поруч із довідкою клавіш під
 * таблицею. Кожен рядок — зразок, намальований ТИМИ САМИМИ класами, що справжня комірка, і пояснення простими словами.
 */
export function CellLegend(): JSX.Element {
  const [opened, setOpened] = useState(false);
  const entries = legendEntries();

  return (
    <Popover opened={opened} onChange={setOpened} position="top-end" withArrow shadow="md" width={420}>
      <Popover.Target>
        <UnstyledButton
          className="ecr-keyhint-btn"
          aria-expanded={opened}
          aria-haspopup="dialog"
          onClick={() => setOpened((value) => !value)}
          data-grid-legend=""
          aria-label={t('grid.legend.title')}
          title={t('grid.legend.title')}
        >
          <span className="ecr-keyhint-icon" aria-hidden="true">
            ◫
          </span>
          <span className="ecr-keyhint-label">{t('grid.legend.title')}</span>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown aria-label={t('grid.legend.title')}>
        <dl
          className="ecr-legend-list"
          style={{ ['--ecr-required-label' as string]: JSON.stringify(t('grid.needsInput.label')) }}
          data-grid-legend-list=""
        >
          {entries.map((entry) => (
            <div key={entry.classes} className="ecr-legend-row">
              <dt>
                <span className={`ecr-legend-cell ${entry.classes}`} data-legend-sample={entry.classes} aria-hidden="true">
                  {entry.sample}
                </span>
              </dt>
              <dd>{entry.what}</dd>
            </div>
          ))}
        </dl>
      </Popover.Dropdown>
    </Popover>
  );
}
