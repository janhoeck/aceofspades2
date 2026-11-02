import Link from "next/link";
import type { ReactNode } from "react";
import { Battlefield } from "./Battlefield";

/** Frame of the login and error pages: a card above the battlefield. */
export function MessagePage({ tone = "gold", title, children }: { tone?: "gold" | "coral"; title: string; children: ReactNode }) {
  return (
    <main className="relative flex flex-1 flex-col items-center justify-center overflow-hidden px-4 py-16">
      <Battlefield className="absolute inset-x-0 bottom-0 h-64 w-full opacity-70 [mask-image:linear-gradient(to_bottom,transparent,black_45%)]" />
      <div className="relative w-full max-w-lg border-2 border-b-[6px] border-stone bg-night-deep px-7 py-7 sm:px-9">
        <div className="flex items-center gap-3">
          <span className={`size-3 border-r-2 border-b-2 ${tone === "gold" ? "border-gold-edge bg-gold" : "border-coral bg-coral-text"}`} />
          <h1 className={`text-2xl font-semibold ${tone === "gold" ? "text-gold" : "text-coral-text"}`}>{title}</h1>
        </div>
        <div className="mt-3 space-y-4 text-lg leading-relaxed text-sand-muted">{children}</div>
      </div>
      <Link href="/" className="title-block relative mt-8 text-lg [text-shadow:2px_2px_0_var(--color-title-shadow)]">
        Ace of Spades 2
      </Link>
    </main>
  );
}
