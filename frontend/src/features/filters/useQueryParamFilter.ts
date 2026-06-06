import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { isPaymentStatus, type PaymentStatus } from '../../lib/api/types';

export interface ListFilters {
  status: PaymentStatus | undefined;
  cursor: string | undefined;
}

const STATUS_KEY = 'status';
const CURSOR_KEY = 'cursor';

export function useQueryParamFilter() {
  const [searchParams, setSearchParams] = useSearchParams();

  const statusRaw = searchParams.get(STATUS_KEY) ?? undefined;
  const filters: ListFilters = {
    status: statusRaw && isPaymentStatus(capitalize(statusRaw))
      ? (capitalize(statusRaw) as PaymentStatus)
      : undefined,
    cursor: searchParams.get(CURSOR_KEY) ?? undefined,
  };

  const setStatus = useCallback(
    (status: PaymentStatus | undefined) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        // Filter change always clears cursor — no stale page.
        next.delete(CURSOR_KEY);
        if (status) {
          next.set(STATUS_KEY, status);
        } else {
          next.delete(STATUS_KEY);
        }
        return next;
      });
    },
    [setSearchParams],
  );

  const setCursor = useCallback(
    (cursor: string | undefined) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        if (cursor) {
          next.set(CURSOR_KEY, cursor);
        } else {
          next.delete(CURSOR_KEY);
        }
        return next;
      });
    },
    [setSearchParams],
  );

  return { filters, setStatus, setCursor };
}

function capitalize(value: string): string {
  if (value.length === 0) return value;
  return value.charAt(0).toUpperCase() + value.slice(1).toLowerCase();
}
