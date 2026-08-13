"use client";

import { useEffect, useRef, useState, useTransition } from "react";

import { ask, listChats, resumeChat } from "@/app/assistant/actions";
import { formatTimestamp } from "@/lib/format";
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
 * A Client Component because a conversation is state: the transcript and what is in flight live
 * here. The calls it makes are Server Functions, so the browser still never talks to the API
 * directly.
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

  // Two flags rather than one, because the sidebar is two different things at two widths and they
  // want opposite defaults. On a wide screen it is a column that is there until someone collapses
  // it; on a phone it is a drawer over the conversation that must start shut, since a chat list
  // covering the chat is not a useful first screen. One shared boolean would have to be wrong at
  // one of the two sizes.
  //
  // Neither is persisted. The theme toggle earns its localStorage entry by being a choice about
  // every page and every visit; this is a choice about the width of one panel, and remembering it
  // would mean an inline script in <head> to avoid the layout jumping on first paint — the same
  // machinery the theme needs, for a great deal less.
  const [expanded, setExpanded] = useState(true);
  const [drawerOpen, setDrawerOpen] = useState(false);

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

    // On a phone the drawer is covering the conversation the user just asked for.
    setDrawerOpen(false);
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

    setDrawerOpen(false);
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

  const sidebar = {
    chats,
    currentSessionId: sessionId,
    disabled: pending,
    onResume: resume,
    onNew: startNewChat,
  };

  return (
    <div className="flex gap-6">
      {/* The column, on anything wider than a phone. Sticky and independently scrollable, so a
          long history scrolls inside the panel rather than dragging the conversation with it. */}
      {expanded ? (
        <aside
          id="assistant-chat-list"
          className="hidden shrink-0 md:sticky md:top-20 md:block md:h-[calc(100vh-9rem)] md:w-72"
        >
          <ChatSidebar {...sidebar} onDismiss={() => setExpanded(false)} dismissLabel="Hide chats" />
        </aside>
      ) : null}

      {/* The same panel as a drawer, on a phone. Rendered only while open — an off-screen copy
          would put a second set of the same buttons in the tab order, so a keyboard user would
          tab through a chat list they cannot see. */}
      {drawerOpen ? (
        <ChatDrawer onClose={() => setDrawerOpen(false)}>
          <ChatSidebar
            {...sidebar}
            onDismiss={() => setDrawerOpen(false)}
            dismissLabel="Close chat list"
          />
        </ChatDrawer>
      ) : null}

      <div className="flex min-w-0 flex-1 flex-col gap-4">
        <ChatListToggle
          count={chats.length}
          expanded={expanded}
          onToggleColumn={() => setExpanded((open) => !open)}
          onOpenDrawer={() => setDrawerOpen(true)}
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
            turns.map((turn) => <TurnView key={turn.key} turn={turn} />)
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
    </div>
  );
}

/**
 * Turns a stored turn back into the shape a live answer has, so one renderer draws both.
 *
 * A replayed turn has no result rows: they were never stored, because a turn can return up to
 * 2,000 of them. That costs nothing now — an answer is prose, and no part of this view reads the
 * rows — so the row fields are carried across as the transcript has them and left at that.
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
 * The chat list: start a new conversation, or reopen one.
 *
 * One component for both placements — the column beside the conversation and the drawer over it —
 * because it is the same panel at two widths, and a second copy would be two things to keep in
 * agreement. Only the dismiss control differs, which is why the caller names it: "Hide chats"
 * collapses a column that is still there to be brought back, while "Close chat list" shuts an
 * overlay, and a button that says the wrong one of those is a button people stop trusting.
 */
