import type { JSX, ReactNode } from 'react';

/**
 * Іконки пунктів навбару (картка «іконки навігації», продовження
 * `routes.ts`/`PR nav-arch #1`, `Q-276`).
 *
 * ⚠ `RouteHandle.icon` (`routes.ts`) — рядковий КЛЮЧ (не компонент): реєстр
 * маршрутів навмисно лишався незалежним від форми рендера іконки (`Q-276`:
 * «поле лишається типізованим, щоб PR, який додасть іконки, не чіпав форму
 * запису — лише саму бібліотеку рендера»). Ця картка додає саме бібліотеку
 * рендера — `navIcons` (ключ → компонент) і `NavIcon` (ключ → елемент) — не
 * чіпаючи форму `RouteHandle`.
 *
 * ⛔ Без нової залежності (`@tabler/icons-react`, `lucide-react` тощо):
 * `package.json`/`package-lock.json` — орендар-виключні файли цієї картки,
 * і нова npm-залежність заради 16 маленьких іконок коштувала б непропорційно
 * дорого проти рукописного inline SVG. Кожна іконка нижче — 2–4 прості
 * `<path>`, той самий візуальний вагомість (лінійний стиль, одна товщина
 * штриха, заокруглені кінці/зʼєднання, viewBox 24×24), щоб набір читався як
 * ОДНА система, а не як зібрані з різних джерел значки.
 *
 * ⚠ `stroke="currentColor"` (не власний колір) — іконка успадковує колір
 * тексту пункту навбару (`NavLink` сам керує кольором активного/неактивного
 * стану через Mantine-токени), тож паритет світлої/темної теми не вимагає
 * окремої роботи цієї картки: колір іконки завжди той самий, що й колір
 * лейбла поруч.
 *
 * ⛔ Кожна іконка — `aria-hidden="true"`: вона стоїть ПОРУЧ із текстовим
 * лейблом (`NavLink label`), який уже й є доступним ім'ям пункту. Без
 * `aria-hidden` читалка озвучувала б порожній/дублюючий вміст `<svg>` перед
 * кожним лейблом навбару.
 */

const StrokeWidth = 1.75;

/** Спільна обгортка розміру/атрибутів доступності для кожної іконки нижче. */
function Icon({ children }: { children: ReactNode }): JSX.Element {
  return (
    <svg
      width={20}
      height={20}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={StrokeWidth}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {children}
    </svg>
  );
}

/** Documents — аркуш із загнутим кутом і рядками тексту. */
function DocumentsIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M6 3h7l5 5v13a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z" />
      <path d="M13 3v5h5" />
      <path d="M8 12.5h8M8 16h5" />
    </Icon>
  );
}

/** Templates — макет/сітка (аркуш, поділений на секції). */
function TemplatesIcon(): JSX.Element {
  return (
    <Icon>
      <rect x="3.5" y="3.5" width="17" height="17" rx="1.5" />
      <path d="M3.5 9.5h17" />
      <path d="M9.5 9.5v11" />
    </Icon>
  );
}

/** Registries — база даних (стос циліндрів). */
function RegistriesIcon(): JSX.Element {
  return (
    <Icon>
      <ellipse cx="12" cy="5.5" rx="7" ry="2.75" />
      <path d="M5 5.5v6c0 1.52 3.13 2.75 7 2.75s7-1.23 7-2.75v-6" />
      <path d="M5 11.5v6c0 1.52 3.13 2.75 7 2.75s7-1.23 7-2.75v-6" />
    </Icon>
  );
}

/** Methodologies — формула (сигма). */
function MethodologiesIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M17 5H7l5 7-5 7h10" />
    </Icon>
  );
}

/** Expressions — код-вираз (кутові дужки). */
function ExpressionsIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M9.5 7.5 4.5 12l5 4.5" />
      <path d="M14.5 7.5l5 4.5-5 4.5" />
    </Icon>
  );
}

/** Units — вимірювальна лінійка з поділками. */
function UnitsIcon(): JSX.Element {
  return (
    <Icon>
      <rect x="3" y="8" width="18" height="8" rx="1" />
      <path d="M7 8v3M11 8v3M15 8v3M19 8v3" />
    </Icon>
  );
}

/** Security — щит із позначкою. */
function SecurityIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M12 3.5 19 6v6c0 5-3.2 7.7-7 8.5-3.8-.8-7-3.5-7-8.5V6l7-2.5Z" />
      <path d="M9 12l2 2 4-4.5" />
    </Icon>
  );
}

