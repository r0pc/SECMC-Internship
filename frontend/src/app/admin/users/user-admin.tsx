"use client";

import { useActionState, useState, useTransition } from "react";

import { Card, CardBody, CardHeader } from "@/components/card";
import { Table, Td, Th, Tr } from "@/components/table";
import { formatTimestamp } from "@/lib/format";
import type { PlatformRole, UserDto } from "@/types/api";

import {
  createUserAction,
  resetPasswordAction,
  setActiveAction,
  setRolesAction,
  type AdminActionState,
} from "./actions";

const ROLES: readonly PlatformRole[] = ["Administrator", "Analyst", "Viewer"];

const FIELD_CLASS =
  "w-full rounded-md border border-zinc-300 bg-white px-3 py-2 text-sm text-zinc-900 outline-none focus:border-zinc-500 dark:border-zinc-700 dark:bg-zinc-950 dark:text-zinc-100";

const BUTTON_CLASS =
  "rounded-md bg-zinc-900 px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-zinc-700 disabled:cursor-not-allowed disabled:opacity-60 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300";

/**
 * The account list and the things an administrator can do to it (FR-9).
 *
 * A Client Component because every control here is a form with its own pending and error state,
 * and `useActionState` is what keeps a failure beside the control that caused it. The work itself
 * happens in Server Functions, which re-check the caller's role before doing anything — see
 * `actions.ts`.
 */
export function UserAdmin({
  users,
  currentUserId,
}: {
  users: UserDto[];
  currentUserId: number;
}) {
  const [outcome, setOutcome] = useState<AdminActionState>({});
  const [pending, startTransition] = useTransition();

  function apply(call: () => Promise<AdminActionState>) {
    startTransition(async () => setOutcome(await call()));
  }

  return (
    <div className="space-y-6">
      <Notice state={outcome} />

      <Card>
        <CardHeader
          title="Accounts"
          hint={
            "Deactivating is how an account is retired: the questions someone asked are audit " +
            "records tied to their account, so the row cannot be deleted without deleting them too."
          }
        />
        <Table caption="Every account on this platform">
          <thead>
            <tr>
              <Th>Name</Th>
              <Th>Email</Th>
              <Th>Role</Th>
              <Th>Last sign-in</Th>
              <Th>Status</Th>
              <Th>Actions</Th>
            </tr>
          </thead>
          <tbody>
            {users.map((user) => {
              const isSelf = user.userId === currentUserId;

              return (
                <Tr key={user.userId}>
                  <Td>
                    {user.displayName}
                    {isSelf ? (
                      <span className="ml-2 text-xs text-zinc-500 dark:text-zinc-400">
                        you
                      </span>
                    ) : null}
                  </Td>
                  <Td>{user.email}</Td>
                  <Td>
                    <select
                      aria-label={`Role for ${user.email}`}
                      className={FIELD_CLASS}
                      defaultValue={user.roles[0] ?? "Viewer"}
                      // Refused by the API too, and disabled here so the refusal is not the first
                      // an administrator hears of it: demoting yourself takes effect on your very
                      // next request, and there is no undo from the outside.
                      disabled={isSelf || pending || !user.isActive}
                      onChange={(event) =>
                        apply(() =>
                          setRolesAction(user.userId, [
                            event.target.value as PlatformRole,
                          ]),
                        )
                      }
                    >
                      {ROLES.map((role) => (
                        <option key={role} value={role}>
                          {role}
                        </option>
                      ))}
                    </select>
                  </Td>
                  <Td>
                    {user.lastLoginAtPkt ? (
                      formatTimestamp(user.lastLoginAtPkt)
                    ) : (
                      <span className="text-zinc-500 dark:text-zinc-400">
                        Never
                      </span>
                    )}
                  </Td>
                  <Td>
                    {user.isActive ? (
                      <span className="text-emerald-700 dark:text-emerald-400">
                        Active
                      </span>
                    ) : (
                      <span className="text-zinc-500 dark:text-zinc-400">
                        Deactivated
                      </span>
                    )}
                  </Td>
                  <Td>
                    <div className="flex flex-wrap gap-3">
                      <button
                        type="button"
                        disabled={isSelf || pending}
                        className="text-sm font-medium text-zinc-700 underline underline-offset-2 hover:text-zinc-900 disabled:cursor-not-allowed disabled:opacity-50 dark:text-zinc-300 dark:hover:text-zinc-100"
                        onClick={() =>
                          apply(() =>
                            setActiveAction(user.userId, !user.isActive),
                          )
                        }
                      >
                        {user.isActive ? "Deactivate" : "Reactivate"}
                      </button>

                      <ResetPassword userId={user.userId} email={user.email} />
                    </div>
                  </Td>
                </Tr>
              );
            })}
          </tbody>
        </Table>
      </Card>

      <NewUser />
    </div>
  );
}

