"use client";

import { useEffect, useRef, useState, useTransition } from "react";

import { ask, listChats, rate, resumeChat } from "@/app/assistant/actions";
import { Table, Td, Th, Tr } from "@/components/table";
import { formatCount, formatTimestamp } from "@/lib/format";
import { MAX_QUESTION_WORDS, countWords } from "@/lib/question";
import type {
  AssistantAnswerDto,
  AssistantModelChoice,
  AssistantSessionSummaryDto,
  AssistantTranscriptTurnDto,
  AssistantValidationOutcome,
} from "@/types/api";

/**
 * The AI query assistant (FR-13 – FR-16).
 *
 * A Client Component because a conversation is state: the transcript, what is in flight, and which
 * answers have been rated all live here. The calls it makes are Server Functions, so the browser
 * still never talks to the API directly.
 *
 * Nothing is kept in `localStorage`. Conversations survive a reload because the server already has
 * them — every turn is written to the session's transcript as it happens — so resuming is a matter
 * of asking which conversations exist and reopening one, not of mirroring users' questions into
 * browser storage where nobody agreed they should be.
 */

/**
 * An answer as this view renders it: a live one, or one replayed out of a stored transcript.
 *
 * The two differ in one field. A live answer always knows which model produced it — the choice is
 * made before the question is sent — while a turn recorded before the choice existed does not say,
 * and the transcript is a document with no migration step that could make it say. Widening the
 * field here rather than defaulting it in `replay` is what keeps that gap visible: filling it with
 * "Cloud" would be inventing a fact about somebody's old conversation.
 */
type Answer = Omit<AssistantAnswerDto, "modelChoice"> & {
  modelChoice: AssistantModelChoice | null;
};

interface Turn {
  /** Local id — the API's id only exists once an answer comes back, and errors never get one. */
  key: number;
  question: string;
  answer?: Answer;
  error?: { title: string; detail: string };
}

const EXAMPLES = [
  "What was CPI in June 2025?",
  "What is the average SOFR rate in 2025?",
  "What was the year over year inflation rate for the last 3 months?",
  "Which sources failed to collect this week?",
] as const;

