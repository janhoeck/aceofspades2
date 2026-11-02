import type { Metadata } from "next";
import { MessagePage } from "@/components/MessagePage";

export const metadata: Metadata = { title: "Angemeldet", robots: { index: false } };

/** The launcher sends the browser here once it has received the one-time code. */
export default function LoginDone() {
  return (
    <MessagePage title="Angemeldet">
      <p>Du kannst dieses Fenster schließen und zum Launcher zurückkehren.</p>
    </MessagePage>
  );
}
