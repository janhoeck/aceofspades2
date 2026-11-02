import { NextRequest } from "next/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GET as proxyGet, POST as proxyPost } from "./api/[...path]/route";
import { GET as callback } from "./auth/discord/callback/route";
import { GET as devApprove } from "./dev/discord/approve/route";
import { GET as login } from "./login/route";

/**
 * The login route handlers with a fake backend (fetch). The backend itself is tested in
 * backend/AoS2.Backend.Tests; this is about what the website does.
 */

const SITE = "https://aos2.test";
const KEY = "website-key-0123456789abcdef0123456";
const CHALLENGE = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
const STATE = "launcher-state-0123456789";

type Call = { url: string; init: RequestInit };
let calls: Call[];
let reply: (call: Call) => Response;

beforeEach(() => {
  vi.stubEnv("SITE_URL", SITE);
  vi.stubEnv("BACKEND_INTERNAL_URL", "http://backend:8080");
  vi.stubEnv("BACKEND_INTERNAL_KEY", KEY);
  vi.stubEnv("AOS2_DEV_LOGIN", "false");
  calls = [];
  reply = () => Response.json({});
  vi.stubGlobal("fetch", async (url: string, init: RequestInit) => {
    const call = { url: String(url), init };
    calls.push(call);
    return reply(call);
  });
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

const request = (path: string, headers?: Record<string, string>, init?: { method?: string; body?: string }) =>
  new NextRequest(new URL(path, SITE), { headers, ...init });

const loginPath = `/login?port=50123&state=${STATE}&code_challenge=${CHALLENGE}`;

describe("/login", () => {
  it("registers the attempt with the backend, sets the cookie and redirects to Discord", async () => {
    reply = () => Response.json({ attemptId: "attempt-1", authorizeUrl: "https://discord.com/oauth2/authorize?state=attempt-1", maxAgeSeconds: 600 });

    const response = await login(request(loginPath, { "x-forwarded-for": "203.0.113.7" }));

    expect(response.status).toBe(307);
    expect(response.headers.get("location")).toBe("https://discord.com/oauth2/authorize?state=attempt-1");
    const cookie = response.headers.get("set-cookie")!;
    expect(cookie).toContain("aos2_login=attempt-1");
    expect(cookie).toContain("Path=/auth/discord/callback");
    expect(cookie).toContain("HttpOnly");
    expect(cookie).toContain("Secure");
    expect(cookie.toLowerCase()).toContain("samesite=lax");
    expect(cookie).toContain("Max-Age=600");

    const [call] = calls;
    expect(call.url).toBe("http://backend:8080/api/internal/login/start");
    const headers = call.init.headers as Record<string, string>;
    expect(headers["x-internal-key"]).toBe(KEY);
    expect(headers["x-forwarded-for"]).toBe("203.0.113.7");
    expect(JSON.parse(call.init.body as string)).toEqual({ port: 50123, state: STATE, codeChallenge: CHALLENGE });
  });

  it("sends invalid links to the error page without asking the backend", async () => {
    const response = await login(request("/login?port=80"));

    expect(response.headers.get("location")).toBe(`${SITE}/login/error?reason=invalid`);
    expect(calls).toHaveLength(0);
  });

  it("reports an unreachable backend", async () => {
    reply = () => {
      throw new TypeError("fetch failed");
    };

    const response = await login(request(loginPath));

    expect(response.headers.get("location")).toBe(`${SITE}/login/error?reason=unavailable`);
  });
});

describe("/auth/discord/callback", () => {
  const withCookie = (path: string, cookie = "attempt-1") => request(path, { cookie: `aos2_login=${cookie}` });

  it("checks the cookie, lets the backend exchange the code and redirects to the launcher", async () => {
    reply = () => Response.json({ redirectUrl: `http://127.0.0.1:50123/callback?code=einmal&state=${STATE}` });

    const response = await callback(withCookie("/auth/discord/callback?code=discord-code&state=attempt-1"));

    expect(response.headers.get("location")).toBe(`http://127.0.0.1:50123/callback?code=einmal&state=${STATE}`);
    expect(response.headers.get("set-cookie")).toContain("aos2_login=;");
    const [call] = calls;
    expect(call.url).toBe("http://backend:8080/api/internal/login/complete");
    expect(JSON.parse(call.init.body as string)).toEqual({ attemptId: "attempt-1", code: "discord-code", error: null });
  });

  it.each([
    ["without cookie", request("/auth/discord/callback?code=c&state=attempt-1")],
    ["with another attempt's cookie", withCookie("/auth/discord/callback?code=c&state=attempt-1", "other-attempt")],
    ["without state", withCookie("/auth/discord/callback?code=c")],
  ])("rejects the return %s without asking the backend", async (_, req) => {
    const response = await callback(req);

    expect(response.headers.get("location")).toBe(`${SITE}/login/error?reason=expired`);
    expect(calls).toHaveLength(0);
  });

  it("passes a cancel at Discord on to the backend", async () => {
    reply = () => Response.json({ redirectUrl: `http://127.0.0.1:50123/callback?error=access_denied&state=${STATE}` });

    const response = await callback(withCookie("/auth/discord/callback?error=access_denied&state=attempt-1"));

    expect(JSON.parse(calls[0].init.body as string)).toEqual({ attemptId: "attempt-1", code: null, error: "access_denied" });
    expect(response.headers.get("location")).toContain("error=access_denied");
  });

  it("shows expired logins as expired", async () => {
    reply = () => Response.json({ title: "Anmeldung abgelaufen." }, { status: 404 });

    const response = await callback(withCookie("/auth/discord/callback?code=c&state=attempt-1"));

    expect(response.headers.get("location")).toBe(`${SITE}/login/error?reason=expired`);
  });

  it("never redirects anywhere but the launcher at 127.0.0.1", async () => {
    reply = () => Response.json({ redirectUrl: "https://evil.example/callback?code=x" });

    const response = await callback(withCookie("/auth/discord/callback?code=c&state=attempt-1"));

    expect(response.headers.get("location")).toBe(`${SITE}/login/error?reason=unavailable`);
  });
});

describe("/dev/discord/approve", () => {
  it("does not exist without AOS2_DEV_LOGIN", async () => {
    expect((await devApprove(request("/dev/discord/approve?state=s&name=Jan"))).status).toBe(404);
  });

  it("pretends Discord returns with the name as code", async () => {
    vi.stubEnv("AOS2_DEV_LOGIN", "true");

    const approve = await devApprove(request("/dev/discord/approve?state=s1&name=J%C3%A4n%20Test"));
    const deny = await devApprove(request("/dev/discord/approve?state=s1&name=Jan&deny=1"));

    expect(approve.headers.get("location")).toBe(`${SITE}/auth/discord/callback?state=s1&code=dev%3ASsOkbiBUZXN0`);
    expect(deny.headers.get("location")).toBe(`${SITE}/auth/discord/callback?state=s1&error=access_denied`);
  });
});

describe("/api/*", () => {
  const context = (path: string[]) => ({ params: Promise.resolve({ path }) });

  it("forwards requests with a token to the backend", async () => {
    reply = () => Response.json({ displayName: "Jan" }, { headers: { "cache-control": "no-store", "set-cookie": "x=y" } });

    const response = await proxyGet(request("/api/me?x=1", { authorization: "Bearer aos2_token", cookie: "secret=1" }), context(["me"]));

    expect(await response.json()).toEqual({ displayName: "Jan" });
    expect(response.headers.get("set-cookie")).toBeNull();
    const [call] = calls;
    expect(call.url).toBe("http://backend:8080/api/me?x=1");
    const headers = call.init.headers as Headers;
    expect(headers.get("authorization")).toBe("Bearer aos2_token");
    expect(headers.get("cookie")).toBeNull();
  });

  it("forwards POST with body", async () => {
    reply = () => new Response(null, { status: 204 });

    const response = await proxyPost(
      request("/api/auth/session", { "content-type": "application/json" }, { method: "POST", body: '{"code":"c"}' }),
      context(["auth", "session"]),
    );

    expect(response.status).toBe(204);
    expect(new TextDecoder().decode(calls[0].init.body as ArrayBuffer)).toBe('{"code":"c"}');
  });

  it("also reaches the key set under /.well-known", async () => {
    await proxyGet(request("/api/.well-known/jwks.json"), context([".well-known", "jwks.json"]));

    expect(calls[0].url).toBe("http://backend:8080/api/.well-known/jwks.json");
  });

  it("does not forward the internal login API", async () => {
    const response = await proxyPost(request("/api/internal/login/start", {}, { method: "POST", body: "{}" }), context(["internal", "login", "start"]));

    expect(response.status).toBe(404);
    expect(calls).toHaveLength(0);
  });

  it("answers 502 when the backend is unreachable", async () => {
    reply = () => {
      throw new TypeError("fetch failed");
    };

    expect((await proxyGet(request("/api/health"), context(["health"]))).status).toBe(502);
  });
});
