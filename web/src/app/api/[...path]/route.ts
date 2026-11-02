import type { NextRequest } from "next/server";
import { getConfig } from "@/lib/config";
import { clientIp } from "@/lib/login";

/**
 * Forwards /api/* to the backend so the launcher, game and game servers only need one address.
 * /api/internal/* stays out: it is for the website itself only.
 */
async function proxy(request: NextRequest, context: RouteContext<"/api/[...path]">) {
  const { path } = await context.params;
  if (path[0] === "internal") return new Response(null, { status: 404 });

  const config = getConfig();
  const target = `${config.backendUrl}/api/${path.map(encodeURIComponent).join("/")}${request.nextUrl.search}`;

  // Only forward what the API needs. Cookies of the website never reach the backend.
  const headers = new Headers();
  for (const name of ["authorization", "content-type", "accept", "user-agent"]) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  const ip = clientIp(request.headers);
  if (ip) headers.set("x-forwarded-for", ip);

  let response: Response;
  try {
    response = await fetch(target, {
      method: request.method,
      headers,
      body: request.method === "GET" || request.method === "HEAD" ? undefined : await request.arrayBuffer(),
      redirect: "manual",
      cache: "no-store",
    });
  } catch {
    return Response.json({ title: "Backend nicht erreichbar." }, { status: 502 });
  }

  const responseHeaders = new Headers();
  for (const name of ["content-type", "cache-control", "www-authenticate", "retry-after"]) {
    const value = response.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }
  return new Response(response.body, { status: response.status, headers: responseHeaders });
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const PATCH = proxy;
export const DELETE = proxy;
