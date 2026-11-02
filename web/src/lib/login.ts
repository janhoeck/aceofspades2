/**
 * Rules of the launcher login, without Next.js and without network access (easy to test).
 * Flow and reasoning: docs/backend.md, section 2. Parameter names match libs/AoS2.Contracts.
 */

/** Cookie that binds the login to this browser. */
export const LOGIN_COOKIE = "aos2_login";

/** Path the browser sends the cookie to: only the return from Discord. */
export const CALLBACK_PATH = "/auth/discord/callback";

export const DONE_PATH = "/login/done";
export const ERROR_PATH = "/login/error";
export const DEV_AUTHORIZE_PATH = "/dev/discord/authorize";

export interface LoginParams {
  port: number;
  state: string;
  codeChallenge: string;
}

/** Reasons shown on the error page. access_denied and discord_failed also come from the launcher (LoginErrors). */
export type FailureReason = "access_denied" | "discord_failed" | "expired" | "invalid" | "unavailable";

const urlSafe = (value: string | null, min: number, max: number): value is string =>
  value !== null && value.length >= min && value.length <= max && /^[A-Za-z0-9_-]+$/.test(value);

/** Validates the parameters the launcher opens /login with. The backend checks them again. */
export function parseLoginParams(query: URLSearchParams): LoginParams | null {
  const port = Number(query.get("port"));
  const state = query.get("state");
  const codeChallenge = query.get("code_challenge");
  if (!Number.isInteger(port) || port < 1024 || port > 65535) return null;
  if (!urlSafe(state, 16, 128) || !urlSafe(codeChallenge, 43, 43)) return null;
  return { port, state, codeChallenge };
}

export interface CookieOptions {
  httpOnly: true;
  secure: boolean;
  sameSite: "lax";
  path: string;
  maxAge: number;
}

/**
 * Cookie settings: invisible to scripts, encrypted-only over https, and "lax" so the browser
 * sends it on the return from Discord (a normal top-level redirect).
 */
export function loginCookieOptions(siteUrl: string, maxAgeSeconds: number): CookieOptions {
  return {
    httpOnly: true,
    secure: siteUrl.startsWith("https://"),
    sameSite: "lax",
    path: CALLBACK_PATH,
    maxAge: maxAgeSeconds,
  };
}

export function errorUrl(siteUrl: string, reason: FailureReason): string {
  return `${siteUrl}${ERROR_PATH}?reason=${reason}`;
}

/** The browser may only be sent to the launcher on this PC, nowhere else. */
export function isLoopbackUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" && parsed.hostname === "127.0.0.1" && parsed.pathname === "/callback";
  } catch {
    return false;
  }
}

/** Development login: the "Discord code" is just the name (same as DevDiscordClient.CreateCode in the backend). */
export function devCode(name: string): string {
  return "dev:" + Buffer.from(name, "utf8").toString("base64url");
}

/** First address from X-Forwarded-For (set by the Coolify proxy). Only used for rate limiting. */
export function clientIp(headers: Headers): string | null {
  return headers.get("x-forwarded-for")?.split(",")[0]?.trim() || null;
}
