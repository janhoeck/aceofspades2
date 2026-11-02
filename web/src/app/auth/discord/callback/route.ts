import { NextResponse, type NextRequest } from "next/server";
import { completeLogin } from "@/lib/backend";
import { getConfig } from "@/lib/config";
import { CALLBACK_PATH, clientIp, errorUrl, isLoopbackUrl, LOGIN_COOKIE } from "@/lib/login";

/**
 * Step 2: Discord redirects back here (?code=…&state=… or ?error=…). This address is registered as a
 * redirect in the Discord app. The backend exchanges the code with Discord and tells us where the launcher waits.
 */
export async function GET(request: NextRequest) {
  // Read the request first: then Next.js runs the handler at request time instead of prerendering it at build time.
  const query = request.nextUrl.searchParams;
  const state = query.get("state");
  const config = getConfig();

  // Only the browser that started the login has the cookie. Otherwise a planted return link from
  // another browser could sign the launcher in with someone else's account.
  const cookie = request.cookies.get(LOGIN_COOKIE)?.value;
  if (!state || cookie !== state) return finish(errorUrl(config.siteUrl, "expired"));

  const result = await completeLogin(
    config,
    { attemptId: state, code: query.get("code"), error: query.get("error") },
    clientIp(request.headers),
  );
  if (!result.ok) return finish(errorUrl(config.siteUrl, result.reason));
  if (!isLoopbackUrl(result.redirectUrl)) return finish(errorUrl(config.siteUrl, "unavailable"));

  // On to the launcher at 127.0.0.1. It then sends the browser to /login/done.
  return finish(result.redirectUrl);
}

function finish(url: string) {
  const response = NextResponse.redirect(url);
  response.cookies.delete({ name: LOGIN_COOKIE, path: CALLBACK_PATH });
  return response;
}
