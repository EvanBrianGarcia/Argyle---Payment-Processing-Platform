import type { Payment, PaymentStatus } from '../../lib/api/types';
import { PAYMENT_STATUSES } from '../../lib/api/types';
import styles from './StatusRail.module.css';

interface StatusRailProps {
  payments: Payment[];
  activeStatus: PaymentStatus | undefined;
  onSelect: (status: PaymentStatus | undefined) => void;
}

export function StatusRail({ payments, activeStatus, onSelect }: StatusRailProps) {
  const counts = countByStatus(payments);
  return (
    <section className={styles.rail} aria-label="Payment status overview">
      {PAYMENT_STATUSES.map((status) => {
        const isActive = activeStatus === status;
        return (
          <button
            key={status}
            type="button"
            className={`${styles.tile} ${isActive ? styles.tileActive : ''}`}
            onClick={() => onSelect(isActive ? undefined : status)}
            aria-pressed={isActive}
            title="Counts reflect the current page. Backend aggregate not implemented."
          >
            <span className={styles.label}>{status}</span>
            <span className={styles.value}>{counts[status]}</span>
          </button>
        );
      })}
    </section>
  );
}

function countByStatus(payments: Payment[]): Record<PaymentStatus, number> {
  const initial: Record<PaymentStatus, number> = {
    Pending: 0,
    Authorized: 0,
    Captured: 0,
    Settled: 0,
    Failed: 0,
    Refunded: 0,
  };
  for (const payment of payments) {
    initial[payment.status] += 1;
  }
  return initial;
}
