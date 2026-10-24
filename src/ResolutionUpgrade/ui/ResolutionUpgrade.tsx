import React from "react";
import { extensionFetch } from "@cove/runtime/api";
import {
  AlertTriangle, ArrowUpCircle, CheckCircle2, Download, Loader2, RefreshCw, Search, Trash2, X,
} from "@cove/runtime/lucide-react";

const { useCallback, useEffect, useMemo, useRef, useState } = React;

const API_BASE = "/api/ext/resolution-upgrade";
const OPEN_EVENT = "cove-resolution-upgrade:open";
const CHANGED_EVENT = "cove-resolution-upgrade:changed";
const POLL_MS = 2000;

type UpgradeState =
  | "upgrade-available"
  | "up-to-date"
  | "no-source"
  | "check-failed"
  | "download-queued"
  | "downloading"
  | "download-failed"
  | "downloaded"
  | "replaced";

interface UpgradeRecord {
  videoId: number;
  state: UpgradeState;
  title?: string;
  message?: string;
  currentFileId?: number;
  currentWidth?: number;
  currentHeight?: number;
  url?: string;
  downloaderName?: string;
  availableWidth?: number;
  availableHeight?: number;
  replacementFileId?: number;
  replacementWidth?: number;
  replacementHeight?: number;
  checkedAt: string;
  updatedAt: string;
}

interface JobInfo {
  id: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  subTask?: string;
  summary?: string;
  error?: string;
}

interface StartedJob {
  jobId: string;
  videoIds: number[];
  description?: string;
}

type DialogMode = "check" | "upgrade";

interface OpenRequest {
  mode: DialogMode;
  videoIds: number[];
}

const STATE_LABELS: Record<UpgradeState, string> = {
  "upgrade-available": "Upgrade available",
  "up-to-date": "Up to date",
  "no-source": "No source",
  "check-failed": "Check failed",
  "download-queued": "Queued",
  downloading: "Downloading",
  "download-failed": "Download failed",
  downloaded: "Ready to make primary",
  replaced: "Replaced",
};

const STATE_STYLES: Record<UpgradeState, string> = {
  "upgrade-available": "border-accent/40 bg-accent/10 text-accent",
  "up-to-date": "border-border bg-card text-secondary",
  "no-source": "border-border bg-card text-muted",
  "check-failed": "border-rose-500/40 bg-rose-500/10 text-rose-300",
  "download-queued": "border-amber-500/40 bg-amber-500/10 text-amber-300",
  downloading: "border-amber-500/40 bg-amber-500/10 text-amber-300",
  "download-failed": "border-rose-500/40 bg-rose-500/10 text-rose-300",
  downloaded: "border-emerald-500/40 bg-emerald-500/10 text-emerald-300",
  replaced: "border-border bg-card text-secondary",
};

const IN_PROGRESS: UpgradeState[] = ["download-queued", "downloading"];
const DOWNLOADABLE: UpgradeState[] = ["upgrade-available", "download-failed"];
const JOB_FINISHED: JobInfo["status"][] = ["completed", "failed", "cancelled"];

// ── API helpers ─────────────────────────────────────────────────────────────

async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await extensionFetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...(init.headers ?? {}) },
  });
  const text = await response.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }
  if (!response.ok) {
    throw new Error(describeError(data) ?? `${response.status} ${response.statusText}`);
  }
  return data as T;
}

function describeError(data: unknown): string | null {
  if (typeof data === "string" && data.trim()) return data;
  if (data && typeof data === "object") {
    const body = data as Record<string, unknown>;
    for (const key of ["error", "detail", "title", "message"]) {
      const value = body[key];
      if (typeof value === "string" && value.trim()) return value;
    }
  }
  return null;
}

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function queryRecords(videoIds: number[]): Promise<UpgradeRecord[]> {
  return call<UpgradeRecord[]>(`${API_BASE}/results/query`, { method: "POST", body: JSON.stringify({ videoIds }) });
}

