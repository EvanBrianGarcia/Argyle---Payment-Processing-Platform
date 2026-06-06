function readRequired(name: string, value: string | undefined): string {
  if (!value || value.trim().length === 0) {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return value;
}

export const env = {
  apiBaseUrl: readRequired('VITE_API_BASE_URL', import.meta.env.VITE_API_BASE_URL),
  devBearerToken: readRequired('VITE_DEV_BEARER_TOKEN', import.meta.env.VITE_DEV_BEARER_TOKEN),
  envLabel: import.meta.env.VITE_ENV_LABEL ?? 'dev',
} as const;
