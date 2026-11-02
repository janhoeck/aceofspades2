import { NextResponse, type NextRequest } from "next/server";
import { startLogin } from "@/lib/backend";
import { getConfig } from "@/lib/config";
import { clientIp, errorUrl, LOGIN_COOKIE, loginCookieOptions, parseLoginParams } from "@/lib/login";

/**
 * Step 1 of the login: the launcher opens /login?port=…&state=…&code_challenge=… in the browser.
 * The backend records the attempt; we bind it to this browser with a cookie and send the browser to Discord.
 */
export async function GET(request: NextRequest) {
  // Read the request first: then Next.js runs the handler at request time instead of prerendering it at build time.
  const params = parseLoginParams(request.nextUrl.searchParams);
  const config = getConfig();
  if (!params) return NextResponse.redirect(errorUrl(config.siteUrl, "invalid"));

  const result = await startLogin(config, params, clientIp(request.headers));
  if (!result.ok) return NextResponse.redirect(errorUrl(config.siteUrl, result.reason));

  const response = NextResponse.redirect(result.authorizeUrl);
  response.cookies.set(LOGIN_COOKIE, result.attemptId, loginCookieOptions(config.siteUrl, result.maxAgeSeconds));
  return response;
}