function formatResolution(width?: number, height?: number): string {
  if (!height) return "Unknown";
  return width ? `${height}p (${width}×${height})` : `${height}p`;
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

/** Make the downloaded file primary through Cove's own endpoint, optionally delete the old one, then record it. */
async function replaceVideo(record: UpgradeRecord, deleteOldFile: boolean): Promise<void> {
  await call(`/api/videos/${record.videoId}/alignments/apply`, {
    method: "POST",
    body: JSON.stringify({
      fileId: record.replacementFileId,
      resolution: "direct",
      expectedPrimaryFileId: record.currentFileId ?? null,
    }),
  });

  let deleteError: string | null = null;
  if (deleteOldFile && record.currentFileId) {
    try {
      await call("/api/files/delete", {
        method: "POST",
        body: JSON.stringify({ fileIds: [record.currentFileId], deleteFromDisk: true }),
      });
    } catch (error) {
      deleteError = errorText(error);
    }
  }

  // The primary file has already changed, so record that even when deleting the old file failed.
  await call(`${API_BASE}/replaced`, {
    method: "POST",
    body: JSON.stringify({ videoId: record.videoId, oldFileDeleted: deleteOldFile && !deleteError }),
  });

  if (deleteError) {
    throw new Error(`Made primary, but the lower-resolution file could not be deleted: ${deleteError}`);
  }
}

// ── Dialog opening (bulk actions and the results page) ──────────────────────

let mountedDialogs = 0;

function requestDialog(mode: DialogMode, videoIds: number[]) {
  if (mountedDialogs === 0) {
    throw new Error("The Resolution Upgrades dialog isn't loaded. Reload Cove and try again.");
  }
  window.dispatchEvent(new CustomEvent<OpenRequest>(OPEN_EVENT, { detail: { mode, videoIds } }));
}

function selectedVideoIds(payload: unknown): number[] {
  const body = (payload ?? {}) as { entityIds?: unknown; selectedIds?: unknown };
  const raw = Array.isArray(body.entityIds) ? body.entityIds : Array.isArray(body.selectedIds) ? body.selectedIds : [];
  return [...new Set(raw.filter((id): id is number => typeof id === "number" && Number.isInteger(id) && id > 0))];
}

function openResolutionCheck(_action: unknown, payload: unknown) {
  requestDialog("check", selectedVideoIds(payload));
  return { suppressToast: true };
}

function openResolutionUpgrade(_action: unknown, payload: unknown) {
  requestDialog("upgrade", selectedVideoIds(payload));
  return { suppressToast: true };
}

// ── Shared pieces ───────────────────────────────────────────────────────────

function StateBadge({ state, waiting }: { state: UpgradeState; waiting?: boolean }) {
  // During a batch, a video still ready to download simply has not been reached yet.
  if (waiting && state === "upgrade-available") {
    return (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-border bg-card px-2 py-0.5 text-xs text-secondary">
        Waiting
      </span>
    );
  }
  return (
    <span className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full border px-2 py-0.5 text-xs ${STATE_STYLES[state]}`}>
      {IN_PROGRESS.includes(state) && <Loader2 className="h-3 w-3 animate-spin" />}
      {state === "downloaded" && <CheckCircle2 className="h-3 w-3" />}
      {STATE_LABELS[state] ?? state}
    </span>
  );
}

function ProgressBar({ progress, label }: { progress: number; label?: string }) {
  const percent = Math.round(Math.min(1, Math.max(0, progress)) * 100);
  return (
    <div className="space-y-1.5">
      <div className="h-2 overflow-hidden rounded-full bg-border">
        <div className="h-2 rounded-full bg-accent transition-all" style={{ width: `${percent}%` }} />
      </div>
      <div className="flex justify-between gap-3 text-xs text-secondary">
        <span className="truncate">{label}</span>
        <span>{percent}%</span>
      </div>
    </div>
  );
}

function SelectAllCheckbox({ ids, picked, onChange }: { ids: number[]; picked: Set<number>; onChange: (next: Set<number>) => void }) {
  const allPicked = ids.length > 0 && ids.every((id) => picked.has(id));
  return (
    <input
      type="checkbox"
      checked={allPicked}
      aria-label="Select all"
      onChange={() => {
        const next = new Set(picked);
        for (const id of ids) {
          if (allPicked) next.delete(id);
          else next.add(id);
        }
        onChange(next);
      }}
    />
  );
}

function togglePick(picked: Set<number>, videoId: number): Set<number> {
  const next = new Set(picked);
  if (next.has(videoId)) next.delete(videoId);
  else next.add(videoId);
  return next;
}

// ── The guided dialog ───────────────────────────────────────────────────────

type Phase = "starting" | "checking" | "review" | "downloading" | "replace" | "replacing" | "done";

function ResolutionUpgradeDialog() {
  const [open, setOpen] = useState(false);
  const [phase, setPhase] = useState<Phase>("starting");
  const [videoIds, setVideoIds] = useState<number[]>([]);
  const [records, setRecords] = useState<Map<number, UpgradeRecord>>(() => new Map());
  const [job, setJob] = useState<JobInfo | null>(null);
  const [jobVideoIds, setJobVideoIds] = useState<number[]>([]);
  const [pickedForDownload, setPickedForDownload] = useState<Set<number>>(() => new Set());
  const [pickedForReplace, setPickedForReplace] = useState<Set<number>>(() => new Set());
  // Chosen before the run starts, so a long batch finishes on its own with nothing left waiting.
  const [makePrimary, setMakePrimary] = useState(true);
  const [deleteOldFiles, setDeleteOldFiles] = useState(false);
  const [replaceProgress, setReplaceProgress] = useState(0);
  const [replaced, setReplaced] = useState<number[]>([]);
  const [replaceErrors, setReplaceErrors] = useState<Record<number, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [showOthers, setShowOthers] = useState(false);
  const session = useRef(0);

  useEffect(() => {
    mountedDialogs += 1;
    return () => {
      mountedDialogs -= 1;
    };
  }, []);

  const loadRecords = useCallback(async (ids: number[]) => {
    const list = await queryRecords(ids);
    setRecords(new Map(list.map((record) => [record.videoId, record])));
    return list;
  }, []);

  const notifyChanged = () => window.dispatchEvent(new CustomEvent(CHANGED_EVENT));

  const enterReview = useCallback((list: UpgradeRecord[]) => {
    setPickedForDownload(new Set(list.filter((record) => record.state === "upgrade-available").map((record) => record.videoId)));
    setPhase("review");
    notifyChanged();
  }, []);

  const startCheck = useCallback(async (ids: number[], token: number) => {
    setPhase("starting");
    setError(null);
    try {
      const started = await call<StartedJob>(`${API_BASE}/check`, { method: "POST", body: JSON.stringify({ videoIds: ids }) });
      if (session.current !== token) return;
      setJobVideoIds(started.videoIds);
      setJob({ id: started.jobId, status: "pending", progress: 0 });
      setPhase("checking");
    } catch (reason) {
      if (session.current !== token) return;
      setError(errorText(reason));
      setPhase("done");
    }
  }, []);

  useEffect(() => {
    const onOpen = (event: Event) => {
      const { mode, videoIds: ids } = (event as CustomEvent<OpenRequest>).detail;
      const token = ++session.current;
      setOpen(true);
      setVideoIds(ids);
      setRecords(new Map());
      setJob(null);
      setJobVideoIds([]);
      setPickedForDownload(new Set());
      setPickedForReplace(new Set());
      setDeleteOldFiles(false);
      setReplaceProgress(0);
      setReplaced([]);
      setReplaceErrors({});
      setError(null);
      setShowOthers(false);

      if (ids.length === 0) {
        setError("Select at least one video first.");
        setPhase("done");
        return;
      }

      if (mode === "check") {
        void startCheck(ids, token);
        return;
      }

      setPhase("starting");
      loadRecords(ids).then(
        (list) => {
          if (session.current === token) enterReview(list);
        },
        (reason) => {
          if (session.current !== token) return;
          setError(errorText(reason));
          setPhase("done");
        },
      );
    };
    window.addEventListener(OPEN_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_EVENT, onOpen);
  }, [enterReview, loadRecords, startCheck]);

  // Poll the running job and the records it is updating.
  const jobId = job?.id;
  useEffect(() => {
    if (!open || !jobId || (phase !== "checking" && phase !== "downloading")) return;
    const token = session.current;
    let stopped = false;
    let timer = 0;

    const tick = async () => {
      try {
        const [latest, list] = await Promise.all([call<JobInfo>(`/api/jobs/${jobId}`), loadRecords(videoIds)]);
        if (stopped || session.current !== token) return;
        setJob(latest);

        if (JOB_FINISHED.includes(latest.status)) {
          if (latest.status === "failed") setError(latest.error ?? "The job failed.");
          if (phase === "checking") {
            enterReview(list);
          } else {
            const ready = list.filter((record) => jobVideoIds.includes(record.videoId) && record.state === "downloaded");
            setPickedForReplace(new Set(ready.map((record) => record.videoId)));
            setPhase(ready.length > 0 ? "replace" : "done");
            notifyChanged();
          }
          return;
        }
      } catch (reason) {
        if (stopped || session.current !== token) return;
        setError(`Lost track of the job: ${errorText(reason)}`);
        return;
      }
      timer = window.setTimeout(tick, POLL_MS);
    };

    timer = window.setTimeout(tick, 500);
    return () => {
      stopped = true;
      window.clearTimeout(timer);
    };
  }, [open, jobId, phase, videoIds, jobVideoIds, loadRecords, enterReview]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape" && phase !== "replacing") close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const close = () => {
    session.current += 1;
    setOpen(false);
    notifyChanged();
  };

  const sessionRecords = useMemo(
    () => videoIds.map((id) => records.get(id)).filter((record): record is UpgradeRecord => record !== undefined),
    [videoIds, records],
  );
  const upgradeable = sessionRecords.filter((record) => DOWNLOADABLE.includes(record.state));
  const readyToReplace = sessionRecords.filter((record) => record.state === "downloaded");
  const others = sessionRecords.filter(
    (record) => !DOWNLOADABLE.includes(record.state) && record.state !== "downloaded" && !IN_PROGRESS.includes(record.state),
  );
  const notChecked = videoIds.length - sessionRecords.length;
  const countOf = (state: UpgradeState) => sessionRecords.filter((record) => record.state === state).length;

  const startDownload = async () => {
    const token = session.current;
    setError(null);
    setPhase("starting");
    try {
      const started = await call<StartedJob>(`${API_BASE}/download`, {
        method: "POST",
        body: JSON.stringify({
          videoIds: [...pickedForDownload],
          makePrimary,
          deleteOldFile: makePrimary && deleteOldFiles,
        }),
      });
      if (session.current !== token) return;
      setJobVideoIds(started.videoIds);
      setJob({ id: started.jobId, status: "pending", progress: 0 });
      await loadRecords(videoIds);
      if (session.current === token) setPhase("downloading");
    } catch (reason) {
      if (session.current !== token) return;
      setError(errorText(reason));
      setPhase("review");
    }
  };

  const cancelJob = async () => {
    if (!jobId) return;
    try {
      await call(`/api/jobs/${jobId}`, { method: "DELETE" });
    } catch (reason) {
      setError(`Could not cancel: ${errorText(reason)}`);
    }
  };

  const runReplace = async () => {
    const targets = [...pickedForReplace]
      .map((id) => records.get(id))
      .filter((record): record is UpgradeRecord => !!record && record.state === "downloaded" && !!record.replacementFileId);
    if (targets.length === 0) return;
    if (
      deleteOldFiles &&
      !window.confirm(`Make ${plural(targets.length, "downloaded file")} primary and permanently delete the lower-resolution files from disk?`)
    ) {
      return;
    }

    const token = session.current;
    setPhase("replacing");
    setReplaceProgress(0);
    const errors: Record<number, string> = {};
    const done: number[] = [];
    for (const [index, record] of targets.entries()) {
      try {
        await replaceVideo(record, deleteOldFiles);
        done.push(record.videoId);
      } catch (reason) {
        errors[record.videoId] = errorText(reason);
      }
      if (session.current !== token) return;
      setReplaceProgress((index + 1) / targets.length);
    }
    setReplaced(done);
    setReplaceErrors(errors);
    try {
      await loadRecords(videoIds);
    } catch (reason) {
      setError(errorText(reason));
    }
    if (session.current === token) {
      setPhase("done");
      notifyChanged();
    }
  };

  if (!open) return null;

  const titles: Record<Phase, string> = {
    starting: "Resolution upgrades",
    checking: `Checking ${plural(jobVideoIds.length || videoIds.length, "video")} for resolution upgrades`,
    review: "Resolution upgrades",
    downloading: `Downloading ${plural(jobVideoIds.length, "upgrade")}`,
    replace: "Make the downloaded files primary?",
    replacing: "Making files primary",
    done: "Resolution upgrades",
  };

  const recordRow = (record: UpgradeRecord, options: { picked?: Set<number>; onToggle?: (id: number) => void; showState?: boolean; waiting?: boolean; target?: "available" | "replacement" }) => (
    <tr key={record.videoId} className="border-t border-border align-top">
      {options.picked && (
        <td className="w-8 px-3 py-2">
          <input
            type="checkbox"
            checked={options.picked.has(record.videoId)}
            onChange={() => options.onToggle?.(record.videoId)}
            aria-label={`Select ${record.title ?? `video ${record.videoId}`}`}
          />
        </td>
      )}
      <td className="px-3 py-2">
        <div className="font-medium text-foreground">{record.title ?? `Video ${record.videoId}`}</div>
        {(record.message || replaceErrors[record.videoId]) && (
          <div className={`mt-0.5 whitespace-pre-line text-xs ${replaceErrors[record.videoId] ? "text-rose-300" : "text-secondary"}`}>
            {replaceErrors[record.videoId] ?? record.message}
          </div>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-2 text-secondary">{formatResolution(record.currentWidth, record.currentHeight)}</td>
      <td className="whitespace-nowrap px-3 py-2">
        {options.target === "replacement"
          ? <span className="text-emerald-300">{formatResolution(record.replacementWidth, record.replacementHeight)}</span>
          : <span className="text-accent">{formatResolution(record.availableWidth, record.availableHeight)}</span>}
        {record.downloaderName && <div className="text-xs text-muted">via {record.downloaderName}</div>}
      </td>
      {options.showState && (
        <td className="px-3 py-2">
          <StateBadge state={record.state} waiting={options.waiting} />
        </td>
      )}
    </tr>
  );

  const table = (rows: React.ReactNode, headers: { pick?: React.ReactNode; state?: boolean; target: string }) => (
    <div className="overflow-hidden rounded-xl border border-border">
      <table className="w-full text-left text-sm">
        <thead className="bg-background/40 text-xs uppercase tracking-wide text-muted">
          <tr>
            {headers.pick !== undefined && <th className="w-8 px-3 py-2">{headers.pick}</th>}
            <th className="px-3 py-2">Video</th>
            <th className="px-3 py-2">In library</th>
            <th className="px-3 py-2">{headers.target}</th>
            {headers.state && <th className="px-3 py-2">Status</th>}
          </tr>
        </thead>
        <tbody>{rows}</tbody>
      </table>
    </div>
  );

  let body: React.ReactNode = null;
  let footer: React.ReactNode = null;

  if (phase === "starting") {
    body = (
      <div className="flex items-center gap-2 py-6 text-sm text-secondary">
        <Loader2 className="h-4 w-4 animate-spin" /> Starting...
      </div>
    );
  } else if (phase === "checking") {
    body = (
      <div className="space-y-4 py-2">
        <ProgressBar progress={job?.progress ?? 0} label={job?.subTask ?? "Waiting for the check to start..."} />
        <p className="text-sm text-secondary">
          Each video's source URL is asked what resolutions it offers. This can take a few seconds per video; you can close
          this window and the check keeps running (results appear on the Resolution Upgrades page).
        </p>
      </div>
    );
    footer = (
      <>
        <button onClick={() => void cancelJob()} className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground">
          Cancel check
        </button>
        <button onClick={close} className="rounded-lg border border-border px-3 py-2 text-sm hover:border-accent">
          Close
        </button>
      </>
    );
  } else if (phase === "review") {
    const summary = [
      { label: "can be upgraded", count: countOf("upgrade-available"), style: "text-accent" },
      { label: "download failed before", count: countOf("download-failed"), style: "text-rose-300" },
      { label: "already downloaded", count: readyToReplace.length, style: "text-emerald-300" },
      { label: "already at the best resolution", count: countOf("up-to-date"), style: "text-secondary" },
      { label: "have no downloadable source", count: countOf("no-source"), style: "text-secondary" },
      { label: "could not be checked", count: countOf("check-failed"), style: "text-rose-300" },
      { label: "downloading", count: sessionRecords.filter((record) => IN_PROGRESS.includes(record.state)).length, style: "text-amber-300" },
      { label: "not checked yet", count: notChecked, style: "text-secondary" },
    ].filter((entry) => entry.count > 0);

    body = (
      <div className="space-y-4">
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
          {summary.map((entry) => (
            <span key={entry.label}>
              <strong className={entry.style}>{entry.count}</strong> <span className="text-secondary">{entry.label}</span>
            </span>
          ))}
        </div>

        {upgradeable.length > 0 ? (
          <div className="space-y-2">
            <p className="text-sm text-secondary">Choose the videos to upgrade. The higher-resolution file is added to the same video; nothing is replaced until the last step.</p>
            {table(
              upgradeable.map((record) =>
                recordRow(record, {
                  picked: pickedForDownload,
                  onToggle: (id) => setPickedForDownload((current) => togglePick(current, id)),
                  showState: countOf("download-failed") > 0,
                }),
              ),
              {
                pick: <SelectAllCheckbox ids={upgradeable.map((record) => record.videoId)} picked={pickedForDownload} onChange={setPickedForDownload} />,
                state: countOf("download-failed") > 0,
                target: "Available",
              },
            )}
          </div>
        ) : (
          <div className="rounded-xl border border-dashed border-border px-4 py-6 text-center text-sm text-secondary">
            {notChecked === videoIds.length
              ? "These videos haven't been checked yet."
              : "None of these videos has a higher resolution to download."}
          </div>
        )}

        {upgradeable.length > 0 && (
          <div className="space-y-2 rounded-xl border border-border bg-background/30 p-3">
            <div className="text-sm font-medium">When each download finishes</div>
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                className="mt-0.5"
                checked={makePrimary}
                onChange={(event) => setMakePrimary(event.target.checked)}
              />
              <span>
                Make the downloaded file primary automatically
                <span className="block text-xs text-secondary">
                  Each video is finished as soon as its own download lands, so you can start a long run and close
                  this window. Cove only swaps files it can confirm are the same footage; anything else is left for
                  review.
                </span>
              </span>
            </label>
            <label className={`flex items-start gap-2 text-sm ${makePrimary ? "" : "opacity-50"}`}>
              <input
                type="checkbox"
                className="mt-0.5"
                checked={makePrimary && deleteOldFiles}
                disabled={!makePrimary}
                onChange={(event) => setDeleteOldFiles(event.target.checked)}
              />
              <span>
                Delete the lower-resolution file from disk afterwards
                <span className="block text-xs text-secondary">Only after the new file has become primary.</span>
              </span>
            </label>
          </div>
        )}

        {readyToReplace.length > 0 && (
          <div className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-emerald-500/30 bg-emerald-500/5 px-3 py-2 text-sm">
            <span>{plural(readyToReplace.length, "video")} already {readyToReplace.length === 1 ? "has" : "have"} a downloaded upgrade waiting to become primary.</span>
            <button
              onClick={() => {
                setPickedForReplace(new Set(readyToReplace.map((record) => record.videoId)));
                setPhase("replace");
              }}
              className="rounded-lg border border-emerald-500/40 px-3 py-1.5 text-emerald-300 hover:bg-emerald-500/10"
            >
              Make primary...
            </button>
          </div>
        )}

        {others.length > 0 && (
          <div>
            <button onClick={() => setShowOthers((value) => !value)} className="text-xs text-secondary hover:text-foreground">
              {showOthers ? "Hide" : "Show"} the {plural(others.length, "other video")}
            </button>
            {showOthers && (
              <div className="mt-2">
                {table(others.map((record) => recordRow(record, { showState: true })), { state: true, target: "Best available" })}
              </div>
            )}
          </div>
        )}
      </div>
    );
    footer = (
      <>
        {notChecked > 0 && (
          <button
            onClick={() => void startCheck(videoIds, session.current)}
            className="mr-auto inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
          >
            <Search className="h-4 w-4" /> Check {plural(videoIds.length, "video")}
          </button>
        )}
        <button onClick={close} className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground">
          Close
        </button>
        <button
          disabled={pickedForDownload.size === 0}
          onClick={() => void startDownload()}
          className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-50"
        >
          <Download className="h-4 w-4" /> Download {plural(pickedForDownload.size, "upgrade")}
        </button>
      </>
    );
  } else if (phase === "downloading") {
    const rows = jobVideoIds.map((id) => records.get(id)).filter((record): record is UpgradeRecord => record !== undefined);
    body = (
      <div className="space-y-4">
        <ProgressBar progress={job?.progress ?? 0} label={job?.subTask ?? "Waiting for downloads to start..."} />
        {table(
          rows.map((record) => recordRow(record, { showState: true, waiting: !job || !JOB_FINISHED.includes(job.status) })),
          { state: true, target: "Downloading" },
        )}
        <p className="text-xs text-secondary">
          {makePrimary
            ? "You can close this window. Each video is made primary as soon as its own download finishes, and anything Cove could not swap automatically is listed on the Resolution Upgrades page."
            : "You can close this window; downloads keep running. Come back through Upgrade resolution on the Videos page, or the Resolution Upgrades page, to make the new files primary."}
        </p>
      </div>
    );
    footer = (
      <>
        <button onClick={() => void cancelJob()} className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground">
          Cancel downloads
        </button>
        <button onClick={close} className="rounded-lg border border-border px-3 py-2 text-sm hover:border-accent">
          Close
        </button>
      </>
    );
  } else if (phase === "replace" || phase === "replacing") {
    const candidates = sessionRecords.filter((record) => record.state === "downloaded");
    const failedDownloads = sessionRecords.filter((record) => jobVideoIds.includes(record.videoId) && record.state === "download-failed");
    body = (
      <div className="space-y-4">
        {phase === "replacing" ? (
          <ProgressBar progress={replaceProgress} label="Updating primary files..." />
        ) : (
          <p className="text-sm text-secondary">
            {plural(candidates.length, "higher-resolution file")} downloaded. Make {candidates.length === 1 ? "it" : "them"} the
            primary file so Cove plays and generates previews from the better version. Cove only swaps files it can confirm are
            the same video (matching duration and appearance) when a video has markers or other timed content.
          </p>
        )}
        {table(
          candidates.map((record) =>
            recordRow(record, {
              picked: pickedForReplace,
              onToggle: (id) => setPickedForReplace((current) => togglePick(current, id)),
              target: "replacement",
            }),
          ),
          {
            pick: <SelectAllCheckbox ids={candidates.map((record) => record.videoId)} picked={pickedForReplace} onChange={setPickedForReplace} />,
            target: "Downloaded",
          },
        )}
        {failedDownloads.length > 0 && (
          <div className="rounded-xl border border-rose-500/30 bg-rose-500/5 p-3 text-sm">
            <div className="mb-1 flex items-center gap-1.5 text-rose-300">
              <AlertTriangle className="h-4 w-4" /> {plural(failedDownloads.length, "download")} failed
            </div>
            {failedDownloads.map((record) => (
              <div key={record.videoId} className="text-xs text-secondary">
                <span className="text-foreground">{record.title}</span>: {record.message}
              </div>
            ))}
          </div>
        )}
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={deleteOldFiles} disabled={phase === "replacing"} onChange={(event) => setDeleteOldFiles(event.target.checked)} />
          <Trash2 className="h-4 w-4 text-secondary" /> Also delete the lower-resolution files from disk
        </label>
      </div>
    );
    footer = (
      <>
        <button
          disabled={phase === "replacing"}
          onClick={close}
          className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-50"
        >
          Not now
        </button>
        <button
          disabled={phase === "replacing" || pickedForReplace.size === 0}
          onClick={() => void runReplace()}
          className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-50"
        >
          {phase === "replacing" ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
          Make {plural(pickedForReplace.size, "file")} primary
        </button>
      </>
    );
  } else if (phase === "done") {
    const failures = Object.entries(replaceErrors);
    const failedDownloads = sessionRecords.filter((record) => jobVideoIds.includes(record.videoId) && record.state === "download-failed");
    body = (
      <div className="space-y-3 py-2 text-sm">
        {replaced.length > 0 && (
          <div className="flex items-center gap-2 text-emerald-300">
            <CheckCircle2 className="h-4 w-4" /> Made {plural(replaced.length, "higher-resolution file")} primary.
          </div>
        )}
        {failures.map(([videoId, message]) => (
          <div key={videoId} className="flex items-start gap-2 text-rose-300">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>
              <span className="text-foreground">{records.get(Number(videoId))?.title ?? `Video ${videoId}`}</span>: {message}
            </span>
          </div>
        ))}
        {failedDownloads.map((record) => (
          <div key={record.videoId} className="flex items-start gap-2 text-rose-300">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>
              <span className="text-foreground">{record.title}</span>: {record.message}
            </span>
          </div>
        ))}
        {replaced.length === 0 && failures.length === 0 && failedDownloads.length === 0 && !error && (
          <div className="text-secondary">Nothing left to do for these videos.</div>
        )}
      </div>
    );
    footer = (
      <button onClick={close} className="rounded-lg border border-border px-3 py-2 text-sm hover:border-accent">
        Close
      </button>
    );
  }

  return (
    <div
      className="resolution-upgrade-dialog fixed inset-0 z-[80] flex items-center justify-center bg-black/60 p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && phase !== "replacing") close();
      }}
    >
      <div role="dialog" aria-modal="true" className="flex max-h-[85vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl border border-border bg-card text-foreground shadow-2xl">
        <div className="flex items-center justify-between gap-3 border-b border-border px-5 py-3">
          <h2 className="flex items-center gap-2 text-lg font-semibold">
            <ArrowUpCircle className="h-5 w-5 text-accent" /> {titles[phase]}
          </h2>
          <button onClick={close} disabled={phase === "replacing"} aria-label="Close" className="rounded-md p-1 text-secondary hover:text-foreground disabled:opacity-50">
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {error && (
            <div className="mb-3 flex items-start gap-2 rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" /> {error}
            </div>
          )}
          {body}
        </div>
        {footer && <div className="flex flex-wrap items-center justify-end gap-2 border-t border-border px-5 py-3">{footer}</div>}
      </div>
    </div>
  );
}

// ── Results page ────────────────────────────────────────────────────────────

type Filter = "all" | "ready" | "available" | "working" | "failed" | "current";

const FILTERS: { id: Filter; label: string; states: UpgradeState[] }[] = [
  { id: "all", label: "All", states: [] },
  { id: "available", label: "Upgrade available", states: ["upgrade-available"] },
  { id: "ready", label: "Ready to make primary", states: ["downloaded"] },
  { id: "working", label: "Downloading", states: ["download-queued", "downloading"] },
  { id: "failed", label: "Failed", states: ["check-failed", "download-failed"] },
  { id: "current", label: "No upgrade", states: ["up-to-date", "no-source"] },
];

function ResolutionUpgradesPage({ onNavigate }: { onNavigate?: (route: { page: string; id?: number }) => void }) {
  const [records, setRecords] = useState<UpgradeRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelected] = useState<Set<number>>(() => new Set());
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRecords(await call<UpgradeRecord[]>(`${API_BASE}/results`));
      setLoadError(null);
    } catch (reason) {
      setLoadError(errorText(reason));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
    const onChanged = () => void load();
    window.addEventListener(CHANGED_EVENT, onChanged);
    return () => window.removeEventListener(CHANGED_EVENT, onChanged);
  }, [load]);

  const hasWorkInProgress = records.some((record) => IN_PROGRESS.includes(record.state));
  useEffect(() => {
    if (!hasWorkInProgress) return;
    const timer = window.setInterval(() => void load(), 5000);
    return () => window.clearInterval(timer);
  }, [hasWorkInProgress, load]);

  const counts = useMemo(() => {
    const result = {} as Record<Filter, number>;
    for (const entry of FILTERS) {
      result[entry.id] = entry.states.length === 0
        ? records.length
        : records.filter((record) => entry.states.includes(record.state)).length;
    }
    return result;
  }, [records]);

  const visible = useMemo(() => {
    const states = FILTERS.find((entry) => entry.id === filter)?.states ?? [];
    return states.length === 0 ? records : records.filter((record) => states.includes(record.state));
  }, [records, filter]);

  const open = (mode: DialogMode) => {
    setNotice(null);
    try {
      requestDialog(mode, [...selected]);
    } catch (reason) {
      setNotice(errorText(reason));
    }
  };

  const dismissSelected = async () => {
    try {
      await call(`${API_BASE}/dismiss`, { method: "POST", body: JSON.stringify({ videoIds: [...selected] }) });
      setSelected(new Set());
      await load();
    } catch (reason) {
      setNotice(errorText(reason));
    }
  };

  const visibleIds = visible.map((record) => record.videoId);

  return (
    <div className="resolution-upgrade-page mx-auto max-w-7xl space-y-4 p-4 text-foreground">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-semibold">
            <ArrowUpCircle className="h-6 w-6 text-accent" /> Resolution Upgrades
          </h1>
          <p className="mt-1 max-w-3xl text-sm text-secondary">
            Videos you have checked for a higher resolution. To check more, select videos on the Videos page and choose
            <strong> Check for resolution upgrades</strong>; a dialog walks you through picking, downloading, and making the
            new files primary.
          </p>
        </div>
        <button
          onClick={() => void load()}
          className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
        >
          <RefreshCw className={`h-4 w-4 ${hasWorkInProgress ? "animate-spin" : ""}`} /> Refresh
        </button>
      </header>

      <div className="flex flex-wrap gap-2">
        {FILTERS.map((entry) => (
          <button
            key={entry.id}
            onClick={() => setFilter(entry.id)}
            className={`rounded-full border px-3 py-1 text-xs ${
              filter === entry.id ? "border-accent bg-accent/15 text-accent" : "border-border text-secondary hover:text-foreground"
            }`}
          >
            {entry.label} <span className="opacity-70">{counts[entry.id]}</span>
          </button>
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-xl border border-border bg-card p-3 text-sm">
        <span className="text-secondary">{selected.size} selected</span>
        <button
          disabled={selected.size === 0}
          onClick={() => open("upgrade")}
          className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-1.5 font-medium text-white hover:bg-accent-hover disabled:opacity-50"
        >
          <Download className="h-4 w-4" /> Upgrade selected...
        </button>
        <button
          disabled={selected.size === 0}
          onClick={() => open("check")}
          className="inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1.5 hover:border-accent disabled:opacity-50"
        >
          <Search className="h-4 w-4" /> Check again
        </button>
        <button
          disabled={selected.size === 0}
          onClick={() => void dismissSelected()}
          className="inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1.5 text-secondary hover:text-foreground disabled:opacity-50"
        >
          <Trash2 className="h-4 w-4" /> Dismiss
        </button>
      </div>

      {notice && <div className="rounded-lg border border-border bg-card px-3 py-2 text-sm">{notice}</div>}
      {loadError && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{loadError}</div>
      )}

      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading results...
        </div>
      ) : visible.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-secondary">
          {records.length === 0
            ? "No videos have been checked yet. Select videos on the Videos page and choose Check for resolution upgrades."
            : "No videos match this filter."}
        </div>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-border">
          <table className="w-full text-left text-sm">
            <thead className="bg-card text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="w-8 px-3 py-2">
                  <SelectAllCheckbox ids={visibleIds} picked={selected} onChange={setSelected} />
                </th>
                <th className="px-3 py-2">Video</th>
                <th className="px-3 py-2">In library</th>
                <th className="px-3 py-2">Available</th>
                <th className="px-3 py-2">Status</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((record) => (
                <tr key={record.videoId} className="border-t border-border align-top">
                  <td className="px-3 py-2">
                    <input
                      type="checkbox"
                      checked={selected.has(record.videoId)}
                      onChange={() => setSelected((current) => togglePick(current, record.videoId))}
                      aria-label={`Select ${record.title ?? `video ${record.videoId}`}`}
                    />
                  </td>
                  <td className="px-3 py-2">
                    <button
                      onClick={() => onNavigate?.({ page: "video", id: record.videoId })}
                      className="text-left font-medium text-foreground hover:text-accent"
                    >
                      {record.title ?? `Video ${record.videoId}`}
                    </button>
                    {record.url && <div className="mt-0.5 max-w-md truncate text-xs text-muted">{record.url}</div>}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2">{formatResolution(record.currentWidth, record.currentHeight)}</td>
                  <td className="whitespace-nowrap px-3 py-2">
                    {record.replacementHeight ? (
                      <span className="text-emerald-300">{formatResolution(record.replacementWidth, record.replacementHeight)}</span>
                    ) : (
                      formatResolution(record.availableWidth, record.availableHeight)
                    )}
                    {record.downloaderName && <div className="text-xs text-muted">via {record.downloaderName}</div>}
                  </td>
                  <td className="px-3 py-2">
                    <StateBadge state={record.state} />
                    {record.message && <div className="mt-1 whitespace-pre-line text-xs text-secondary">{record.message}</div>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export default {
  components: {
    ResolutionUpgradesPage,
    ResolutionUpgradeDialog,
  },
  actionHandlers: {
    openResolutionCheck,
    openResolutionUpgrade,
  },
};
