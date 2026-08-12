import type { Metadata } from "next";

import { AssistantChat } from "@/components/assistant-chat";
import { PageHeader } from "@/components/card";
import { requireRole } from "@/lib/session";

/**
 * The AI query assistant (FR-13 – FR-16).
 *
 * A question becomes a read-only SQL query over the published analytics views, which is validated,
 * executed as a restricted database principal, and then summarised back in plain language. The
 * query is shown with every answer: a figure whose derivation cannot be inspected is one the
 * reader has to take on trust, and this platform exists to make numbers checkable.
 */

export const metadata: Metadata = {
  title: "Assistant",
  description:
    "Ask questions about the collected CPI and SOFR data in plain language. Every question is turned into a read-only query, shown with its answer, and logged for review.",
};

export default async function AssistantPage() {
  // Analysts and administrators. A Viewer's role is read-only dashboards, and the API answers
  // their assistant calls with a 403 — sending them to a chat that refuses every question would
  // be a worse answer than the dashboard they can use.
  await requireRole("Administrator", "Analyst");

  return (
    <div className="space-y-6">
      <PageHeader
        title="Assistant"
        description={
          <>
            Ask about the data this platform collects — CPI figures, SOFR daily
            rates, and the collection log. Answers are drawn only from what has
            actually been collected; the assistant says so rather than guessing
            when a question is outside that. Every question and the SQL it
            produced is logged for review.
          </>
        }
      />

      <AssistantChat />
    </div>
  );
}
