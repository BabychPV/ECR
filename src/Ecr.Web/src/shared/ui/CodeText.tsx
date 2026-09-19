import type { JSX } from 'react';
import { Button, Code, Group } from '@mantine/core';
import { showApiError, showDone } from './notify';

/**
 * Код моноширинним (`KIT.md` §6.7 `CodeText`, крок `UI-04` директиви №15).
 *
 * ⚠ Рядковий варіант — це `Document.Approve`, `ECR-PRD-4223`, `B12`; блоковий
 * — текст формули або правила, і саме він має кнопку копіювання: формулу
 * переносять у редактор виразів або в звернення до підтримки, і виділяти її
 * мишею в таблиці — це та дрібниця, через яку люди роблять знімок екрана
 * замість того, щоб надіслати текст.
 */

/**
 * Написи кнопки й підтвердження.
 *
 * ⚠ Літерали, а не `t()`, — той самий свідомий компроміс, що вже прийнятий
 * для `notificationCloseButtonProps` у `notify.ts` і для
 * `passwordToggleProps` у `pages/LoginPage.tsx`: рядки застосунку йдуть із
 * серверного каталогу (`09-seed.sql`), ключа під ці два написи там немає, а
 * голий `t()` без рядка показав би користувачеві позначений ключ (`⟦…⟧`)
 * замість підпису кнопки. Заводити ключі в сід заради компонента без екрана
 * заборонено `D15-06`; вони приїдуть із `UI-09` і заміняться пропами нижче
 * без переробки.
 */
const DefaultCopyLabel = 'Copy';
const DefaultCopiedMessage = 'Copied';

export interface CodeTextProps {
  /** Сам код. */
  readonly children: string;

  /** Блоком (`<pre>`) і з кнопкою копіювання — для формул і правил. */
  readonly block?: boolean | undefined;

  /** Підпис кнопки копіювання; він же її доступне ім'я. */
  readonly copyLabel?: string | undefined;

  /** Текст тоста після вдалого копіювання. */
  readonly copiedMessage?: string | undefined;
}

/**
 * Код моноширинним; блоком — із копіюванням.
 *
 * ⛔ Порожній код не малюється (`D15-06`): ні `<code>` без вмісту, ні кнопки
 * «Copy», що кладе в буфер порожній рядок. Порожня рамка коду в шторці
 * читається як «код є, і він порожній», а це інше твердження, ніж «коду
 * немає».
 */
export function CodeText({
  children,
  block = false,
  copyLabel = DefaultCopyLabel,
  copiedMessage = DefaultCopiedMessage,
}: CodeTextProps): JSX.Element | null {
  if (children.trim().length === 0) return null;

  if (!block) {
    return <Code data-code-text="">{children}</Code>;
  }

  /**
   * ⛔ `try`/`catch` тут обов'язковий, і не «про всяк випадок»:
   * `navigator.clipboard` ВІДСУТНІЙ у незахищеному контексті (`http://` не на
   * `localhost`) — тобто саме там, де цей застосунок і живе в корпоративній
   * мережі. Без перехоплення звернення до `undefined.writeText` кидає
   * `TypeError` з обробника події, React 19 піднімає його до межі помилок, і
   * натискання «Copy» гасить ЕКРАН. Відмова буфера обміну не є приводом
   * втратити сторінку з незбереженими правками.
   *
   * ⚠ Причина показується словами сервера/браузера (`showApiError`), а не
   * «не вдалося»: «Document is not focused» і «Write permission denied» — це
   * дві різні дії користувача, і узагальнення викинуло б єдину підказку
   * (`ФВ-14.24`).
   */
  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(children);
      showDone(copiedMessage);
    } catch (error) {
      showApiError(error);
    }
  }

  return (
    <Group gap="xs" align="flex-start" wrap="nowrap" data-code-text-block="">
      <Code block data-code-text="">
        {children}
      </Code>

      {/* ⚠ Без фіксованої ширини (`ФВ-14.30`): казахський і російський
          переклади напису на 20–40 % довші за англійський. */}
      <Button
        size="xs"
        variant="default"
        data-code-text-copy=""
        onClick={() => {
          void copy();
        }}
      >
        {copyLabel}
      </Button>
    </Group>
  );
}
