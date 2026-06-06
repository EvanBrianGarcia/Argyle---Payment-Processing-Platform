import { ApiError } from '../../lib/api/client';
import { ErrorNotice } from '../../components/ui/ErrorNotice';
import { useCapturePayment } from './usePaymentDetail';
import styles from './CaptureAction.module.css';

interface CaptureActionProps {
  paymentId: string;
}

export function CaptureAction({ paymentId }: CaptureActionProps) {
  const mutation = useCapturePayment(paymentId);

  return (
    <div className={styles.wrap}>
      <button
        type="button"
        className={styles.primary}
        onClick={() => mutation.mutate()}
        disabled={mutation.isPending}
      >
        {mutation.isPending ? 'Capturing…' : 'Capture payment'}
      </button>
      <button type="button" className={styles.secondary} disabled>
        Refund
      </button>
      {mutation.isError && (
        <div className={styles.error}>
          <ErrorNotice
            code={mutation.error instanceof ApiError ? mutation.error.code : 'capture_failed'}
            message={mutation.error?.message ?? 'Capture failed.'}
            requestId={
              mutation.error instanceof ApiError ? mutation.error.requestId : null
            }
          />
        </div>
      )}
    </div>
  );
}
