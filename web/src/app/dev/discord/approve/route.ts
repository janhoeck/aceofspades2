import { NextResponse, type NextRequest } from "next/server";
import { getConfig } from "@/lib/config";
import { CALLBACK_PATH, devCode } from "@/lib/login";

/** Development login: form target of /dev/discord/authorize. Pretends Discord is redirecting back. */
export async function GET(request: NextRequest) {
  // Read the request first: then Next.js runs the handler at request time instead of prerendering it at build time.
  const query = request.nextUrl.searchParams;
  const config = getConfig();
  if (!config.devLogin) return new NextResponse(null, { status: 404 });

  const name = query.get("name")?.trim();
  const target = new URL(CALLBACK_PATH, config.siteUrl);
  target.searchParams.set("state", query.get("state") ?? "");
  if (query.has("deny") || !name) target.searchParams.set("error", "access_denied");
  else target.searchParams.set("code", devCode(name.slice(0, 32)));

  return NextResponse.redirect(target);
}