export function AssistantChat() {
  const [turns, setTurns] = useState<Turn[]>([]);
  const [draft, setDraft] = useState("");
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [chats, setChats] = useState<AssistantSessionSummaryDto[]>([]);
  const [resumeError, setResumeError] = useState<string | null>(null);
  const [pending, startTransition] = useTransition();

  // Per question, not per conversation, and deliberately not reset by starting or resuming a chat:
  // someone who has switched to the local model has said something about how they want to work,
  // not about the conversation they happened to be in. Which model actually answered is recorded
  // on every turn, so a chat that changed models halfway still reads correctly afterwards.
  const [model, setModel] = useState<AssistantModelChoice>("Cloud");

  const nextKey = useRef(0);
  const transcriptEnd = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);

  // Follow the conversation as it grows. Only after a turn is added or resolved — scrolling on
  // every render would fight the user the moment they scrolled up to read something.
  useEffect(() => {
    transcriptEnd.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [turns.length, pending]);

  // The list of past conversations, fetched once on mount. Failure is silent by design: this is a
  // convenience beside the chat, and someone who cannot see their history can still ask a question.
  useEffect(() => {
    let cancelled = false;

    listChats().then((found) => {
      if (!cancelled) {
        setChats(found);
      }
    });

    return () => {
      cancelled = true;
    };
  }, []);

  /** Reopens a past conversation, replacing whatever is on screen. */
  function resume(id: string) {
    if (pending) {
      return;
    }

    setResumeError(null);

    startTransition(async () => {
      const result = await resumeChat(id);

      if (!result.ok) {
        setResumeError(result.detail);
        return;
      }

      // Keys come from nextKey for replayed turns too. Numbering them by array position would
      // collide with the counter the moment a question was asked after resuming: both start at 0,
      // and React would then see two children with the same key.
      setTurns(result.transcript.turns.map((t) => replay(t, nextKey.current++)));
      setSessionId(result.transcript.sessionId);
      setDraft("");
    });
  }

  /** Starts a fresh conversation. The old one is on the server; nothing is lost by leaving it. */
  function startNewChat() {
    if (pending) {
      return;
    }

    setTurns([]);
    setSessionId(null);
    setResumeError(null);
    setDraft("");
    input.current?.focus();
  }

  function submit(question: string) {
    const trimmed = question.trim();

    // Over-length questions are refused rather than truncated or sent anyway. The counter beside
    // the button is already saying so, which is why this can be a silent no-op: Enter doing
    // nothing while a red "112 / 100" sits under the cursor is not a mystery.
    if (
      trimmed.length === 0 ||
      pending ||
      countWords(trimmed) > MAX_QUESTION_WORDS
    ) {
      return;
    }

    const key = nextKey.current++;

    setTurns((current) => [...current, { key, question: trimmed }]);
    setDraft("");

    startTransition(async () => {
      const result = await ask(trimmed, sessionId, model);

      setTurns((current) =>
        current.map((turn) =>
          turn.key !== key
            ? turn
            : result.ok
              ? { ...turn, answer: result.answer }
              : { ...turn, error: { title: result.title, detail: result.detail } },
        ),
      );

      if (result.ok) {
        // Every later question joins the same session, so the audit log groups a conversation.
        setSessionId(result.answer.sessionId);

        // A question that opened a new conversation adds a row to the resume list. Refreshed only
        // then: within a conversation the list's contents do not change, and re-fetching it after
        // every answer would spend a round trip to redraw the same thing.
        if (sessionId === null) {
          listChats().then(setChats);
        }
      }
    });
  }

  const words = countWords(draft);
  const tooLong = words > MAX_QUESTION_WORDS;

  return (
    <div className="flex flex-col gap-4">
      <ChatPicker
        chats={chats}
        currentSessionId={sessionId}
        disabled={pending}
        onResume={resume}
        onNew={startNewChat}
      />

      {resumeError ? (
        <p className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-700 dark:bg-red-950/40 dark:text-red-300">
          {resumeError}
        </p>
      ) : null}

      <div
        className="min-h-[24rem] space-y-6"
        aria-live="polite"
        aria-busy={pending}
      >
        {turns.length === 0 ? (
          <Welcome onPick={submit} disabled={pending} />
        ) : (
          turns.map((turn) => (
            <TurnView key={turn.key} turn={turn} onRate={rate} />
          ))
        )}

        {pending ? <Thinking model={model} /> : null}

        <div ref={transcriptEnd} />
      </div>

      <form
        className="sticky bottom-4 rounded-lg border border-zinc-200 bg-white p-2 shadow-sm dark:border-zinc-800 dark:bg-zinc-950"
        onSubmit={(event) => {
          event.preventDefault();
          submit(draft);
        }}
      >
        <label className="sr-only" htmlFor="assistant-question">
          Ask a question about the collected data, in {MAX_QUESTION_WORDS} words
          or fewer
        </label>
        <textarea
          id="assistant-question"
          ref={input}
          rows={2}
          value={draft}
          disabled={pending}
          // The character cap is a hard stop because it is a storage bound — past it the audit log
          // would keep a truncated question. The word limit is not: an over-long draft is left
          // intact and refused, so the sentence being cut is chosen by the person who wrote it.
          maxLength={2000}
          aria-invalid={tooLong || undefined}
          placeholder="Ask about CPI, SOFR, or collection health…"
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            // Enter sends, Shift+Enter breaks the line. A question is usually one line, and
            // reaching for the mouse to send each one gets old immediately.
            if (event.key === "Enter" && !event.shiftKey) {
              event.preventDefault();
              submit(draft);
            }
          }}
          className="w-full resize-none bg-transparent px-3 py-2 text-sm text-zinc-900 outline-none placeholder:text-zinc-400 disabled:opacity-60 dark:text-zinc-100 dark:placeholder:text-zinc-600"
        />
        <div className="flex flex-wrap items-center justify-between gap-3 px-3 pb-1">
          <div className="flex items-center gap-3">
            <ModelPicker value={model} onChange={setModel} disabled={pending} />
            <p className="text-xs text-zinc-500 dark:text-zinc-400">
              Answers come from collected data only. Every question is logged.
            </p>
          </div>
          <div className="flex items-center gap-3">
            <WordCount words={words} />
            <button
              type="submit"
              disabled={pending || draft.trim().length === 0 || tooLong}
              className="rounded-md bg-zinc-900 px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-zinc-700 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300"
            >
              {pending ? "Asking…" : "Ask"}
            </button>
          </div>
        </div>
      </form>
    </div>
  );
}

