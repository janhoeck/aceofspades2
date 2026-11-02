import { describe, expect, it } from "vitest";
import { getConfig } from "./config";
import { clientIp, devCode, errorUrl, isLoopbackUrl, loginCookieOptions, parseLoginParams } from "./login";

const CHALLENGE = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
const STATE = "launcher-state-0123456789";

describe("parseLoginParams", () => {
  it("accepts valid launcher parameters", () => {
    const query = new URLSearchParams({ port: "50123", state: STATE, code_challenge: CHALLENGE });
    expect(parseLoginParams(query)).toEqual({ port: 50123, state: STATE, codeChallenge: CHALLENGE });
  });

  it.each([
    { port: "80", state: STATE, code_challenge: CHALLENGE },
    { port: "70000", state: STATE, code_challenge: CHALLENGE },
    { port: "abc", state: STATE, code_challenge: CHALLENGE },
    { port: "50123", state: "short", code_challenge: CHALLENGE },
    { port: "50123", state: "launcher state 0123456789", code_challenge: CHALLENGE },
    { port: "50123", state: STATE, code_challenge: "too-short" },
    { port: "50123", state: STATE },
    {},
  ])("rejects invalid parameters: %o", (params) => {
    expect(parseLoginParams(new URLSearchParams(params as Record<string, string>))).toBeNull();
  });
});

describe("loginCookieOptions", () => {
  it("is https-only and limited to the Discord callback", () => {
    expect(loginCookieOptions("https://aos2.example.org", 600)).toEqual({
      httpOnly: true,
      secure: true,
      sameSite: "lax",
      path: "/auth/discord/callback",
      maxAge: 600,
    });
  });

  it("also works locally over http", () => {
    expect(loginCookieOptions("http://localhost:3000", 600).secure).toBe(false);
  });
});

describe("isLoopbackUrl", () => {
  it.each([
    ["http://127.0.0.1:50123/callback?code=a&state=b", true],
    ["https://127.0.0.1:50123/callback?code=a", false],
    ["http://localhost:50123/callback?code=a", false],
    ["http://127.0.0.1.evil.example/callback", false],
    ["http://127.0.0.1:50123/other", false],
    ["https://evil.example/callback", false],
    ["not a url", false],
  ])("%s → %s", (url, expected) => {
    expect(isLoopbackUrl(url)).toBe(expected);
  });
});

describe("devCode", () => {
  it("encodes the name like the backend (base64url without padding)", () => {
    expect(devCode("Jän Test")).toBe("dev:SsOkbiBUZXN0");
  });
});

describe("clientIp", () => {
  it("takes the first address from X-Forwarded-For", () => {
    expect(clientIp(new Headers({ "x-forwarded-for": "203.0.113.7, 10.0.0.2" }))).toBe("203.0.113.7");
    expect(clientIp(new Headers())).toBeNull();
  });
});

describe("errorUrl", () => {
  it("points to the error page with a reason", () => {
    expect(errorUrl("https://aos2.example.org", "expired")).toBe("https://aos2.example.org/login/error?reason=expired");
  });
});

describe("getConfig", () => {
  const env = {
    SITE_URL: "https://aos2.example.org/",
    BACKEND_INTERNAL_URL: "http://backend:8080",
    BACKEND_INTERNAL_KEY: "key",
  };

  it("reads the environment and strips the trailing slash", () => {
    expect(getConfig(env)).toEqual({
      siteUrl: "https://aos2.example.org",
      backendUrl: "http://backend:8080",
      internalKey: "key",
      devLogin: false,
    });
  });

  it("reports missing settings", () => {
    expect(() => getConfig({ ...env, BACKEND_INTERNAL_KEY: " " })).toThrow("BACKEND_INTERNAL_KEY");
  });
});
