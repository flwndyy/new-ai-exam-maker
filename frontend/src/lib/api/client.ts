import createClient from "openapi-fetch";

import { createAuthMiddleware } from "./auth-middleware";
import type { paths } from "./schema";

// Paths in docs/api/openapi.yaml are declared WITHOUT the `/api/v1` prefix (it lives in
// `servers`), so the prefix belongs here, exactly once. Web and API share one domain
// behind CloudFront, so no CORS setup is needed. NEXT_PUBLIC_API_BASE_URL is only an
// optional override for local development against a separately running backend.
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? "/api/v1";

export const api = createClient<paths>({ baseUrl: API_BASE_URL });
api.use(createAuthMiddleware());