/**
 * Turns a stored turn back into the shape a live answer has, so one renderer draws both.
 *
 * The two differ in exactly one way that matters: a replayed turn has no result rows. They were
 * never stored — a turn can return up to 2,000 of them — so a resumed conversation shows the
 * answer text and the query that produced it, and not the table underneath. `resultRowCount`
 * survives, which is what lets the answer still say how many rows it was drawn from.
 *
 * The key is supplied rather than derived from the turn: it has to come from the same counter as
 * live turns, or a resumed conversation and the next question asked in it can claim the same one.
 */
function replay(turn: AssistantTranscriptTurnDto, key: number): Turn {
  return {
    key,
    question: turn.question,
    answer: {
      // Carried through unchanged, null included. See the `Answer` type for why an older turn is
      // left saying nothing rather than defaulted to the model that was the only one at the time.
      modelChoice: turn.modelChoice,
      modelName: turn.modelName,

      assistantQueryId: turn.assistantQueryId,
      sessionId: "",
      questionText: turn.question,
      validationOutcome: turn.outcome,
      generatedSql: turn.generatedSql,
      sqlParameters: turn.sqlParameters as Record<string, unknown> | null,
      explanation: turn.explanation,
      wasExecuted: turn.wasExecuted,
      executionStatus: null,
      answerText: turn.answer ?? "",
      rows: null,
      resultRowCount: turn.resultRowCount,
    },
  };
}

/**
 * The bar above the transcript: start a new conversation, or reopen one.
 *
 * A `<details>` rather than a managed dropdown. The list is closed most of the time and opening it
 * is not application state — the browser already does this, keyboard and screen reader included,
 * and reimplementing it with `useState` would be more code that behaves slightly worse.
 */
