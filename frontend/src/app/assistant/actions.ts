"use server";

/**
 * The assistant's two mutations, as Server Functions.
 *
 * Routed through the server rather than called from the browser so this app keeps its one rule:
 * only the server talks to the API (SOW 4.2). That also means `NEXT_PUBLIC_API_BASE_URL` is not
 * the thing standing between the chat and the backend, and no CORS allowance is needed for a page
 * that would otherwise be the first client-side caller in the app.
 *
 * Both return an outcome rather than throwing. A rejected question is an ordinary result here —
 * the assistant refusing to answer is the system working — and an unreachable API needs to render
 * as a message in the transcript, not as a blown-up route.
 */

import { ApiError, askAssistant, submitAssistantFeedback } from "@/lib/api";
import type { AssistantAnswerDto } from "@/types/api";

export type AskResult =
  | { readonly ok: true; readonly answer: AssistantAnswerDto }
  | { readonly ok: false; readonly title: string; readonly detail: string };

/** Length ceiling the API enforces; checked here too so a typo costs no round trip. */
const MAX_QUESTION_LENGTH = 2000;

export async function ask(
  question: string,
  sessionId: string | null,
): Promise<AskResult> {
  const trimmed = question.trim();

  if (trimmed.length === 0) {
    return {
      ok: false,
      title: "Nothing to ask",
      detail: "Type a question first.",
    };
  }

  if (trimmed.length > MAX_QUESTION_LENGTH) {
    return {
      ok: false,
      title: "Question too long",
      detail: `Questions are limited to ${MAX_QUESTION_LENGTH} characters; that one is ${trimmed.length}.`,
    };
  }

  try {
    const answer = await askAssistant({
      question: trimmed,
      sessionId: sessionId ?? undefined,
    });

    return { ok: true, answer };
  } catch (error) {
    if (error instanceof ApiError) {
      return {
        ok: false,
        title: error.problem.title ?? "Request failed",
        // Shown verbatim. A 503 here names the setting an operator has to fix, and paraphrasing
        // it would lose exactly the part worth reading.
        detail:
          error.problem.detail ??
          "The assistant could not answer. Try again in a moment.",
      };
    }

    throw error;
  }
}

/**
 * Records a thumbs up or down. Returns whether it landed, and nothing else: feedback failing is
 * worth telling the user about quietly, and is never worth losing the answer over.
 */
export async function rate(
  assistantQueryId: number,
  isHelpful: boolean,
): Promise<boolean> {
  try {
    await submitAssistantFeedback(assistantQueryId, { isHelpful });
    return true;
  } catch (error) {
    if (error instanceof ApiError) {
      return false;
    }

    throw error;
  }
}
