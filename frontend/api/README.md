# API schema

`openapi.v1.json` is the contract between the backend and this frontend. It is committed to the repo so PR diffs surface contract changes, and so codegen runs without a live backend.

## Regenerate from the live backend

When backend DTOs change, regenerate both the schema and the TypeScript types:

```bash
# 1. Start the backend (docker compose up -d, or dotnet run from backend/)
# 2. From frontend/:
pnpm codegen:fetch
```

That command curls `http://localhost:8080/openapi/v1.json` into `api/openapi.v1.json` and then runs `openapi-typescript` to produce `src/lib/api/generated.ts`.

Commit both files together. CI re-runs codegen against a fresh backend container and fails the build if the working tree differs (see Phase 5 Task 7).
