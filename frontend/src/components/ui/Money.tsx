import { formatMoneyParts } from '../../lib/format/money';
import styles from './Money.module.css';

interface MoneyProps {
  amountMinor: number;
  currency: string;
  size?: 'sm' | 'md' | 'lg';
}

export function Money({ amountMinor, currency, size = 'md' }: MoneyProps) {
  const { sign, whole, fractional, currency: code } = formatMoneyParts(amountMinor, currency);
  const className = `${styles.money} ${styles[`size-${size}`]}`;
  return (
    <span className={className}>
      <span className={styles.number}>
        {sign}
        {whole}
        {fractional.length > 0 && (
          <>
            <span className={styles.dot}>.</span>
            <span className={styles.fractional}>{fractional}</span>
          </>
        )}
      </span>
      <span className={styles.currency}>{code}</span>
    </span>
  );
}
