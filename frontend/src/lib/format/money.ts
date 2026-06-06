// Currency exponent overrides. ISO 4217 defaults to 2; the entries below
// cover the currencies our test data uses. Extend on demand — never guess.
const CURRENCY_EXPONENT: Record<string, number> = {
  USD: 2,
  EUR: 2,
  GBP: 2,
  JPY: 0,
};

export interface MoneyParts {
  whole: string;
  fractional: string;
  currency: string;
  sign: '' | '-';
}

export function exponentFor(currency: string): number {
  return CURRENCY_EXPONENT[currency.toUpperCase()] ?? 2;
}

export function formatMoneyParts(amountMinor: number, currency: string): MoneyParts {
  const exponent = exponentFor(currency);
  const sign = amountMinor < 0 ? '-' : '';
  const absMinor = Math.abs(amountMinor);
  const divisor = 10 ** exponent;
  const wholeNumber = Math.floor(absMinor / divisor);
  const fractionalNumber = absMinor % divisor;

  const whole = wholeNumber.toLocaleString('en-US');
  const fractional = exponent === 0
    ? ''
    : String(fractionalNumber).padStart(exponent, '0');

  return { whole, fractional, currency: currency.toUpperCase(), sign };
}

export function formatMoney(amountMinor: number, currency: string): string {
  const { sign, whole, fractional, currency: code } = formatMoneyParts(amountMinor, currency);
  const number = fractional.length > 0 ? `${whole}.${fractional}` : whole;
  return `${sign}${number} ${code}`;
}
