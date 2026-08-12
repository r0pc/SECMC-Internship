"use server";

/**
 * User administration, as Server Functions (FR-9).
 *
 * Every one of these re-checks the caller's role with `requireRole` before it does anything. The
 * page already does, and the API does again — that is not redundancy to trim. A Server Function is
 * a public endpoint with a generated name: it can be invoked directly, and reaching it does not
 * mean anyone rendered the page that normally offers it. The Next.js authentication guide is
 * explicit about this, and it is the reason the checks look repetitive.
 *
 * They return an outcome rather than throwing. Every failure here is one an administrator can act
 * on — an address already taken, the last administrator, a password too short — and belongs beside
 * the form rather than on an error page.
 */

import { revalidatePath } from "next/cache";

import {
  ApiError,
  createUser,
  resetUserPassword,
  updateUser,
} from "@/lib/api";
import { requireRole } from "@/lib/session";
import type { PlatformRole } from "@/types/api";

export interface AdminActionState {
  readonly error?: string;
  readonly message?: string;
}

/** The floor the API enforces, restated so the form can refuse before a round trip. */
const MIN_PASSWORD_LENGTH = 12;

const USERS_PATH = "/admin/users";

export async function createUserAction(
  _state: AdminActionState,
  formData: FormData,
): Promise<AdminActionState> {
  await requireRole("Administrator");

  const email = String(formData.get("email") ?? "").trim();
  const displayName = String(formData.get("displayName") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  const roles = formData.getAll("roles").map(String) as PlatformRole[];

  if (email.length === 0 || displayName.length === 0) {
    return { error: "An email and a display name are both required." };
  }

  if (password.length < MIN_PASSWORD_LENGTH) {
    return {
      error: `The password must be at least ${MIN_PASSWORD_LENGTH} characters.`,
    };
  }

  return run(async () => {
    const user = await createUser({ email, displayName, password, roles });

    return {
      message:
        `Created ${user.email} as ${user.roles.join(", ")}. Give them that ` +
        "password directly, and ask them to change it once they are in.",
    };
  });
}

export async function setRolesAction(
  userId: number,
  roles: PlatformRole[],
): Promise<AdminActionState> {
  await requireRole("Administrator");

  return run(async () => {
    const user = await updateUser(userId, { roles });

    return {
      message: `${user.email} is now ${user.roles.join(", ")}. Any session they had open has ended.`,
    };
  });
}

export async function setActiveAction(
  userId: number,
  isActive: boolean,
): Promise<AdminActionState> {
  await requireRole("Administrator");

  return run(async () => {
    const user = await updateUser(userId, { isActive });

    return {
      message: isActive
        ? `${user.email} can sign in again.`
        : `${user.email} is deactivated, and any session they had open has ended.`,
    };
  });
}

export async function resetPasswordAction(
  _state: AdminActionState,
  formData: FormData,
): Promise<AdminActionState> {
  await requireRole("Administrator");

  const userId = Number(formData.get("userId"));
  const newPassword = String(formData.get("newPassword") ?? "");

  if (!Number.isFinite(userId)) {
    return { error: "That user could not be identified." };
  }

  if (newPassword.length < MIN_PASSWORD_LENGTH) {
    return {
      error: `The password must be at least ${MIN_PASSWORD_LENGTH} characters.`,
    };
  }

  return run(async () => {
    await resetUserPassword(userId, newPassword);

    return {
      message:
        "Password set. Tell them out of band — this platform sends no mail — " +
        "and their open sessions have ended.",
    };
  });
}

/**
 * Runs one administrative call, turning the API's refusal into a message and refreshing the list.
 *
 * The API's `detail` is shown verbatim. It is the half of the answer worth reading — "this is the
 * last active administrator", "an account already exists for that address" — and a generic
 * "something went wrong" would throw away the only part that says what to do next.
 */
async function run(
  call: () => Promise<AdminActionState>,
): Promise<AdminActionState> {
  try {
    const outcome = await call();

    // The page reads the user list on every render, so this is what puts the change on screen
    // without a manual refresh.
    revalidatePath(USERS_PATH);

    return outcome;
  } catch (error) {
    if (error instanceof ApiError) {
      return {
        error: error.isUnreachable
          ? "The platform is not reachable right now. Nothing was changed."
          : (error.problem.detail ?? "That change was not accepted."),
      };
    }

    throw error;
  }
}
