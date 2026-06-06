import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { StatusBadge } from './StatusBadge';
import { PAYMENT_STATUSES } from '../../lib/api/types';

describe('StatusBadge', () => {
  it.each(PAYMENT_STATUSES)('renders the %s status with accessible name', (status) => {
    render(<StatusBadge status={status} />);
    expect(screen.getByRole('status', { name: `Status: ${status}` })).toHaveTextContent(status);
  });
});