/** The result of the last change, success or refusal. */
function Notice({ state }: { state: AdminActionState }) {
  if (!state.error && !state.message) {
    return null;
  }

  return (
    <p
      role="status"
      className={
        state.error
          ? "rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300"
          : "rounded-md border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-300"
      }
    >
      {state.error ?? state.message}
    </p>
  );
}

/**
 * Setting someone else's password.
 *
 * There is no email-based reset flow, because this platform sends no mail. A forgotten password is
 * an administrator setting a new one and telling the person out of band — which the confirmation
 * message says, so nobody sits waiting for an email that is not coming.
 */
function ResetPassword({ userId, email }: { userId: number; email: string }) {
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState<AdminActionState, FormData>(
    resetPasswordAction,
    {},
  );

  if (!open) {
    return (
      <button
        type="button"
        className="text-sm font-medium text-zinc-700 underline underline-offset-2 hover:text-zinc-900 dark:text-zinc-300 dark:hover:text-zinc-100"
        onClick={() => setOpen(true)}
      >
        Set password
      </button>
    );
  }

  return (
    <form action={action} className="w-full space-y-2">
      <input type="hidden" name="userId" value={userId} />
      <input
        name="newPassword"
        type="password"
        required
        minLength={12}
        autoComplete="new-password"
        aria-label={`New password for ${email}`}
        placeholder="At least 12 characters"
        className={FIELD_CLASS}
      />
      <div className="flex gap-2">
        <button type="submit" disabled={pending} className={BUTTON_CLASS}>
          {pending ? "Setting…" : "Set"}
        </button>
        <button
          type="button"
          className="rounded-md px-3 py-2 text-sm text-zinc-600 hover:text-zinc-900 dark:text-zinc-400 dark:hover:text-zinc-100"
          onClick={() => setOpen(false)}
        >
          Cancel
        </button>
      </div>
      {state.error || state.message ? <Notice state={state} /> : null}
    </form>
  );
}

/** Creating an account. The only way one comes into existence — there is no self-registration. */
function NewUser() {
  const [state, action, pending] = useActionState<AdminActionState, FormData>(
    createUserAction,
    {},
  );

  return (
    <Card>
      <CardHeader
        title="Add someone"
        hint="They will need the password from you directly; nothing is emailed."
      />
      <CardBody>
        <form action={action} className="space-y-4">
          <Notice state={state} />

          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <label
                htmlFor="new-email"
                className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
              >
                Email
              </label>
              <input
                id="new-email"
                name="email"
                type="email"
                required
                className={`mt-1.5 ${FIELD_CLASS}`}
              />
            </div>

            <div>
              <label
                htmlFor="new-name"
                className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
              >
                Display name
              </label>
              <input
                id="new-name"
                name="displayName"
                type="text"
                required
                className={`mt-1.5 ${FIELD_CLASS}`}
              />
            </div>

            <div>
              <label
                htmlFor="new-password"
                className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
              >
                Initial password
              </label>
              <input
                id="new-password"
                name="password"
                type="password"
                required
                minLength={12}
                autoComplete="new-password"
                placeholder="At least 12 characters"
                className={`mt-1.5 ${FIELD_CLASS}`}
              />
            </div>

            <div>
              <label
                htmlFor="new-role"
                className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
              >
                Role
              </label>
              <select
                id="new-role"
                name="roles"
                defaultValue="Viewer"
                className={`mt-1.5 ${FIELD_CLASS}`}
              >
                {ROLES.map((role) => (
                  <option key={role} value={role}>
                    {role}
                  </option>
                ))}
              </select>
              <p className="mt-1.5 text-xs text-zinc-500 dark:text-zinc-400">
                Viewer reads dashboards. Analyst adds the AI assistant.
                Administrator adds user and source management.
              </p>
            </div>
          </div>

          <button type="submit" disabled={pending} className={BUTTON_CLASS}>
            {pending ? "Creating…" : "Create account"}
          </button>
        </form>
      </CardBody>
    </Card>
  );
}

