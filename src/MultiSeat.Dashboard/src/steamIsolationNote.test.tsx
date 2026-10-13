import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { SteamIsolationStatus } from "./api/types";
import { SteamIsolationNote } from "./components/SteamIsolationNote";

const status = (over: Partial<SteamIsolationStatus> = {}): SteamIsolationStatus => ({
  enabled: true,
  ipcName: "ms-seat01-1a2b3c4d",
  folder: "C:\MultiSeatGames\Steam-Seat01",
  folderPresent: true,
  ...over,
});

afterEach(cleanup);

describe("SteamIsolationNote", () => {
  it("shows nothing while the feature is off, so a default install looks unchanged", () => {
    for (const steam of [null, undefined, status({ enabled: false })]) {
      const { container } = render(<SteamIsolationNote steam={steam} />);
      expect(container.firstChild).toBeNull();
      cleanup();
    }
  });

  it("shows the IPC name and that the folder is ready", () => {
    render(<SteamIsolationNote steam={status()} />);
    expect(screen.getByText("ms-seat01-1a2b3c4d")).toBeTruthy();
    expect(screen.getByText(/folder ready/)).toBeTruthy();
  });

  it("says the folder is not built yet when it is missing", () => {
    render(<SteamIsolationNote steam={status({ folderPresent: false })} />);
    expect(screen.getByText(/folder not built yet/)).toBeTruthy();
    expect(screen.queryByText(/folder ready/)).toBeNull();
  });

  it("states the requirements and the untested part in its tooltip", () => {
    const { container } = render(<SteamIsolationNote steam={status()} />);
    const title = (container.firstChild as HTMLElement).getAttribute("title") ?? "";
    expect(title).toContain("own Steam account");
    expect(title).toContain("1.4 GB");
    expect(title).toContain("not verified");
    expect(title).toContain("C:\MultiSeatGames\Steam-Seat01");
  });
});
