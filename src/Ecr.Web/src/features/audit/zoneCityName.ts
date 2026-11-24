/**
 * Назва поясу для людини (✎ P-1124-4): `Asia/Atyrau` → «Atyrau», `America/Argentina/Buenos_Aires` → «Buenos Aires».
 * Не переклад — та сама назва з бази поясів, лише без регіону й підкреслень.
 */
export function zoneCityName(zone: string): string {
  const city = zone.slice(zone.lastIndexOf('/') + 1).replace(/_/g, ' ');
  return city === '' ? zone : city;
}
