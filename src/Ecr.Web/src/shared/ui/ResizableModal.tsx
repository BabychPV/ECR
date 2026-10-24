import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type JSX,
  type KeyboardEvent,
  type PointerEvent,
} from 'react';
import { CloseButton, Group, Modal, useProps, type ModalProps, type ModalRootProps } from '@mantine/core';
import { hasText, t } from '@/shared/i18n';
import {
  NormalSize,
  dragResize,
  keyResize,
  readSize,
  writeSize,
  type Box,
  type ModalSize,
} from './modalSize';
import { ResizableModalContext } from './resizableModalContext';
import './ResizableModal.css';

/**
 * Діалог, який можна розгорнути на весь екран або потягнути за кут (P44, прохання людини 24.10:
 * «всі вспливаючі вікна, які відносяться до формул та методологій, мають мати можливість змінювати
 * розмір»).
 *
 * Ті самі пропи, що й у `Modal` Mantine, плюс `sizeKey` — вид вікна, під яким запам'ятовується
 * останній розмір (`methodology.formula`, `template.formula`…):
 * - кнопка в заголовку (і подвійний клік по заголовку) — на весь екран ↔ звичайний розмір;
 *   повернення до звичайного стирає запам'ятоване;
 * - ручка в правому нижньому куті — тягнути мишею/пальцем або стрілками (`Shift` — крупніше);
 *   на вузькому екрані ручки немає (CSS), кнопка лишається;
 * - вміст заповнює висоту, довге прокручується всередині тіла, редактор виразу росте з вікном
 *   (`resizableModalContext.ts`, `ExpressionEditor`).
 *
 * ⛔ Складено з частин (`Modal.Root`/`Content`/`Header`…), а не обгорнуто `Modal`: кнопка має
 * стояти ПОРУЧ із хрестиком, а не в заголовку. Усередині `<h2>` її назва потрапила б у доступне
 * ім'я діалогу (`aria-labelledby` → заголовок), і «Formula» стала б «Formula Full screen» — для
 * читалки й для кожного `getByRole('dialog', { name })` у тестах.
 *
 * ⚠ Дефолти теми для `Modal` (`theme.ts`: тривалість переходу, `aria-label` хрестика) частини
 * самі не беруть — `useProps('Modal', …)` підтягує їх так само, як це робить `Modal`.
 *
 * ⚠ Анімації розміру немає навмисно: перемикання миттєве, тож `prefers-reduced-motion` нічого
 * тут не вимикає; перехід відкриття — той самий, що в теми.
 */
export type ResizableModalProps = Omit<ModalProps, 'classNames' | 'stackId' | '__staticSelector'> & {
  /** Вид вікна для запам'ятовування розміру (`localStorage`, `ecr.modalSize.<sizeKey>`). */
  readonly sizeKey: string;
};

const ModalDefaults: Partial<ModalProps> = { withOverlay: true, withCloseButton: true };

/** Назва кнопки «на весь екран» — стала: стан несе `aria-pressed`, а не зміна назви. */
function fullScreenLabel(): string {
  return hasText('common.modal.fullScreen') ? t('common.modal.fullScreen') : 'Full screen';
}

/** Підказка кнопки: що станеться після натиску. */
function toggleHint(full: boolean): string {
  if (full) return hasText('common.modal.restore') ? t('common.modal.restore') : 'Restore size';
  return hasText('common.modal.expand') ? t('common.modal.expand') : 'Expand';
}

function resizeLabel(): string {
  return hasText('common.modal.resize') ? t('common.modal.resize') : 'Resize window';
}

function resizeHint(): string {
  return hasText('common.modal.resizeHint')
    ? t('common.modal.resizeHint')
    : 'Drag the corner or use the arrow keys; Shift makes bigger steps.';
}

function viewport(): Box {
  return { width: globalThis.innerWidth, height: globalThis.innerHeight };
}

/** Чотири стрілки назовні — розгорнути; всередину — згорнути. */
function ToggleGlyph({ full }: { readonly full: boolean }): JSX.Element {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path
        d={
          full
            ? 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5'
            : 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5'
        }
      />
    </svg>
  );
}

