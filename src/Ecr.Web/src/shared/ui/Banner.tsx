import type { JSX, ReactNode } from 'react';
import { Alert, Button, Group } from '@mantine/core';

/**
 * Смуга з підсумком дії або з попередженням (`KIT.md` §6.4 `Banner`/`ResultBanner`,
 * директива №15 §2, шар 2).
 *
 * ⛔ Головне тут — НЕ колір, а РОЛЬ. `role="alert"` читалка озвучує негайно,
 * перебиваючи те, що вона читала; `role="status"` — ввічливо, у паузі. Смуга
 * «Збережено. Далі: надіслати на погодження» з роллю тривоги обриває людину
 * посеред речення заради новини, яка може почекати, — а коли так поводиться
 * КОЖНА смуга, справжню тривогу вже не чути. Тому відповідність тон → роль
 * задана таблицею в одному місці (`ToneRole` нижче), а не розсипана по
 * викликах.
 */
export type BannerTone = 'info' | 'warning' | 'danger' | 'success';

export interface BannerAction {
  readonly label: string;
  readonly onClick: () => void;

  /**
   * ⚠ За замовчуванням `default`, а не `filled`: `L1` (§0 директиви) дозволяє
   * рівно одну `primary`-кнопку на екран, і смуга не має права її забирати —
   * вона повідомляє про те, що вже сталося.
   */
  readonly variant?: 'default' | 'subtle' | 'filled' | undefined;
}

export interface BannerDismiss {
  /**
   * ⛔ Підпис ОБОВ'ЯЗКОВИЙ, і саме тому «закривання» — об'єкт, а не пара
   * незалежних пропсів. Mantine малює кнопку закриття самим значком; без
   * `aria-label` читалка оголошує її просто «кнопка», і єдиний спосіб прибрати
   * смугу стає невидимим для того, хто не бачить значка. Пара
   * `dismissable + dismissLabel` дозволяла б забути другий проп і не помітити.
   */
  readonly label: string;
  readonly onDismiss: () => void;
}

export interface BannerProps {
  readonly tone?: BannerTone | undefined;
  readonly title?: string | undefined;
  readonly text?: ReactNode | undefined;
  readonly icon?: ReactNode | undefined;
  readonly actions?: readonly BannerAction[] | undefined;
  readonly dismiss?: BannerDismiss | undefined;
  readonly testId?: string | undefined;
}

/**
 * Тон → колір теми.
 *
 * ⛔ Жодного літерала кольору: `statusError`/`statusWarning`/`statusSuccess` —
 * кортежі з `shared/theme/theme.ts`, перевірені `contrast.test.ts`. Голі
 * `red`/`orange` заборонені лінтером (`W4.2`) саме тому, що ніхто не міряв
 * їхній контраст у темній темі.
 */
const ToneColor: Record<BannerTone, string> = {
  info: 'brand',
  warning: 'statusWarning',
  danger: 'statusError',
  success: 'statusSuccess',
};

/**
 * Тон → роль живої області.
 *
 * ⚠ Директива називає дослівно два випадки: `status` для `success`, `alert`
 * для `danger`. Решту вирішено тут і це СУДЖЕННЯ, а не цитата: `warning` —
 * проблема, про яку треба знати зараз (перебиваємо), `info` («чекає дії») —
 * новина, яка почекає до паузи.
 */
const ToneRole: Record<BannerTone, 'status' | 'alert'> = {
  info: 'status',
  warning: 'alert',
  danger: 'alert',
  success: 'status',
};

/**
 * ⛔ `renderRoot`, а не `role` пропом. Mantine `Alert` ставить `role="alert"`
 * ПІСЛЯ розгортання чужих пропсів:
 *
 *     jsx(Box, { ...getStyles('root'), variant, ref, ...others, role: 'alert', … })
 *
 * тобто `<Alert role="status">` мовчки нічого не робить — атрибут лишається
 * `alert`. Це не теорія: репозиторій уже наступав на це
 * (`features/security/UserAccessEditor.tsx:260` — «⛔ НЕ `<Alert>`: Mantine
 * ставить йому `role="alert"` за умовчанням»), і там довелося відмовитись від
 * `Alert` зовсім. `renderRoot` — штатний важіль `Box`: Mantine віддає нам
 * зібрані пропси кореня, і наш `role` стоїть ПІСЛЯ них, тож виграє він.
 *
 * ⚠ Тип `Alert` про `renderRoot` не знає (його оголошує `BoxProps`
 * поліморфного `Box`, а не фабрика `Alert`), тому компонент звужено
 * псевдонімом — без `any`, який заборонений лінтером у продуктивному коді.
 */
type AlertRootProps = Parameters<typeof Alert>[0] & {
  readonly renderRoot?: (props: Record<string, unknown>) => ReactNode;
};

const AlertWithRoot = Alert as unknown as (props: AlertRootProps) => JSX.Element;

export function Banner({
  tone = 'info',
  title,
  text,
  icon,
  actions,
  dismiss,
  testId,
}: BannerProps): JSX.Element | null {
  const hasText = text !== undefined && text !== null && text !== '' && text !== false;
  const hasActions = actions !== undefined && actions.length > 0;

  /*
   * ⛔ `D15-06`: елемент без даних не малюється. Смуга без заголовка, тексту й
   * дій — це порожня кольорова стрічка, яка займає місце вгорі екрана й
   * повідомляє рівно нічого; для читалки вона ще й жива область, що
   * «оголосила» порожнечу.
   */
  if (title === undefined && !hasText && !hasActions) return null;

  const body =
    hasText || hasActions ? (
      <>
        {hasText ? text : null}
        {hasActions ? (
          <Group gap="xs" mt="xs">
            {actions.map((action) => (
              <Button
                key={action.label}
                size="xs"
                variant={action.variant ?? 'default'}
                onClick={action.onClick}
              >
                {action.label}
              </Button>
            ))}
          </Group>
        ) : null}
      </>
    ) : null;

  return (
    <AlertWithRoot
      variant="light"
      color={ToneColor[tone]}
      title={title}
      icon={icon}
      withCloseButton={dismiss !== undefined}
      /*
       * ⚠ Розгортання, а не `closeButtonLabel={dismiss?.label}`:
       * `exactOptionalPropertyTypes` (tsconfig) відрізняє «пропа немає» від
       * «проп дорівнює undefined», і Mantine оголошує ці пропси без
       * `| undefined`.
       */
      {...(dismiss === undefined
        ? {}
        : { closeButtonLabel: dismiss.label, onClose: dismiss.onDismiss })}
      data-tone={tone}
      data-testid={testId}
      renderRoot={(rootProps: Record<string, unknown>) => (
        <div {...rootProps} role={ToneRole[tone]} />
      )}
    >
      {body}
    </AlertWithRoot>
  );
}

/**
 * Підсумок щойно виконаної дії (`KIT.md` §6.4, правило `L7`).
 *
 * ⚠ Це той самий `Banner` із тоном `success` — окреме ім'я існує, щоб на
 * екрані було видно НАМІР («що сталося і що далі»), а не лише колір. Тон
 * зафіксований: `ResultBanner` із тоном `danger` був би просто помилкою
 * читання.
 */
export function ResultBanner(props: Omit<BannerProps, 'tone'>): JSX.Element | null {
  return <Banner {...props} tone="success" />;
}
