import clsx from 'clsx';
import type { PaymentStatus } from '../../lib/api/types';
import styles from './StatusBadge.module.css';

interface StatusBadgeProps {
  status: PaymentStatus;
  size?: 'sm' | 'md' | 'lg';
}

const VARIANT: Record<PaymentStatus, string> = {
  Settled: styles.settled!,
  Failed: styles.failed!,
  Refunded: styles.refunded!,
  Pending: styles.pending!,
  Authorized: styles.authorized!,
  Captured: styles.captured!,
};

export function StatusBadge({ status, size = 'md' }: StatusBadgeProps) {
  return (
    <span
      className={clsx(styles.badge, VARIANT[status], styles[`size-${size}`])}
      role="status"
      aria-label={`Status: ${status}`}
    >
      <span aria-hidden="true" className={styles.dot} />
      {status}
    </span>
  );
}