export function ResizableModal(props: ResizableModalProps): JSX.Element {
  const { sizeKey, ...rest } = props;
  const {
    title,
    withOverlay,
    overlayProps,
    withCloseButton,
    closeButtonProps,
    children,
    size: defaultSize,
    fullScreen: forcedFullScreen,
    centered,
    opened,
    onClose,
    ...others
  } = useProps('Modal', ModalDefaults, rest);

  // ⚠ `useProps` повертає кожен необов'язковий проп як `T | undefined`, а `exactOptionalPropertyTypes`
  // не пускає `undefined` туди, де проп лише необов'язковий; значення ті самі, що отримав би `Modal`.
  const root = others as Partial<ModalRootProps>;

  const [size, setSize] = useState<ModalSize>(() => readSize(sizeKey));
  const content = useRef<HTMLDivElement | null>(null);
  const drag = useRef<{ readonly x: number; readonly y: number; readonly start: Box } | null>(null);

  // ⚠ Перечитується на КОЖНЕ відкриття: те саме вікно (той самий `sizeKey`) живе на кількох
  // екранах, і розмір, змінений на одному, має діяти й на іншому.
  useEffect(() => {
    if (opened) setSize(readSize(sizeKey));
  }, [opened, sizeKey]);

  const commit = useCallback(
    (next: ModalSize) => {
      setSize(next);
      writeSize(sizeKey, next);
    },
    [sizeKey],
  );

  const full = forcedFullScreen === true || size.mode === 'full';

  const toggle = useCallback(() => {
    commit(size.mode === 'full' ? NormalSize : { mode: 'full' });
  }, [commit, size.mode]);

  /** Поточний розмір вікна на екрані — відправна точка, коли його ще не тягнули. */
  const measured = (): Box => {
    if (size.mode === 'custom') return { width: size.width, height: size.height };
    const rect = content.current?.getBoundingClientRect();
    return { width: rect?.width ?? 0, height: rect?.height ?? 0 };
  };

  const onPointerDown = (event: PointerEvent<HTMLButtonElement>): void => {
    if (event.button !== 0) return;
    event.preventDefault();
    // ⚠ Захоплення — щоб рух поза ручкою (курсор швидший за перерендер) не губився; без
    // підтримки (старий рушій, jsdom) тягнути можна однаково, лише курсором над ручкою.
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // Див. вище.
    }
    drag.current = { x: event.clientX, y: event.clientY, start: measured() };
  };

  const onPointerMove = (event: PointerEvent<HTMLButtonElement>): void => {
    const from = drag.current;
    if (from === null) return;

    const box = dragResize(from.start, event.clientX - from.x, event.clientY - from.y, centered === true, viewport());
    setSize({ mode: 'custom', ...box });
  };

  const onPointerEnd = (event: PointerEvent<HTMLButtonElement>): void => {
    if (drag.current === null) return;
    drag.current = null;
    try {
      event.currentTarget.releasePointerCapture(event.pointerId);
    } catch {
      // Захоплення не було — звільняти нічого.
    }
    // Запис — раз на відпускання, а не на кожен рух миші.
    if (size.mode === 'custom') writeSize(sizeKey, size);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>): void => {
    const box = keyResize(measured(), event.key, event.shiftKey, viewport());
    if (box === null) return;

    // ⚠ Лише стрілки: `Escape`, `Tab` ідуть далі — до закриття вікна й пастки фокуса.
    event.preventDefault();
    commit({ mode: 'custom', ...box });
  };

  const custom = !full && size.mode === 'custom';
  // ⚠ Ширина — прямо на вміст (`flex-basis`), а не пропом `size`: Mantine переводить пікселі в
  // `rem × --mantine-scale`, а тема масштабує шкалу, тож 636px пропом ставали ~574px і кут
  // відставав від курсора. `max-width`/`max-height` Mantine однаково тримають вікно в екрані.
  const contentStyle: CSSProperties | undefined = custom
    ? { flex: `0 0 ${String(size.width)}px`, height: `${String(size.height)}px` }
    : undefined;

  return (
    <ResizableModalContext.Provider value>
      <Modal.Root
        {...root}
        opened={opened}
        onClose={onClose}
        centered={centered === true}
        fullScreen={full}
        {...(defaultSize === undefined ? {} : { size: defaultSize })}
        classNames={{ content: 'ecr-rm-content', body: 'ecr-rm-body', header: 'ecr-rm-header' }}
      >
        {withOverlay === true && <Modal.Overlay {...overlayProps} />}
        <Modal.Content
          ref={content}
          style={contentStyle}
          data-rm-size={full ? 'full' : size.mode}
          data-rm-width={custom ? size.width : undefined}
          data-rm-height={custom ? size.height : undefined}
        >
          <Modal.Header>
            {title !== undefined && title !== null && title !== false && (
              <Modal.Title onDoubleClick={toggle}>{title}</Modal.Title>
            )}
            <Group gap="xs" wrap="nowrap" className="ecr-rm-actions">
              {/*
                ⚠ `CloseButton` з власною іконкою, а не `ActionIcon`: той самий вигляд і розмір, що
                в хрестика поруч, і без окремого чанка `ActionIcon` (+1.2 КБ gzip на маршрут, D-132).
              */}
              <CloseButton
                icon={<ToggleGlyph full={full} />}
                aria-label={fullScreenLabel()}
                aria-pressed={full}
                title={toggleHint(full)}
                onClick={toggle}
                disabled={forcedFullScreen === true}
                data-rm-toggle
              />
              {withCloseButton === true && <Modal.CloseButton {...closeButtonProps} />}
            </Group>
          </Modal.Header>
          <Modal.Body>
            <div className="ecr-rm-fill">{children}</div>
          </Modal.Body>
          {!full && (
            <button
              type="button"
              className="ecr-rm-handle"
              aria-label={resizeLabel()}
              title={resizeHint()}
              aria-keyshortcuts="ArrowUp ArrowDown ArrowLeft ArrowRight"
              onPointerDown={onPointerDown}
              onPointerMove={onPointerMove}
              onPointerUp={onPointerEnd}
              onPointerCancel={onPointerEnd}
              onKeyDown={onKeyDown}
              data-rm-handle
            />
          )}
        </Modal.Content>
      </Modal.Root>
    </ResizableModalContext.Provider>
  );
}