function ChatPicker({
  chats,
  currentSessionId,
  disabled,
  onResume,
  onNew,
}: {
  chats: AssistantSessionSummaryDto[];
  currentSessionId: string | null;
  disabled: boolean;
  onResume: (sessionId: string) => void;
  onNew: () => void;
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <details className="group relative">
        <summary className="inline-flex cursor-pointer list-none items-center gap-2 rounded-md border border-zinc-300 px-3 py-1.5 text-xs text-zinc-700 transition-colors hover:border-zinc-400 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-900">
          Previous chats
          <span className="text-zinc-400 dark:text-zinc-600">
            {chats.length === 0 ? "—" : chats.length}
          </span>
        </summary>

        <div className="absolute left-0 z-10 mt-2 max-h-80 w-80 overflow-y-auto rounded-lg border border-zinc-200 bg-white p-1 shadow-lg dark:border-zinc-800 dark:bg-zinc-950">
          {chats.length === 0 ? (
            <p className="px-3 py-4 text-xs text-zinc-500 dark:text-zinc-400">
              Nothing yet. Conversations appear here once they have an answer,
              and stay after you close the page.
            </p>
          ) : (
            <ul>
              {chats.map((chat) => (
                <li key={chat.sessionId}>
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => onResume(chat.sessionId)}
                    aria-current={
                      chat.sessionId === currentSessionId ? "true" : undefined
                    }
                    className="w-full rounded-md px-3 py-2 text-left transition-colors hover:bg-zinc-100 disabled:cursor-not-allowed disabled:opacity-50 aria-[current]:bg-zinc-100 dark:hover:bg-zinc-900 dark:aria-[current]:bg-zinc-900"
                  >
                    {/* The first question, clamped. A conversation is recognised by how it opened. */}
                    <span className="line-clamp-2 text-xs text-zinc-800 dark:text-zinc-200">
                      {chat.title ?? "Untitled conversation"}
                    </span>
                    <span className="mt-0.5 block text-[11px] text-zinc-500 dark:text-zinc-400">
                      {chat.turnCount}
                      {chat.turnCount === 1 ? " question" : " questions"} ·{" "}
                      {formatTimestamp(chat.lastActivityAtPkt)}
                      {/* Omitted rather than shown as a dash when unknown. A chat whose turns
                          never reported usage is a gap in our records, not a fact about the
                          conversation, and this line is read at a glance. */}
                      {chat.totalTokens !== null && (
                        <> · {formatCount(chat.totalTokens)} tokens</>
                      )}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </details>

      <button
        type="button"
        onClick={onNew}
        disabled={disabled}
        className="rounded-md border border-zinc-300 px-3 py-1.5 text-xs text-zinc-700 transition-colors hover:border-zinc-400 hover:bg-zinc-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-900"
      >
        New chat
      </button>
    </div>
  );
}

function Welcome({
  onPick,
  disabled,
}: {
  onPick: (question: string) => void;
  disabled: boolean;
}) {
  return (
    <div className="rounded-lg border border-dashed border-zinc-300 px-5 py-8 dark:border-zinc-700">
      <p className="text-sm font-medium text-zinc-700 dark:text-zinc-300">
        Ask a question about the data this platform collects.
      </p>
      <p className="mt-1 max-w-xl text-sm text-zinc-500 dark:text-zinc-400">
        US consumer price index figures, SOFR daily rates, and the collection
        log. Questions are turned into a read-only SQL query, which is shown
        with every answer.
      </p>
      <ul className="mt-4 flex flex-wrap gap-2">
        {EXAMPLES.map((example) => (
          <li key={example}>
            <button
              type="button"
              disabled={disabled}
              onClick={() => onPick(example)}
              className="rounded-full border border-zinc-300 px-3 py-1.5 text-xs text-zinc-700 transition-colors hover:border-zinc-400 hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-900"
            >
              {example}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * Which model answers the next question.
 *
 * A plain `<select>`. Two options do not need a custom control, and the browser's own comes with
 * keyboard handling, screen-reader support and a touch UI that a pair of styled buttons would have
 * to reimplement worse.
 *
 * It sits in the composer rather than in the bar above, because the choice belongs to the question
 * being written and not to the conversation: it can change between one turn and the next, and each
 * turn records which model actually served it.
 */
function ModelPicker({
  value,
  onChange,
  disabled,
}: {
  value: AssistantModelChoice;
  onChange: (model: AssistantModelChoice) => void;
  disabled: boolean;
}) {
  return (
    <>
      <label className="sr-only" htmlFor="assistant-model">
        Which model answers the question
      </label>
      <select
        id="assistant-model"
        value={value}
        disabled={disabled}
        // The cast is safe because the only values are the two options below, and narrowing it
        // any other way would mean validating a string this component itself wrote.
        onChange={(event) => onChange(event.target.value as AssistantModelChoice)}
        className="rounded-md border border-zinc-300 bg-transparent px-2 py-1 text-xs text-zinc-700 outline-none transition-colors hover:border-zinc-400 disabled:cursor-not-allowed disabled:opacity-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600"
      >
        <option value="Cloud">Cloud model</option>
        <option value="Local">Local model</option>
      </select>
    </>
  );
}

/**
 * Which model produced an answer, under it.
 *
 * Shown rather than folded into the query details, because it is the one thing that explains a
 * difference between two answers to the same question — and the local model's answers are visibly
 * worse often enough that a reader without this would conclude the platform is unreliable rather
 * than that they picked the small model.
 *
 * Nothing is rendered for a turn that does not say. That is every turn recorded before the choice
 * existed, and "Cloud" would be a guess dressed as a record.
 */
function ModelNote({
  choice,
  name,
}: {
  choice: AssistantModelChoice | null;
  name: string | null;
}) {
  if (choice === null) {
    return null;
  }

  return (
    <p className="text-[11px] text-zinc-400 dark:text-zinc-600">
      {choice === "Local" ? "Local model" : "Cloud model"}
      {name ? <> · {name}</> : null}
    </p>
  );
}

/**
 * How close the draft is to the word limit.
 *
 * Silent until the limit is within reach. A counter reading "3 / 100" under every question is
 * noise about a rule almost nobody meets — the one worth showing is the one that has started to
 * matter, and it appears in time to shorten a sentence rather than after the Ask button has
 * already gone dead.
 *
 * The visible count is not a live region: it changes on every keystroke, and announcing "98, 99,
 * 100" is worse than not announcing it. The screen-reader message beside it is the boundary
 * instead, whose text does not change while it is being crossed.
 */
function WordCount({ words }: { words: number }) {
  const over = words > MAX_QUESTION_WORDS;
  const near = words >= MAX_QUESTION_WORDS * 0.75;

  return (
    <>
      <span className="sr-only" aria-live="polite">
        {over ? `Over the ${MAX_QUESTION_WORDS} word limit. Shorten it to ask.` : ""}
      </span>

      {over || near ? (
        <p
          id="assistant-question-count"
          className={
            over
              ? "text-xs font-medium text-red-600 dark:text-red-400"
              : "text-xs text-zinc-500 dark:text-zinc-400"
          }
        >
          {words} / {MAX_QUESTION_WORDS} words
        </p>
      ) : null}
    </>
  );
}

function Thinking({ model }: { model: AssistantModelChoice }) {
  return (
    <div className="flex items-center gap-2 text-sm text-zinc-500 dark:text-zinc-400">
      <span className="flex gap-1" aria-hidden>
        {[0, 150, 300].map((delay) => (
          <span
            key={delay}
            className="size-1.5 animate-bounce rounded-full bg-zinc-400 dark:bg-zinc-600"
            style={{ animationDelay: `${delay}ms` }}
          />
        ))}
      </span>
      {/* Named rather than a bare spinner: this takes seconds, and saying which step it is on is
          the difference between "working" and "stuck". The local model turns that from seconds
          into up to a couple of minutes, so it says which model it is waiting on — otherwise the
          only difference the user sees between the two options is that one of them looks broken. */}
      Writing a query and running it…
      {model === "Local" ? " The local model is slower." : null}
    </div>
  );
}

function TurnView({
  turn,
  onRate,
}: {
  turn: Turn;
  onRate: (id: number, helpful: boolean) => Promise<boolean>;
}) {
  return (
    <article className="space-y-3">
      <div className="flex justify-end">
        <p className="max-w-[85%] whitespace-pre-wrap rounded-lg rounded-br-sm bg-zinc-900 px-4 py-2.5 text-sm text-white dark:bg-zinc-100 dark:text-zinc-900">
          {turn.question}
        </p>
      </div>

      {turn.error ? (
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 dark:border-red-900 dark:bg-red-950/40"
        >
          <p className="text-sm font-semibold text-red-800 dark:text-red-300">
            {turn.error.title}
          </p>
          <p className="mt-1 text-sm leading-6 text-red-700 dark:text-red-400">
            {turn.error.detail}
          </p>
        </div>
      ) : null}

      {turn.answer ? <AnswerView answer={turn.answer} onRate={onRate} /> : null}
    </article>
  );
}

function AnswerView({
  answer,
  onRate,
}: {
  answer: Answer;
  onRate: (id: number, helpful: boolean) => Promise<boolean>;
}) {
  return (
    <div className="max-w-[85%] space-y-3 rounded-lg rounded-bl-sm border border-zinc-200 bg-white px-4 py-3 dark:border-zinc-800 dark:bg-zinc-950">
      <p className="whitespace-pre-wrap text-sm leading-6 text-zinc-800 dark:text-zinc-200">
        {answer.answerText}
      </p>

      <OutcomeNote outcome={answer.validationOutcome} />

      {answer.rows && answer.rows.length > 0 ? (
        <ResultTable rows={answer.rows} total={answer.resultRowCount} />
      ) : null}

      {answer.generatedSql ? (
        <QueryDetails
          sql={answer.generatedSql}
          parameters={answer.sqlParameters}
          explanation={answer.explanation}
        />
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <Rating assistantQueryId={answer.assistantQueryId} onRate={onRate} />
        <ModelNote choice={answer.modelChoice} name={answer.modelName} />
      </div>
    </div>
  );
}

/**
 * Says why an answer has no query behind it, when that is not obvious from the text.
 *
 * Approved answers get nothing — the SQL is right there. A refusal is worth naming, because
 * "I can only answer from the data this platform collects" reads the same whether the question was
 * off-topic or the validator turned the query away, and those are different facts about the system.
 */
function OutcomeNote({ outcome }: { outcome: AssistantValidationOutcome }) {
  if (outcome === "Approved" || outcome === "NotADataQuestion") {
    return null;
  }

  const reason =
    outcome === "RejectedNoSql"
      ? "No query could be written against the published views."
      : outcome === "RejectedUnreadableResponse"
        ? "The model's reply could not be read, so no query was attempted."
        : "The query that was written is not one the assistant is permitted to run.";

  return (
    <p className="rounded-md bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-300">
      {reason} Recorded as <code className="font-mono">{outcome}</code>.
    </p>
  );
}

function ResultTable({
  rows,
  total,
}: {
  rows: Record<string, unknown>[];
  total: number | null;
}) {
  // Column order follows the first row, which is the order the query selected them in.
  const columns = Object.keys(rows[0] ?? {});
  const shown = rows.slice(0, 50);

  return (
    <div className="rounded-md border border-zinc-200 dark:border-zinc-800">
      <Table caption="Rows returned by the generated query">
        <thead>
          <tr>
            {columns.map((column) => (
              <Th key={column}>{column}</Th>
            ))}
          </tr>
        </thead>
        <tbody>
          {shown.map((row, index) => (
            <Tr key={index}>
              {columns.map((column) => (
                <Cell key={column} value={row[column]} />
              ))}
            </Tr>
          ))}
        </tbody>
      </Table>
      {total !== null && total > shown.length ? (
        <p className="border-t border-zinc-200 px-4 py-2 text-xs text-zinc-500 dark:border-zinc-800 dark:text-zinc-400">
          Showing {shown.length} of {total} rows.
        </p>
      ) : null}
    </div>
  );
}

/**
 * One cell, formatted by what the value actually is.
 *
 * Values arrive untyped — the columns depend on whatever the model selected — so numbers are
 * right-aligned and tabular by detection rather than by a column definition that does not exist.
 */
function Cell({ value }: { value: unknown }) {
  if (value === null || value === undefined) {
    return <Td className="text-zinc-400 dark:text-zinc-600">—</Td>;
  }

  if (typeof value === "number") {
    return <Td numeric>{value.toLocaleString("en-US")}</Td>;
  }

  if (typeof value === "boolean") {
    return <Td>{value ? "Yes" : "No"}</Td>;
  }

  const text = String(value);

  // Trim the time off a midnight timestamp. Every reference date the API returns is a whole day,
  // and "2025-06-01T00:00:00.0000000Z" in a cell is noise standing where a date should be.
  const midnightUtc = /^(\d{4}-\d{2}-\d{2})T00:00:00(\.0+)?Z$/.exec(text);

  return <Td>{midnightUtc ? midnightUtc[1] : text}</Td>;
}

/** The query, folded away. Available on every answer, in the way of none of them. */
function QueryDetails({
  sql,
  parameters,
  explanation,
}: {
  sql: string;
  parameters: Record<string, unknown> | null;
  explanation: string | null;
}) {
  const bound = Object.entries(parameters ?? {});

  return (
    <details className="group">
      <summary className="cursor-pointer list-none text-xs font-medium text-zinc-500 hover:text-zinc-700 dark:text-zinc-400 dark:hover:text-zinc-200">
        <span className="group-open:hidden">Show the query</span>
        <span className="hidden group-open:inline">Hide the query</span>
      </summary>

      <div className="mt-2 space-y-2">
        {explanation ? (
          <p className="text-xs leading-5 text-zinc-600 dark:text-zinc-400">
            {explanation}
          </p>
        ) : null}

        <pre className="overflow-x-auto rounded-md bg-zinc-50 p-3 font-mono text-xs leading-5 text-zinc-800 dark:bg-zinc-900 dark:text-zinc-200">
          {sql}
        </pre>

        {bound.length > 0 ? (
          <dl className="space-y-1 text-xs">
            {/* Shown separately because they were never in the statement: values are bound, so
                one containing SQL is data and is never parsed. */}
            {bound.map(([name, value]) => (
              <div key={name} className="flex gap-2">
                <dt className="font-mono text-zinc-500 dark:text-zinc-400">
                  {name}
                </dt>
                <dd className="font-mono text-zinc-700 dark:text-zinc-300">
                  {value === null ? "null" : String(value)}
                </dd>
              </div>
            ))}
          </dl>
        ) : null}
      </div>
    </details>
  );
}

function Rating({
  assistantQueryId,
  onRate,
}: {
  assistantQueryId: number;
  onRate: (id: number, helpful: boolean) => Promise<boolean>;
}) {
  const [state, setState] = useState<"idle" | "saving" | "saved" | "failed">(
    "idle",
  );

  if (state === "saved") {
    return (
      <p className="text-xs text-zinc-500 dark:text-zinc-400">
        Thanks — recorded.
      </p>
    );
  }

  return (
    <div className="flex items-center gap-2">
      {(
        [
          ["Helpful", true],
          ["Not helpful", false],
        ] as const
      ).map(([label, helpful]) => (
        <button
          key={label}
          type="button"
          disabled={state === "saving"}
          onClick={async () => {
            setState("saving");
            setState((await onRate(assistantQueryId, helpful)) ? "saved" : "failed");
          }}
          className="rounded border border-zinc-200 px-2 py-1 text-xs text-zinc-600 transition-colors hover:border-zinc-300 hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:text-zinc-400 dark:hover:border-zinc-700 dark:hover:bg-zinc-900"
        >
          {label}
        </button>
      ))}
      {state === "failed" ? (
        <span className="text-xs text-zinc-500 dark:text-zinc-400">
          Could not save that.
        </span>
      ) : null}
    </div>
  );
}
