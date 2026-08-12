import type { Metadata } from "next";

import { PageHeader } from "@/components/card";
import { ApiErrorPanel } from "@/components/states";
import { attempt, getUsers } from "@/lib/api";
import { requireRole } from "@/lib/session";

import { UserAdmin } from "./user-admin";

/**
 * User administration (FR-9).
 *
 * The role is checked here, in the page, rather than in a layout: a layout does not re-render on
 * client-side navigation, so a check there would pass once and then never run again. It is checked
 * again in every Server Function this page offers, and again by the API on every call — because
 * this check only decides what to render, and a Server Function can be invoked without anyone
 * having rendered anything.
 */
export const metadata: Metadata = {
  title: "Users",
};

export default async function UsersPage() {
  const session = await requireRole("Administrator");

  const users = await attempt(getUsers());

  return (
    <div className="space-y-6">
      <PageHeader
        title="Users"
        description={
          "Everyone who can sign in, and what they may reach. Accounts are created here — there " +
          "is no self-registration, so reaching the login page entitles a visitor to nothing."
        }
      />

      {users.ok ? (
        <UserAdmin users={users.data} currentUserId={session.user.userId} />
      ) : (
        <ApiErrorPanel error={users.error} label="The user list" />
      )}
    </div>
  );
}