function ChatSidebar({
  chats,
  currentSessionId,
  disabled,
  onResume,
  onNew,
  onDismiss,
  dismissLabel,
}: {
  chats: AssistantSessionSummaryDto[];
  currentSessionId: string | null;
  disabled: boolean;
  onResume: (sessionId: string) => void;
  onNew: () => void;
  onDismiss: () => void;
  dismissLabel: string;
}) {
  return (
    <div className="flex h-full flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-xs font-semibold uppercase tracking-wide text-zinc-500 dark:text-zinc-400">
          Chats
          <span className="ml-1.5 font-normal text-zinc-400 dark:text-zinc-600">
            {chats.length === 0 ? "—" : chats.length}
          </span>
        </h2>

        <button
          type="button"
          onClick={onDismiss}
          aria-label={dismissLabel}
          title={dismissLabel}
          className="rounded-md p-1.5 text-zinc-500 transition-colors hover:bg-zinc-100 hover:text-zinc-800 dark:text-zinc-400 dark:hover:bg-zinc-900 dark:hover:text-zinc-200"
        >
          <PanelIcon />
        </button>
      </div>

      {/* Above the list rather than below it: it is the one thing here that is always available,
          including when there is no history for the list to hold. */}
      <button
        type="button"
        onClick={onNew}
        disabled={disabled}
        className="flex items-center gap-2 rounded-md border border-zinc-300 px-3 py-2 text-xs font-medium text-zinc-700 transition-colors hover:border-zinc-400 hover:bg-zinc-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-900"
      >
        <span aria-hidden className="text-sm leading-none">
          +
        </span>
        New chat
      </button>

      {chats.length === 0 ? (
        <p className="text-xs leading-5 text-zinc-500 dark:text-zinc-400">
          Nothing yet. Conversations appear here once they have an answer, and
          stay after you close the page.
        </p>
      ) : (
        // min-h-0 is what lets this scroll inside a flex column: without it the list sets the
        // panel's height instead of fitting within it, and a long history runs off the screen.
        <ul className="-mr-1 min-h-0 flex-1 space-y-0.5 overflow-y-auto pr-1">
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
                {/* Token cost is deliberately not shown. It is still recorded on every turn
                    and still readable in the audit log, which is where a question about what
                    the assistant costs belongs — a running total beside someone's own chat
                    history reads as a budget they are being measured against, and none of
                    them can act on it. */}
                <span className="mt-0.5 block text-[11px] text-zinc-500 dark:text-zinc-400">
                  {chat.turnCount}
                  {chat.turnCount === 1 ? " question" : " questions"} ·{" "}
                  {formatTimestamp(chat.lastActivityAtPkt)}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * The chat list as an overlay, for widths with no room to put it beside the conversation.
 *
 * Escape closes it and the backdrop is a real button, so the two ways every overlay is expected to
 * be dismissed both work. Focus is not trapped: what is behind it is the conversation this panel
 * came from, and tabbing into it is a reasonable thing to have happen — trapping would be more
 * machinery than a list of links needs.
 */
function ChatDrawer({
  onClose,
  children,
}: {
  onClose: () => void;
  children: React.ReactNode;
}) {
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        onClose();
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Previous chats"
      className="fixed inset-0 z-40 md:hidden"
    >
      <button
        type="button"
        aria-label="Close chat list"
        onClick={onClose}
        className="absolute inset-0 bg-zinc-950/40"
      />

      <div className="absolute inset-y-0 left-0 flex w-80 max-w-[85%] flex-col border-r border-zinc-200 bg-white p-4 shadow-xl dark:border-zinc-800 dark:bg-zinc-950">
        {children}
      </div>
    </div>
  );
}

/**
 * Brings the chat list back.
 *
 * Two buttons rather than one, swapped by width: on a phone the list is an overlay that is opened,
 * on a wider screen a column that is shown, and `aria-expanded` is only true of the second. One
 * button carrying both meanings would have to describe itself wrongly at one of the two sizes.
 *
 * The column's button stays visible while the column is open, as the way to collapse it — the
 * matching control inside the panel does the same thing, and losing the toggle the moment it worked
 * is how a panel becomes something people are reluctant to close.
 */
function ChatListToggle({
  count,
  expanded,
  onToggleColumn,
  onOpenDrawer,
}: {
  count: number;
  expanded: boolean;
  onToggleColumn: () => void;
  onOpenDrawer: () => void;
}) {
  const label = `Previous chats${count === 0 ? "" : ` (${count})`}`;

  const className =
    "inline-flex items-center gap-2 rounded-md border border-zinc-300 px-3 py-1.5 text-xs text-zinc-700 transition-colors hover:border-zinc-400 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-900";

  return (
    <div className="flex items-center">
      <button
        type="button"
        onClick={onOpenDrawer}
        className={`${className} md:hidden`}
      >
        <PanelIcon />
        {label}
      </button>

      <button
        type="button"
        onClick={onToggleColumn}
        aria-expanded={expanded}
        // Only while the panel is actually in the document. A collapsed column is not rendered at
        // all, and aria-controls naming an id that is not there is a dangling reference rather
        // than a helpful one.
        aria-controls={expanded ? "assistant-chat-list" : undefined}
        className={`hidden ${className} md:inline-flex`}
      >
        <PanelIcon />
        {expanded ? "Hide chats" : label}
      </button>
    </div>
  );
}

/** A panel beside a page, in the shape every sidebar toggle is already drawn as. */
function PanelIcon() {
  return (
    <svg
      aria-hidden
      viewBox="0 0 16 16"
      className="size-3.5"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
    >
      <rect x="1.5" y="2.5" width="13" height="11" rx="2" />
      <line x1="6" y1="2.5" x2="6" y2="13.5" />
    </svg>
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

function TurnView({ turn }: { turn: Turn }) {
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

      {turn.answer ? <AnswerView answer={turn.answer} /> : null}
    </article>
  );
}

function AnswerView({ answer }: { answer: Answer }) {
  return (
    <div className="max-w-[85%] space-y-3 rounded-lg rounded-bl-sm border border-zinc-200 bg-white px-4 py-3 dark:border-zinc-800 dark:bg-zinc-950">
      <p className="whitespace-pre-wrap text-sm leading-6 text-zinc-800 dark:text-zinc-200">
        {answer.answerText}
      </p>

      <OutcomeNote outcome={answer.validationOutcome} />

      {answer.generatedSql ? (
        <QueryDetails
          sql={answer.generatedSql}
          parameters={answer.sqlParameters}
          explanation={answer.explanation}
        />
      ) : null}

      <ModelNote choice={answer.modelChoice} name={answer.modelName} />
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
