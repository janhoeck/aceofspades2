import { Battlefield } from "@/components/Battlefield";

const features = [
  {
    title: "Alles ist zerstörbar",
    text: "Jeder Block der Kampfzone lässt sich abbauen, zerschießen, sprengen und neu bauen. Verliert ein Turm den Halt, stürzt er ein.",
    color: "bg-gold border-gold-edge",
  },
  {
    title: "Blau gegen Grün",
    text: "Team Deathmatch, 16 gegen 16. Das erste Team mit 50 Punkten gewinnt.",
    color: "bg-blue border-[#2659B8]",
  },
  {
    title: "Vier Klassen",
    text: "Miner graben Tunnel, Assault stürmt nach vorn, Recon kämpft auf Distanz, Rocketeer reißt Mauern ein.",
    color: "bg-green border-[#33853E]",
  },
];

export default function Home() {
  return (
    <>
      <header className="relative h-[420px] overflow-hidden sm:h-[460px]">
        <Battlefield className="absolute inset-0 h-full w-full" />
        <div className="relative flex flex-col items-center px-4 pt-16 text-center sm:pt-20">
          <h1 className="title-block text-4xl sm:text-6xl">Ace of Spades 2</h1>
          <p className="mt-4 text-lg [text-shadow:2px_2px_0_var(--color-title-bar)] sm:text-xl">
            Blau gegen Grün. Alles ist zerstörbar.
          </p>
        </div>
      </header>

      <main className="flex-1 border-t-4 border-night-deep">
        <section className="mx-auto grid max-w-5xl gap-5 px-4 py-14 sm:grid-cols-3">
          {features.map((feature) => (
            <article key={feature.title} className="border-2 border-b-[6px] border-stone bg-night-deep p-6">
              <span className={`block size-4 border-r-2 border-b-2 ${feature.color}`} />
              <h2 className="mt-4 text-xl font-semibold">{feature.title}</h2>
              <p className="mt-2 leading-relaxed text-sand-muted">{feature.text}</p>
            </article>
          ))}
        </section>

        <section className="mx-auto max-w-5xl px-4 pb-16">
          <div className="border-l-4 border-gold bg-title-bar px-6 py-5">
            <h2 className="font-semibold text-gold">Noch in Entwicklung</h2>
            <p className="mt-1 leading-relaxed text-sand-muted">
              Gespielt wird über den Launcher. Er hält das Spiel aktuell, und angemeldet wirst du mit deinem Discord-Konto.
              Diese Website zeigt dabei nur deinen Namen und dein Profilbild an.
            </p>
          </div>
        </section>
      </main>

      <footer className="border-t-2 border-divider px-4 py-6 text-center text-sm text-hint">
        Ace of Spades 2 ·{" "}
        <a className="underline-offset-4 hover:text-sand hover:underline" href="https://github.com/janhoeck/aceofspades2">
          Quellcode auf GitHub
        </a>
      </footer>
    </>
  );
}
