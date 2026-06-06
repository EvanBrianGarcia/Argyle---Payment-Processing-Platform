import { useNavigate } from 'react-router-dom';
import type { Payment } from '../../lib/api/types';
import { Money } from '../../components/ui/Money';
import { RelativeTime } from '../../components/ui/RelativeTime';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { truncateMiddle } from '../../lib/format/id';
import styles from './PaymentsTable.module.css';

interface PaymentsTableProps {
  payments: Payment[];
}

export function PaymentsTable({ payments }: PaymentsTableProps) {
  const navigate = useNavigate();

  function go(id: string) {
    navigate(`/payments/${id}`);
  }

  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th scope="col">Transaction ID</th>
          <th scope="col" className={styles.right}>Amount</th>
          <th scope="col">Status</th>
          <th scope="col">Created</th>
          <th scope="col">Updated</th>
          <th scope="col">Reference</th>
        </tr>
      </thead>
      <tbody>
        {payments.map((payment) => (
          <tr
            key={payment.id}
            className={styles.row}
            tabIndex={0}
            role="link"
            onClick={() => go(payment.id)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                go(payment.id);
              }
            }}
            aria-label={`Open payment ${payment.id}`}
          >
            <td className={styles.id}>{truncateMiddle(payment.id)}</td>
            <td className={styles.right}>
              <Money amountMinor={payment.amountMinor} currency={payment.currency} />
            </td>
            <td><StatusBadge status={payment.status} size="sm" /></td>
            <td><RelativeTime iso={payment.createdAt} /></td>
            <td><RelativeTime iso={payment.updatedAt} /></td>
            <td className={styles.muted}>{payment.customerReference ?? '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
