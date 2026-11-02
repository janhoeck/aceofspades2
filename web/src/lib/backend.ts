import type { SiteConfig } from "./config";
import type { LoginParams } from "./login";

/**
 * Calls to the backend's internal login API (/api/internal/login/…). Server-side only:
 * the key in the X-Internal-Key header must never reach the browser.
 */

export type StartResult =
  | { ok: true; attemptId: string; authorizeUrl: string; maxAgeSeconds: number }
  | { ok: false; reason: "invalid" | "unavailable" };

export type CompleteResult =
  | { ok: true; redirectUrl: string }
  | { ok: false; reason: "expired" | "unavailable" };

const TIMEOUT_MS = 15_000;

async function callInternal(config: SiteConfig, path: string, body: unknown, ip: string | null): Promise<Response | null> {
  const headers: Record<string, string> = {
    "content-type": "application/json",
    "x-internal-key": config.internalKey,
  };
  // So the backend rate-limits per player instead of counting everything under the website's IP.
  if (ip) headers["x-forwarded-for"] = ip;

  try {
    return await fetch(`${config.backendUrl}${path}`, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      cache: "no-store",
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
  } catch {
    return null;
  }
}

export async function startLogin(config: SiteConfig, params: LoginParams, ip: string | null): Promise<StartResult> {
  const response = await callInternal(config, "/api/internal/login/start", params, ip);
  if (response?.status === 400) return { ok: false, reason: "invalid" };
  if (!response?.ok) return { ok: false, reason: "unavailable" };

  const data = (await response.json()) as { attemptId: string; authorizeUrl: string; maxAgeSeconds: number };
  return { ok: true, ...data };
}

export async function completeLogin(
  config: SiteConfig,
  request: { attemptId: string; code: string | null; error: string | null },
  ip: string | null,
): Promise<CompleteResult> {
  const response = await callInternal(config, "/api/internal/login/complete", request, ip);
  if (response?.status === 404) return { ok: false, reason: "expired" };
  if (!response?.ok) return { ok: false, reason: "unavailable" };

  const data = (await response.json()) as { redirectUrl: string };
  return { ok: true, redirectUrl: data.redirectUrl };
}
