import createClient, { type Middleware } from "openapi-fetch";

import { getAccessToken } from "./auth-token";
import type { paths } from "./schema";

// Paths in docs/api/openapi.yaml are declared WITHOUT the `/api/v1` prefix (it lives in
// `servers`), so the prefix belongs here, exactly once. Web and API share one domain
// behind CloudFront, so no CORS setup is needed. NEXT_PUBLIC_API_BASE_URL is only an
// optional override for local development against a separately running backend.
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? "/api/v1";

const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = await getAccessToken();
    if (token) {
      request.headers.set("Authorization", `Bearer ${token}`);
    }
    return request;
  },
};

export const api = createClient<paths>({ baseUrl: API_BASE_URL });
api.use(authMiddleware);
