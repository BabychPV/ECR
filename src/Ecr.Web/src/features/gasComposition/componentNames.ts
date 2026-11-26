import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { GasComponent } from './api';

/**
 * Типова назва компонента мовою інтерфейсу, коли в довіднику COMPONENT власної немає (імпорт FLERT заводить компонент із
 * назвою, рівною коду). ⚠ Ключі — літералами: сторож каталогу рядків бачить кожен. Коду без рядка тут немає навмисно
 * (`CN1_2`, `CN2_2`, `CN3_1` — значення колонок FLERT невідоме, назву не вигадуємо): такий компонент показується кодом.
 */
const DefaultName: Readonly<Record<string, () => string>> = {
  Nitrogen: () => t('gasComposition.component.Nitrogen'),
  CO2: () => t('gasComposition.component.CO2'),
  H2S: () => t('gasComposition.component.H2S'),
  Methane: () => t('gasComposition.component.Methane'),
  Ethane: () => t('gasComposition.component.Ethane'),
  Propane: () => t('gasComposition.component.Propane'),
  IC4_1: () => t('gasComposition.component.IC4_1'),
  NC4_1: () => t('gasComposition.component.NC4_1'),
  IC5_1: () => t('gasComposition.component.IC5_1'),
  NC5_1: () => t('gasComposition.component.NC5_1'),
  C6_1: () => t('gasComposition.component.C6_1'),
  C7_1: () => t('gasComposition.component.C7_1'),
  C8_1: () => t('gasComposition.component.C8_1'),
  C9_1: () => t('gasComposition.component.C9_1'),
  C10_1: () => t('gasComposition.component.C10_1'),
  C11_1: () => t('gasComposition.component.C11_1'),
  C12_1: () => t('gasComposition.component.C12_1'),
  C13_1: () => t('gasComposition.component.C13_1'),
  C14_1: () => t('gasComposition.component.C14_1'),
  Benzene: () => t('gasComposition.component.Benzene'),
  Toluene: () => t('gasComposition.component.Toluene'),
  pXylene: () => t('gasComposition.component.pXylene'),
  EBenzene: () => t('gasComposition.component.EBenzene'),
  MMercaptan: () => t('gasComposition.component.MMercaptan'),
  EMercaptan: () => t('gasComposition.component.EMercaptan'),
  nPMercaptan: () => t('gasComposition.component.nPMercaptan'),
  nBMercaptan: () => t('gasComposition.component.nBMercaptan'),
  CS2: () => t('gasComposition.component.CS2'),
  COS: () => t('gasComposition.component.COS'),
  H2O: () => t('gasComposition.component.H2O'),
  TEGlycol: () => t('gasComposition.component.TEGlycol'),
  SO2: () => t('gasComposition.component.SO2'),
  O2: () => t('gasComposition.component.O2'),
  NH3: () => t('gasComposition.component.NH3'),
  H2: () => t('gasComposition.component.H2'),
  CO: () => t('gasComposition.component.CO'),
  C2H7NO: () => t('gasComposition.component.C2H7NO'),
  Sulfur: () => t('gasComposition.component.Sulfur'),
  C2H6S2: () => t('gasComposition.component.C2H6S2'),
  C3H8S2: () => t('gasComposition.component.C3H8S2'),
  C4H10S2: () => t('gasComposition.component.C4H10S2'),
  C4H11NO2: () => t('gasComposition.component.C4H11NO2'),
  Methanol: () => t('gasComposition.component.Methanol'),
};

/**
 * Людська назва компонента: власна з довідника (мовою користувача), інакше типова з каталогу рядків; undefined — назви
 * немає, показуємо лише код. Код поруч показує редактор завжди.
 */
export function componentName(component: Pick<GasComponent, 'code' | 'names'>): string | undefined {
  const own = localized({ values: component.names });
  const name = own.length > 0 ? own : Object.hasOwn(DefaultName, component.code) ? DefaultName[component.code]?.() : undefined;
  // Назва, що повторює код (`Methane`), нічого не додає — показуємо сам код, без «Methane (Methane)».
  return name === undefined || name.trim().toLowerCase() === component.code.toLowerCase() ? undefined : name;
}