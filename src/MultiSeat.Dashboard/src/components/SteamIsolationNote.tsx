import type { SteamIsolationStatus } from "../api/types";

/**
 * One line on a seat card about the seat's own Steam (MultiSeat:SteamIsolation).
 * Renders nothing while the feature is off, which is when the service sends null,
 * so a default install looks exactly as it did.
 * Requirements and the untested parts are in docs/steam-isolation.md.
 */
export function SteamIsolationNote({ steam }: { steam: SteamIsolationStatus | null | undefined }) {
  if (!steam || !steam.enabled) return null;
  return (
    <div
      className="text-muted"
      style={{ fontSize: 12, marginTop: 8 }}
      title={
        "Own Steam for this seat. Needs its own Steam account and about 1.4 GB of disk. " +
        "Games and helper processes are not verified yet. Folder: " + steam.folder
      }
    >
      Own Steam: {steam.folderPresent ? "folder ready" : "folder not built yet"} · IPC name{" "}
      <code>{steam.ipcName}</code>
    </div>
  );
}
