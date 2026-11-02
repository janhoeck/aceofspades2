/** For the Docker HEALTHCHECK: is the website running? (The backend is checked via /api/health.) */
export function GET() {
  return new Response("ok");
}
