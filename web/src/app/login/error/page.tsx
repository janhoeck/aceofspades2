import type { Metadata } from "next";
import { Suspense } from "react";
import { MessagePage } from "@/components/MessagePage";
import type { FailureReason } from "@/lib/login";

export const metadata: Metadata = { title: "Anmeldung nicht möglich", robots: { index: false } };

// Player-facing text stays German.
const messages: Record<FailureReason, { title: string; text: string }> = {
  access_denied: {
    title: "Anmeldung abgebrochen",
    text: "Du hast die Anmeldung bei Discord abgebrochen. Im Launcher kannst du es noch einmal versuchen.",
  },
  discord_failed: {
    title: "Discord antwortet nicht",
    text: "Discord hat nicht richtig geantwortet. Versuch es im Launcher gleich noch einmal.",
  },
  expired: {
    title: "Anmeldung abgelaufen",
    text: "Diese Anmeldung ist abgelaufen oder wurde in einem anderen Browser gestartet. Starte sie bitte im Launcher neu.",
  },
  invalid: {
    title: "Ungültige Anmeldung",
    text: "Dieser Link stammt nicht vom Launcher. Starte die Anmeldung bitte im Launcher.",
  },
  unavailable: {
    title: "Server nicht erreichbar",
    text: "Der Anmeldeserver antwortet gerade nicht. Versuch es später noch einmal.",
  },
};

const fallback = {
  title: "Anmeldung nicht möglich",
  text: "Etwas ist schiefgegangen. Starte die Anmeldung bitte im Launcher neu.",
};

/** Cancelled or failed login, ?reason=… (see FailureReason). */
export default function LoginError({ searchParams }: PageProps<"/login/error">) {
  return (
    <Suspense fallback={<MessagePage tone="coral" title={fallback.title}>{null}</MessagePage>}>
      <Reason searchParams={searchParams} />
    </Suspense>
  );
}

async function Reason({ searchParams }: Pick<PageProps<"/login/error">, "searchParams">) {
  const { reason } = await searchParams;
  const message = messages[reason as FailureReason] ?? fallback;

  return (
    <MessagePage tone="coral" title={message.title}>
      <p>{message.text}</p>
    </MessagePage>
  );
}
