import { Navigate, Route, Routes } from 'react-router-dom';
import { TopNav } from './components/ui/TopNav';
import { ArgyleStrip } from './components/ui/ArgyleStrip';
import { SkipToMain } from './components/ui/SkipToMain';
import { PaymentsListPage } from './features/payments-list/PaymentsListPage';
import { PaymentDetailPage } from './features/payment-detail/PaymentDetailPage';
import { NotFoundPage } from './features/payment-detail/NotFoundPage';

export function App() {
  return (
    <>
      <SkipToMain />
      <ArgyleStrip />
      <TopNav />
      <main id="main" tabIndex={-1}>
        <Routes>
          <Route path="/" element={<Navigate to="/payments" replace />} />
          <Route path="/payments" element={<PaymentsListPage />} />
          <Route path="/payments/:id" element={<PaymentDetailPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </main>
    </>
  );
}