/** Periods — календар. */
function PeriodsIcon(): JSX.Element {
  return (
    <Icon>
      <rect x="3.5" y="5" width="17" height="15.5" rx="2" />
      <path d="M3.5 10h17" />
      <path d="M8 3v4M16 3v4" />
    </Icon>
  );
}

/** Sources — вилка живлення/підключення. */
function SourcesIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M7 6.5h10v4a5 5 0 0 1-10 0v-4Z" />
      <path d="M9.5 3v3.5M14.5 3v3.5" />
      <path d="M12 15.5V21" />
    </Icon>
  );
}

/** Mapping preview — зіставлення (протилежні стрілки). */
function MappingIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M6 8.5h13M15.5 5l3.5 3.5-3.5 3.5" />
      <path d="M18 15.5H5M8.5 12l-3.5 3.5 3.5 3.5" />
    </Icon>
  );
}

/** Jobs — шестерня (фонові процеси). */
function JobsIcon(): JSX.Element {
  return (
    <Icon>
      <circle cx="12" cy="12" r="3" />
      <path d="M12 2.5v3M12 18.5v3M4.4 4.4l2.1 2.1M17.5 17.5l2.1 2.1M2.5 12h3M18.5 12h3M4.4 19.6l2.1-2.1M17.5 6.5l2.1-2.1" />
    </Icon>
  );
}

/** Report snapshots — фотоапарат (знімок стану). */
function SnapshotsIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M4 8.5h3l1.8-2h6.4l1.8 2h3v10.5H4V8.5Z" />
      <circle cx="12" cy="13.5" r="3.25" />
    </Icon>
  );
}

/** Audit trail — годинник зі стрілкою повернення (історія). */
function AuditIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M4.5 9A8 8 0 1 1 6 15.5" />
      <path d="M4.5 4.5V9h4.5" />
      <path d="M12 8v4.3l3 1.8" />
    </Icon>
  );
}

/** Interface texts — репліка з рядками тексту (каталог рядків). */
function UiStringsIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M4 5h16v10H11l-3.5 3.5V15H4V5Z" />
      <path d="M7 8.5h10M7 11.5h6" />
    </Icon>
  );
}

/** Health — пульс (кардіограма). */
function HealthIcon(): JSX.Element {
  return (
    <Icon>
      <path d="M3 12.5h4l2-5.5 4 11 2-5.5h6" />
    </Icon>
  );
}

/** My groups — люди (двоє). */
function MyGroupsIcon(): JSX.Element {
  return (
    <Icon>
      <circle cx="9" cy="8" r="3" />
      <path d="M3.5 20c0-3.3 2.5-6 5.5-6s5.5 2.7 5.5 6" />
      <circle cx="17.5" cy="9.5" r="2.25" />
      <path d="M15.8 14.3c2.4.5 4.2 2.7 4.2 5.7" />
    </Icon>
  );
}

/**
 * Ключ → компонент. Ключі відповідають рядковим значенням `handle.icon` у
 * `routes.ts` — один нав-пункт, один ключ, одна іконка.
 */
export const navIcons: Record<string, () => JSX.Element> = {
  documents: DocumentsIcon,
  templates: TemplatesIcon,
  registries: RegistriesIcon,
  methodologies: MethodologiesIcon,
  expressions: ExpressionsIcon,
  units: UnitsIcon,
  security: SecurityIcon,
  periods: PeriodsIcon,
  sources: SourcesIcon,
  mapping: MappingIcon,
  jobs: JobsIcon,
  snapshots: SnapshotsIcon,
  audit: AuditIcon,
  uiStrings: UiStringsIcon,
  health: HealthIcon,
  myGroups: MyGroupsIcon,
};

/**
 * Рендерить іконку пункту навбару за ключем `handle.icon`.
 *
 * ⚠ Немає ключа або ключ не знайдено в `navIcons` — повертає `null`, а не
 * кидає: відсутня/помилкова іконка не має ламати рендер навбару (лейбл і
 * так лишається доступним ім'ям пункту).
 */
export function NavIcon({ name }: { name?: string | undefined }): JSX.Element | null {
  if (name === undefined) return null;

  const Component = navIcons[name];
  return Component ? <Component /> : null;
}
