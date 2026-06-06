import type { PaymentStatus } from '../../lib/api/types';
import { PAYMENT_STATUSES } from '../../lib/api/types';
import styles from './StatusFilterChips.module.css';

interface StatusFilterChipsProps {
  activeStatus: PaymentStatus | undefined;
  onSelect: (status: PaymentStatus | undefined) => void;
}

export function StatusFilterChips({ activeStatus, onSelect }: StatusFilterChipsProps) {
  return (
    <div role="toolbar" aria-label="Filter by status" className={styles.row}>
      <Chip
        label="All"
        isActive={activeStatus === undefined}
        onClick={() => onSelect(undefined)}
      />
      {PAYMENT_STATUSES.map((status) => (
        <Chip
          key={status}
          label={status}
          isActive={activeStatus === status}
          onClick={() => onSelect(status)}
        />
      ))}
    </div>
  );
}

interface ChipProps {
  label: string;
  isActive: boolean;
  onClick: () => void;
}

function Chip({ label, isActive, onClick }: ChipProps) {
  return (
    <button
      type="button"
      className={`${styles.chip} ${isActive ? styles.chipActive : ''}`}
      onClick={onClick}
      aria-pressed={isActive}
    >
      {label}
    </button>
  );
}
