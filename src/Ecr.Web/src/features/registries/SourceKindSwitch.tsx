import { Suspense, lazy, useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import type { RegistryDefDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';

/*
 * ⚠ Діалог — лінивим чанком (D-132): `Checkbox`/`Select`/`Textarea` Mantine потрібні лише після
 * кліку. Кнопка лишається статичною — рядок дій шапки не зсувається, поки чанк їде. Діалог
 * змонтований завжди (у закритому стані він нічого не малює), тож чанк вантажиться разом зі
 * сторінкою.
 *
 * ⛔ WCAG 2.4.3: відкривача (кнопку чи пункт «Ще») запам'ятовує `useRestoreFocus` тут, а не лише
 * `returnFocus` Mantine: клік раніше, ніж приїхав чанк, монтує діалог уже відкритим, Mantine
 * переходу `opened` false → true не бачить, і після закриття фокус падав на `BODY`.
 */
const SourceKindSwitchDialog = lazy(async () => ({
  default: (await import('./SourceKindSwitchDialog')).SourceKindSwitchDialog,
}));

/**
 * Перемикання master-джерела **набором** довідників (`ФВ-13.10`, `ФВ-11.4`) — кнопка і діалог
 * (`SourceKindSwitchDialog`).
 */
export function SourceKindSwitch({
  registries,
  opened: controlledOpened,
  onClose,
}: {
  registries: RegistryDefDto[];

  /**
   * Керований режим (REG-UI 3/3): `opened` задано — власної кнопки немає, діалог відкриває й
   * закриває сторінка (пункт меню «Ще»). Без нього — кнопка й діалог разом, як було.
   */
  opened?: boolean;
  onClose?: () => void;
}): JSX.Element {
  const controlled = controlledOpened !== undefined;
  const [ownOpened, setOwnOpened] = useState(false);
  const opened = controlled ? controlledOpened : ownOpened;
  useRestoreFocus(opened);

  return (
    <>
      {!controlled && (
        <Button variant="default" onClick={() => setOwnOpened(true)}>
          {t('registries.sourceSwitch')}
        </Button>
      )}

      <Suspense fallback={null}>
        <SourceKindSwitchDialog
          registries={registries}
          opened={opened}
          onClose={() => {
            setOwnOpened(false);
            onClose?.();
          }}
        />
      </Suspense>
    </>
  );
}
