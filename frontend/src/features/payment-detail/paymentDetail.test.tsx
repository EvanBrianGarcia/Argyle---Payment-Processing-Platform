import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server-export';
import { PaymentDetailPage } from './PaymentDetailPage';
import { fixturePayment } from '../../test/fixtures/payments';

const BASE = 'http://localhost:8080';

function renderDetail(id: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[`/payments/${id}`]}>
        <Routes>
          <Route path="/payments/:id" element={<PaymentDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('PaymentDetailPage — Failed payment', () => {
  const FAILED_ID = 'pay_01H8XK2L000000000000000002';

  it('renders the status badge and amount', async () => {
    renderDetail(FAILED_ID);
    expect(await screen.findByRole('status', { name: 'Status: Failed' })).toBeInTheDocument();
    expect(await screen.findByText(/899/)).toBeInTheDocument();
  });

  it('renders the event timeline in chronological order with the decline reason', async () => {
    renderDetail(FAILED_ID);
    const timeline = await screen.findByRole('list');
    const items = within(timeline).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(items[0]).toHaveTextContent('Payment created');
    expect(items[1]).toHaveTextContent('Processor authorization succeeded');
    expect(items[2]).toHaveTextContent('insufficient_funds');
  });

  it('does not render the Capture button when the payment is Failed', async () => {
    renderDetail(FAILED_ID);
    await screen.findByRole('status', { name: 'Status: Failed' });
    expect(screen.queryByRole('button', { name: /Capture payment/i })).not.toBeInTheDocument();
  });
});

describe('PaymentDetailPage — Authorized payment', () => {
  const AUTHORIZED_ID = 'pay_01H8XP9Q000000000000000003';

  it('renders the Capture button and triggers an optimistic update on success', async () => {
    const user = userEvent.setup();
    renderDetail(AUTHORIZED_ID);
    const captureButton = await screen.findByRole('button', { name: /Capture payment/i });
    expect(captureButton).toBeInTheDocument();

    await user.click(captureButton);

    await waitFor(() => {
      expect(screen.getByRole('status', { name: 'Status: Captured' })).toBeInTheDocument();
    });
  });

  it('rolls back the optimistic update on a 409 conflict', async () => {
    server.use(
      http.post(`${BASE}/v1/payments/${AUTHORIZED_ID}/capture`, () =>
        HttpResponse.json(
          {
            error: {
              code: 'invalid_state_transition',
              message: 'Payment is no longer Authorized.',
              details: null,
              traceId: null,
              requestId: 'req_test_409',
            },
          },
          { status: 409 },
        ),
      ),
    );
    const user = userEvent.setup();
    renderDetail(AUTHORIZED_ID);
    const captureButton = await screen.findByRole('button', { name: /Capture payment/i });
    await user.click(captureButton);

    await waitFor(() => {
      const alert = screen.getByRole('alert');
      expect(within(alert).getByText('invalid_state_transition')).toBeInTheDocument();
    });
    // Rolled back — original Authorized still showing somewhere.
    expect(screen.getByRole('status', { name: 'Status: Authorized' })).toBeInTheDocument();
  });
});

describe('PaymentDetailPage — Not Found', () => {
  it('renders the not-found page on 404', async () => {
    renderDetail('pay_does_not_exist');
    expect(await screen.findByText('payment_not_found')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Back to payments/i })).toBeInTheDocument();
  });
});

describe('PaymentDetailPage — Copy as curl', () => {
  it('writes a curl command to the clipboard', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      writable: true,
      configurable: true,
    });
    renderDetail(fixturePayment('pay_01H8XK2L000000000000000002').id);
    const copyButton = await screen.findByRole('button', { name: /Copy as curl/i });
    await user.click(copyButton);
    expect(writeText).toHaveBeenCalledTimes(1);
    expect(writeText.mock.calls[0]![0]).toContain('Bearer ');
    expect(writeText.mock.calls[0]![0]).toContain('/v1/payments/');
  });
});
