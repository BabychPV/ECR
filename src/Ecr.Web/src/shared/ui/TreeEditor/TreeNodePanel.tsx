import type { JSX, ReactNode } from 'react';
import { Menu } from '@mantine/core';
import { formatDate } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { KeyValue, type KeyValueItem } from '../KeyValue';
import type { TreeNodeActions } from './TreeEditor';
import type { TreeLevel, TreeNode } from './treeModel';

export interface TreeNodePanelProps {
  readonly node: TreeNode | null;
  readonly path: readonly TreeNode[];
  readonly levels: readonly TreeLevel[];
  readonly actions: TreeNodeActions;
  /** Початок дії вузла: перший день, коли він дійсний. */
  readonly validFrom?: string | null | undefined;
  /** Перший день, коли вузол уже НЕ дійсний. */
  readonly validTo?: string | null | undefined;
  /** Додаткові пари сторінки («де використано», атрибути). */
  readonly extra?: readonly KeyValueItem[] | undefined;
  readonly children?: ReactNode;
}

/**
 * Панель вибраного вузла (макет: «NAE» + назва, шлях, кнопки). Показує лише те, що знає дерево: код, рівень, шлях,
 * дійсність, кількість дітей. Атрибути конкретного довідника сторінка додає через `extra` і `children`.
 *
 * ⚠ Кнопка, якої немає права чи сенсу (виведений вузол, нема колбека), не малюється - а не стоїть сірою без пояснення.
 */
export function TreeNodePanel({ node, path, levels, actions, validFrom, validTo, extra, children }: TreeNodePanelProps): JSX.Element {
  if (node === null) {
    return (
      <div className="ecr-tree-panel" data-testid="tree-panel-empty">
        <p className="ecr-tree-plain">{t('treeEditor.panel.empty')}</p>
      </div>
    );
  }

  const level = levels.find((item) => item.code === node.levelCode);
  const validity =
    validFrom !== undefined && validFrom !== null
      ? validTo !== undefined && validTo !== null
        ? t('treeEditor.panel.fromTo', { from: formatDate(validFrom), to: formatDate(validTo) })
        : t('treeEditor.panel.from', { from: formatDate(validFrom) })
      : validTo !== undefined && validTo !== null
        ? t('treeEditor.panel.until', { to: formatDate(validTo) })
        : undefined;

  const items: KeyValueItem[] = [
    { label: t('treeEditor.panel.code'), value: node.code, mono: true },
    { label: t('treeEditor.panel.level'), value: level?.name },
    { label: t('treeEditor.panel.path'), value: path.map((item) => item.label).join(' › ') },
    { label: t('treeEditor.panel.validity'), value: validity },
    { label: t('treeEditor.panel.children'), value: node.childCount },
    ...(extra ?? []),
  ];

  return (
    <div className="ecr-tree-panel" data-testid="tree-panel">
      <div className="ecr-tree-panel-head">
        {level !== undefined && <span className="ecr-tree-kind">{level.name}</span>}
        <h2>{node.label}</h2>
        {node.retired === true && <span className="ecr-tree-retired">{t('treeEditor.retiredTag')}</span>}
      </div>

      {node.retired === true && <p className="ecr-tree-plain">{t('treeEditor.panel.retiredNote')}</p>}

      <KeyValue items={items} wide />

      <div className="ecr-tree-panel-actions">
        {actions.canMove && (
          <button type="button" className="ecr-tree-btn" onClick={actions.move} data-testid="tree-panel-move">
            {t('treeEditor.move.action')}
          </button>
        )}
        {actions.canRetire && (
          <button type="button" className="ecr-tree-btn" onClick={actions.retire} data-testid="tree-panel-retire">
            {t('treeEditor.retire.action')}
          </button>
        )}
        {actions.childLevels.length === 1 && actions.childLevels[0] !== undefined && (
          <button
            type="button"
            className="ecr-tree-btn"
            onClick={() => {
              const only = actions.childLevels[0];
              if (only !== undefined) actions.addChild(only);
            }}
            data-testid="tree-panel-add"
          >
            {t('treeEditor.addChild')}
          </button>
        )}
        {actions.childLevels.length > 1 && (
          <Menu withinPortal>
            <Menu.Target>
              <button type="button" className="ecr-tree-btn" data-testid="tree-panel-add">
                {t('treeEditor.addChild')}
              </button>
            </Menu.Target>
            <Menu.Dropdown>
              {actions.childLevels.map((item) => (
                <Menu.Item key={item.code} onClick={() => actions.addChild(item)}>
                  {item.name}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        )}
      </div>

      {children}
    </div>
  );
}
