import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { Suspense } from "react";
import { MessagePage } from "@/components/MessagePage";
import { getConfig } from "@/lib/config";

export const metadata: Metadata = { title: "Entwicklungs-Login", robots: { index: false } };

/**
 * Stand-in for the Discord page during development (AOS2_DEV_LOGIN=true, backend with Discord:UseDevLogin).
 * Without that setting the page does not exist.
 */
export default function DevAuthorize({ searchParams }: PageProps<"/dev/discord/authorize">) {
  return (
    <Suspense>
      <DevForm searchParams={searchParams} />
    </Suspense>
  );
}

async function DevForm({ searchParams }: Pick<PageProps<"/dev/discord/authorize">, "searchParams">) {
  // Read the setting at request time, not at build time.
  await connection();
  if (!getConfig().devLogin) notFound();
  const { state } = await searchParams;

  return (
    <MessagePage title="Entwicklungs-Login">
      <p>Statt Discord: Mit welchem Namen willst du dich anmelden?</p>
      <form method="get" action="/dev/discord/approve" className="space-y-4">
        <input type="hidden" name="state" value={typeof state === "string" ? state : ""} />
        <input
          name="name"
          defaultValue="Testspieler"
          maxLength={32}
          required
          autoFocus
          aria-label="Name"
          className="h-12 w-full border-2 border-stone bg-night px-4 text-sand outline-none focus:border-gold"
        />
        <div className="flex flex-wrap gap-3">
          <button type="submit" className="block-button primary">
            Anmelden
          </button>
          <button type="submit" name="deny" value="1" formNoValidate className="block-button">
            Abbrechen
          </button>
        </div>
      </form>
      <p className="text-sm text-hint">
        Diese Seite gibt es nur mit <code>AOS2_DEV_LOGIN=true</code>.
      </p>
    </MessagePage>
  );
}
