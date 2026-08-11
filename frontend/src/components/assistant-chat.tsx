"use client";

import { useEffect, useRef, useState, useTransition } from "react";

import { ask, listChats, rate, resumeChat } from "@/app/assistant/actions";
import { Table, Td, Th, Tr } from "@/components/table";
import { formatCount, formatTimestamp } from "@/lib/format";
import type {
  AssistantAnswerDto,
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

interface Turn {
  /** Local id — the API's id only exists once an answer comes back, and errors never get one. */
  key: number;
  question: string;
  answer?: AssistantAnswerDto;
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

    if (trimmed.length === 0 || pending) {
      return;
    }

    const key = nextKey.current++;

    setTurns((current) => [...current, { key, question: trimmed }]);
    setDraft("");

    startTransition(async () => {
      const result = await ask(trimmed, sessionId);

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

        {pending ? <Thinking /> : null}

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
          Ask a question about the collected data
        </label>
        <textarea
          id="assistant-question"
          ref={input}
          rows={2}
          value={draft}
          disabled={pending}
          maxLength={2000}
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
        <div className="flex items-center justify-between gap-3 px-3 pb-1">
          <p className="text-xs text-zinc-500 dark:text-zinc-400">
            Answers come from collected data only. Every question is logged.
          </p>
          <button
            type="submit"
            disabled={pending || draft.trim().length === 0}
            className="rounded-md bg-zinc-900 px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-zinc-700 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300"
          >
            {pending ? "Asking…" : "Ask"}
          </button>
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

function Thinking() {
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
          the difference between "working" and "stuck". */}
      Writing a query and running it…
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
  answer: AssistantAnswerDto;
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

      <Rating assistantQueryId={answer.assistantQueryId} onRate={onRate} />
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
