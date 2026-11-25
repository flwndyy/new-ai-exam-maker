import Link from "next/link";

import { navItemsFor, type NavItem, type NavKey } from "@/components/shell/nav-items";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type Role = components["schemas"]["Role"];
type ShortcutItem = NavItem & { key: Exclude<NavKey, "home"> };

function isShortcut(item: NavItem): item is ShortcutItem {
  return item.key !== "home";
}

/** Links to the pages of the role's menu, except home. Separated by lines, not cards. */
export function Shortcuts({ role }: { role: Role }) {
  const items = navItemsFor(role).filter(isShortcut);
  return (
    <ul className="grid grid-cols-1 border-t border-line md:grid-cols-2 md:gap-x-10">
      {items.map((item) => (
        <li key={item.key} className="border-b border-line">
          <Link href={item.href} className="group block rounded-md px-1 py-3.5">
            <span className="block text-[15px] font-semibold text-accent-strong group-hover:underline">
              {item.label}
            </span>
            <span className="mt-0.5 block text-[13.5px] text-muted">{vi.home.shortcutDescriptions[item.key]}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
