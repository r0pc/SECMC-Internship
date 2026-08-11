/**
 * The rules a question has to satisfy before it is worth sending.
 *
 * Its own module rather than part of `app/assistant/actions.ts` because both sides need it and
 * that file is `"use server"` — which may only export async functions, so a constant and a plain
 * helper cannot live there. Sharing them from here is what keeps the counter under the textarea
 * and the check in the Server Function counting the same thing.
 *
 * The API enforces all of this again. These copies exist so a limit is visible while typing and
 * costs no round trip when crossed, never as the only place it is applied.
 */

/**
 * Ceiling on one question, in words — the API's `MaxQuestionWords`.
 *
 * A question is one thing asked; past about a hundred words it is a paragraph carrying several,
 * and the assistant answers some part of it with a query that looks like it answered all of it.
 */
export const MAX_QUESTION_WORDS = 100;

/** Storage bound: the audit log shreds the question out of the transcript as `NVARCHAR(2000)`. */
export const MAX_QUESTION_LENGTH = 2000;

/**
 * Words in a question: runs of non-whitespace, however they are separated.
 *
 * The crudest definition that matches what a person counting would get, and deliberately the same
 * one the API applies. Anything cleverer — splitting hyphenated compounds, folding punctuation —
 * would let the two disagree, and a browser reading 98 where the API reads 101 turns a limit into
 * a bug report.
 */
export function countWords(text: string): number {
  const trimmed = text.trim();

  return trimmed.length === 0 ? 0 : trimmed.split(/\s+/).length;
}
