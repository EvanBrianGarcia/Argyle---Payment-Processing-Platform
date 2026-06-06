// Re-export so test files can override handlers via server.use() without
// reaching across feature boundaries into the test/ directory directly.
export { server } from './msw/server';
