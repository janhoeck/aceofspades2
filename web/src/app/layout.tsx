import type { Metadata } from "next";
import { Barlow, Bungee } from "next/font/google";
import "./globals.css";

// Same fonts as the launcher (SIL Open Font License).
const bungee = Bungee({ variable: "--font-bungee", weight: "400", subsets: ["latin"] });
const barlow = Barlow({ variable: "--font-barlow", weight: ["400", "500", "600"], subsets: ["latin"] });

export const metadata: Metadata = {
  title: { default: "Ace of Spades 2", template: "%s – Ace of Spades 2" },
  description: "Team-Shooter in einer vollständig zerstörbaren Blockwelt. Blau gegen Grün.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="de" className={`${bungee.variable} ${barlow.variable} h-full antialiased`}>
      <body className="flex min-h-full flex-col">{children}</body>
    </html>
  );
}
