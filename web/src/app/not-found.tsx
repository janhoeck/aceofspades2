import { MessagePage } from "@/components/MessagePage";

export default function NotFound() {
  return (
    <MessagePage tone="coral" title="Hier ist nichts">
      <p>Diese Seite gibt es nicht. Vielleicht wurde der Block schon abgebaut.</p>
    </MessagePage>
  );
}
