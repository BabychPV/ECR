import type { JSX } from 'react';
import { Button, Menu } from '@mantine/core';
import { t } from '@/shared/i18n';

/** Пункт меню «More» шапки версії шаблону; `null` — дії немає (бракує права чи стану). */
export interface VersionMoreItem {
  readonly key: string;
  readonly label: string;
  readonly onClick: () => void;
  /** Незворотна чи руйнівна дія — червоним, як і кнопка, якою вона була. */
  readonly danger?: boolean;
}

/**
 * Меню «More» у шапці конструктора версії (b4b; макет `screens-templates.js`,
 * KIT §1.2: одна головна дія + ≤ 2 другорядні, решта — у меню).
 *
 * ⛔ Меню без пунктів не малюється — той самий прийом, що `DocumentToolbar`:
 * людина без жодної рідкісної дії не мусить відкривати порожній список.
 *
 * ⚠ Діалоги дій живуть ПОЗА меню (у сторінці): `Menu.Dropdown` розмонтовує
 * вміст, щойно закривається, — тобто саме тоді, коли діалог мав би відкритися.
 */
export function VersionMoreMenu({ items }: { items: readonly (VersionMoreItem | null)[] }): JSX.Element | null {
  const shown = items.filter((item): item is VersionMoreItem => item !== null);
  if (shown.length === 0) return null;

  return (
    <Menu shadow="md" position="bottom-end" withinPortal>
      <Menu.Target>
        <Button variant="default" rightSection={<span aria-hidden="true">▾</span>} data-testid="version-more">
          {t('document.moreActions')}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        {shown.map((item) => (
          <Menu.Item key={item.key} onClick={item.onClick} {...(item.danger === true ? { color: 'statusError' } : {})}>
            {item.label}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
