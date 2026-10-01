import { cloneElement, type JSX, type MouseEvent, type ReactElement } from 'react';
import { Hint } from '@/shared/ui/Hint';

/** Пропи кнопки, які `DisabledReason` підміняє, поки дія недоступна. */
interface BlockableProps {
  readonly 'data-disabled'?: boolean | undefined;
  readonly 'aria-disabled'?: boolean | 'true' | 'false' | undefined;
  readonly onClick?: ((event: MouseEvent<HTMLElement>) => void) | undefined;
  /** Доповнює `Hint` — власний опис кнопки зберігається. */
  readonly 'aria-describedby'?: string | undefined;
}

interface DisabledReasonProps {
  /** Чому дія зараз недоступна (уже перекладено); `null` — доступна, кнопка як є. */
  readonly reason: string | null;
  /** Кнопка (`Button`, `ActionIcon`) — БЕЗ власного `disabled`. */
  readonly children: ReactElement<BlockableProps>;
}

/**
 * Недоступна дія з поясненням «чому».
 *
 * ⛔ `data-disabled` + `aria-disabled`, а не `disabled` — той самий прийом, що
 * й у `SheetActions` («потрібен рівень доступу»): вимкнена кнопка не отримує
 * ні фокуса, ні наведення, і підказку не прочитав би ніхто. Тут кнопка лишається
 * в порядку табуляції, читач чує «недоступно» разом із причиною
 * (`aria-describedby` від `Hint`), а клік і Enter/Space гасяться — обробник
 * дії не викликається.
 */
export function DisabledReason({ reason, children }: DisabledReasonProps): JSX.Element {
  if (reason === null) return children;

  return (
    <Hint label={reason}>
      {cloneElement(children, {
        'data-disabled': true,
        'aria-disabled': 'true',
        onClick: (event: MouseEvent<HTMLElement>) => event.preventDefault(),
      })}
    </Hint>
  );
}
