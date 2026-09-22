import type { JSX } from 'react';
import { Alert, Anchor, Button, Group, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import type { MappedFieldPreview } from '@/api/types';
import {
  acceptSourceUnitChange,
  isMappingUnitChangeNotPending,
  isPendingUnitNotInCatalog,
} from './api';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Банер «джерело змінило одиницю» в рядку перегляду мапінгу (`ФВ-16.9`).
 *
 * ⛔ Замінює звичайний «paused»-рядок ЦІЛКОМ, доки позначка `pendingSourceUnitChange`
 * не null: пауза тут — не рішення людини (як ручна пауза, `BE-27`), а
 * автоматична зупинка збору, що чекає рішення саме тут, а не деінде.
 *
 * ⚠ Дві дії, а не select із макета (`docs/design/hybrid/screens-data.js`,
 * діалог `unit-changed`): людині це рішення поставлене дослівно текстом
 * задачі — дві кнопки, не випадний список. Форма — судження, а не цитата з
 * макета.
 *
 * ⛔ «Ні, це помилка джерела» НЕ викликає API: сервер про цю відповідь нічого
 * не знає й нічого не зберігає (за умовою задачі). Банер ховається лише в
 * ЦЬОМУ сеансі рендеру — `onDismiss` керує станом у батьківському
 * `MappingRows`, і повторне відкриття рядка (перемонтування) покаже банер
 * знову, якщо позначка на сервері досі там.
 *
 * ⚠ `onDismiss` — той самий механізм і для 409 (`mappingUnitChangeNotPending`):
 * позначки вже немає, хтось інший вирішив до цього кліка. Різниця лише в
 * тому, ЩО викликало приховання — клік людини чи відповідь сервера.
 */

/**
 * Маршрут довідника одиниць — ЛІТЕРАЛ, а не `routes.adminUnits.path`.
 *
 * ⛔ `features` не має права імпортувати `app` (`ClientLayerRulesTests`,
 * напрям залежностей — лише вниз: `app → pages → features → shared`).
 * `RegistryUsage.tsx` (сусідня фіча) з тієї самої причини бере адресу з
 * ДАНИХ сервера (`item.route`), а не з реєстру маршрутів; тут дані сервер не
 * віддає, тож єдиний вихід — рядок тут, синхронізований з
 * `app/routes.ts:adminUnits.path` вручну.
 */
const AdminUnitsPath = '/admin/units';

export function UnitChangeAction({
  field,
  allowed,
  onDismiss,
}: {
  readonly field: MappedFieldPreview;

  /** Право `Integration.Manage` — те саме, що ховає кнопки Pause/Resume. */
  readonly allowed: boolean;

  /** Ховає банер У БАТЬКІВСЬКОМУ рядку: клік «Ні» або відповідь 409. */
  readonly onDismiss: () => void;
}): JSX.Element | null {
  const pending = field.pendingSourceUnitChange;
  const queryClient = useQueryClient();

  const accept = useMutation({
    mutationFn: () => acceptSourceUnitChange(field.fieldMapId),
    onSuccess: async () => {
      onDismiss();

      // ⚠ Той самий префікс ключа, що в `PauseResumeAction`: сторінка
      // перегляду запитує його як `['mapping-preview', entityId, fromUtc]`,
      // і `invalidateQueries` зіставляє ЗА ПРЕФІКСОМ.
      await queryClient.invalidateQueries({ queryKey: ['mapping-preview'] });
      showDone(t('mapping.unitChangeAccepted'));
    },
    onError: (error) => {
      // ⛔ Позначки вже немає (хтось інший вирішив) — банер зникає сам, і
      // перелік перечитується, щоб рядок показав актуальний стан.
      if (isMappingUnitChangeNotPending(error)) {
        onDismiss();
        void queryClient.invalidateQueries({ queryKey: ['mapping-preview'] });
      }
    },
  });

  if (pending === null) return null;

  const catalogMissing = isPendingUnitNotInCatalog(accept.error);

  return (
    <Alert
      color="statusWarning"
      title={t('mapping.unitChangeTitle')}
      role="alert"
      data-mapping-state="pending-unit-change"
    >
      <Stack gap="xs">
        <Text size="sm">
          {t('mapping.unitChangeBanner', {
            actualUnitCode: pending.actualUnitCode,
            expectedUnitCode: field.targetUnitCode ?? field.sourceUnitCode ?? '—',
          })}
        </Text>

        <Text size="sm" c="dimmed">
          {t('mapping.unitChangeDetected')} <Timestamp value={pending.detectedAt} precise />
        </Text>

        {allowed && (
          <Group gap="xs">
            <Button
              size="compact-xs"
              variant="filled"
              loading={accept.isPending}
              onClick={() => accept.mutate()}
            >
              {t('mapping.unitChangeAccept', { actualUnitCode: pending.actualUnitCode })}
            </Button>

            <Button size="compact-xs" variant="default" onClick={onDismiss}>
              {t('mapping.unitChangeDecline')}
            </Button>
          </Group>
        )}

        {accept.error !== null && <ErrorAlert error={accept.error} />}

        {/*
          ⛔ Лише для ЦІЄЇ відмови (422, одиниці немає в каталозі): людина
          може завести одиницю в іншій вкладці й повернутись повторити клік
          — кнопки прийняття лишаються показаними вище, посилання лише
          додає шлях до довідника.
        */}
        {catalogMissing && (
          <Text size="sm">
            <Anchor component={Link} to={AdminUnitsPath}>
              {t('mapping.unitChangeGoToUnits')}
            </Anchor>
          </Text>
        )}
      </Stack>
    </Alert>
  );
}
