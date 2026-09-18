import {
	Alert,
	Button,
	Card,
	CardContent,
	Checkbox,
	CircularProgress,
	Dialog,
	DialogActions,
	DialogContent,
	DialogTitle,
	FormControlLabel,
	MenuItem,
	Stack,
	TextField,
	Typography,
} from "@mui/material";
import { ask, open } from "@tauri-apps/plugin-dialog";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { PathInput } from "@/components/PathInput";
import { useCloudSaves } from "@/hooks/queries/useCloudSaves";
import {
	type CloudGame,
	CloudSaveError,
	type CloudSnapshot,
	cloudSaveService,
	type SaveSlot,
} from "@/services/invoke/cloudSaveService";

type Preview = {
	profile: CloudGame;
	files: { slotId: string; relativePath: string; length: number }[];
	total: number;
};
const newSlot = (path = "", isFile = false): SaveSlot => ({
	id: crypto.randomUUID().replaceAll("-", ""),
	label: "",
	path,
	isFile,
	recursive: !isFile,
	patterns: ["*"],
});

export function CloudSaves({ gameId, name }: { gameId: number; name: string }) {
	const { t } = useTranslation();
	const status = useCloudSaves(gameId);
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState("");
	const [slots, setSlots] = useState<SaveSlot[]>([]);
	const [evidence, setEvidence] = useState("");
	const [cloudGames, setCloudGames] = useState<CloudGame[]>([]);
	const [cloudId, setCloudId] = useState("");
	const [preview, setPreview] = useState<Preview | null>(null);
	const [history, setHistory] = useState<CloudSnapshot[] | null>(null);
	const [conflict, setConflict] = useState<CloudSnapshot[] | null>(null);
	const run = async (work: () => Promise<unknown>) => {
		setBusy(true);
		setError("");
		try {
			await work();
		} catch (e) {
			setError(e instanceof Error ? e.message : String(e));
			if (e instanceof CloudSaveError && e.code === "conflict")
				setConflict(e.heads);
		} finally {
			setBusy(false);
			void status.refetch();
		}
	};
	const request = (action: string, fields: Record<string, unknown> = {}) =>
		cloudSaveService.request(action, gameId, fields);
	const pick = async (isFile: boolean) => {
		const path = await open({ directory: !isFile, multiple: false });
		if (typeof path === "string")
			setSlots((current) => [...current, newSlot(path, isFile)]);
	};
	const choose = async (snapshotId?: string) => {
		if (!(await ask(t("cloudSaves.restoreConfirm"), { kind: "warning" })))
			return;
		await run(async () => {
			await request("resolve", { snapshotId, confirmed: true });
			setConflict(null);
			setHistory(null);
		});
	};
	const data = status.data;
	return (
		<Card variant="outlined">
			<CardContent>
				<Stack spacing={2}>
					<Typography variant="h6">{t("cloudSaves.title")}</Typography>
					<Typography variant="body2">{t("cloudSaves.intro")}</Typography>
					{(error || status.error) && (
						<Alert severity="error">{error || String(status.error)}</Alert>
					)}
					{busy && <CircularProgress size={24} />}
					<Stack direction="row" spacing={1} useFlexGap className="flex-wrap">
						<Button
							disabled={busy}
							onClick={() =>
								void run(async () => {
									const path = await open({
										filters: [{ name: "OAuth JSON", extensions: ["json"] }],
									});
									if (typeof path === "string")
										await request("import-client", { clientPath: path });
								})
							}
						>
							{t("cloudSaves.importClient")}
						</Button>
						<Button
							disabled={busy || !data?.hasClient}
							onClick={() => void run(() => request("login"))}
						>
							{data?.signedIn ? t("cloudSaves.relogin") : t("cloudSaves.login")}
						</Button>
						<Button
							href="https://console.cloud.google.com/auth/audience"
							target="_blank"
							rel="noreferrer"
						>
							{t("cloudSaves.setup")}
						</Button>
					</Stack>
					<Alert severity="info">{t("cloudSaves.oauthHelp")}</Alert>
					{data?.game ? (
						<>
							<Typography>
								{data.game.status} ·{" "}
								{t("cloudSaves.pending", { count: data.pending })}
							</Typography>
							<Typography variant="caption">ID: {data.game.id}</Typography>
							{data.last && (
								<Typography variant="body2">
									{data.last.device} ·{" "}
									{new Date(data.last.createdUtc).toLocaleString()}
								</Typography>
							)}
							{data.game.slots.map((slot) => (
								<Typography key={slot.id} variant="body2" className="break-all">
									{slot.label} · {slot.path} ({slot.patterns.join(", ")})
								</Typography>
							))}
							<Stack
								direction="row"
								spacing={1}
								useFlexGap
								className="flex-wrap"
							>
								<Button
									disabled={busy || data.active}
									onClick={() => void run(() => request("sync"))}
								>
									{t("cloudSaves.sync")}
								</Button>
								<Button
									disabled={busy || data.active}
									onClick={() => void run(() => request("backup"))}
								>
									{t("cloudSaves.backup")}
								</Button>
								<Button
									disabled={busy || data.active}
									onClick={() =>
										void run(async () =>
											setHistory(
												await cloudSaveService.request<CloudSnapshot[]>(
													"history",
													gameId,
												),
											),
										)
									}
								>
									{t("cloudSaves.history")}
								</Button>
								<Button
									disabled={busy || data.active}
									onClick={() => void run(() => request("pause"))}
								>
									{data.enabled
										? t("cloudSaves.pause")
										: t("cloudSaves.resume")}
								</Button>
								<Button
									disabled={busy || data.active}
									onClick={() =>
										void run(async () => {
											if (await ask(t("cloudSaves.relinkConfirm")))
												await request("relink", { confirmed: true });
										})
									}
								>
									{t("cloudSaves.relink")}
								</Button>
								{data.active && (
									<Button
										disabled={busy}
										onClick={() =>
											void run(async () => {
												if (
													await ask(t("cloudSaves.endedConfirm"), {
														kind: "warning",
													})
												)
													await request("confirm-ended", { confirmed: true });
											})
										}
									>
										{t("cloudSaves.ended")}
									</Button>
								)}
							</Stack>
						</>
					) : (
						<>
							<Stack
								direction="row"
								spacing={1}
								useFlexGap
								className="flex-wrap"
							>
								<Button
									disabled={busy || !!cloudId}
									onClick={() =>
										void run(async () => {
											const result = await cloudSaveService.request<{
												evidence: string;
												slots: SaveSlot[];
											}>("identify", gameId);
											setSlots(result.slots);
											setEvidence(result.evidence);
										})
									}
								>
									{t("cloudSaves.detect")}
								</Button>
								<Button
									disabled={busy || !!cloudId}
									onClick={() => void run(() => pick(false))}
								>
									{t("cloudSaves.addFolder")}
								</Button>
								<Button
									disabled={busy || !!cloudId}
									onClick={() => void run(() => pick(true))}
								>
									{t("cloudSaves.addFile")}
								</Button>
								<Button
									disabled={busy || !data?.signedIn}
									onClick={() =>
										void run(async () =>
											setCloudGames(
												await cloudSaveService.request<CloudGame[]>(
													"cloud-games",
												),
											),
										)
									}
								>
									{t("cloudSaves.loadCloud")}
								</Button>
							</Stack>
							{cloudGames.length > 0 && (
								<TextField
									select
									label={t("cloudSaves.linkCloud")}
									value={cloudId}
									disabled={busy}
									onChange={(e) => {
										setCloudId(e.target.value);
										setSlots(
											cloudGames.find((game) => game.id === e.target.value)
												?.slots ?? [],
										);
										setEvidence("");
									}}
								>
									<MenuItem value="">{t("cloudSaves.newGame")}</MenuItem>
									{cloudGames.map((game) => (
										<MenuItem key={game.id} value={game.id}>
											{game.name} · {game.id.slice(0, 8)}
										</MenuItem>
									))}
								</TextField>
							)}
							{evidence && <Alert severity="info">{evidence}</Alert>}
							{slots.map((slot, index) => (
								<Stack key={slot.id} spacing={1}>
									<PathInput
										label={slot.label || t("cloudSaves.path")}
										value={slot.path}
										pathType={slot.isFile ? "file" : "directory"}
										disabled={busy}
										onChange={(value) =>
											setSlots(
												slots.map((s, i) =>
													i === index ? { ...s, path: value } : s,
												),
											)
										}
									/>
									<Stack direction="row" spacing={1}>
										<TextField
											label={t("cloudSaves.patterns")}
											value={slot.patterns.join(";")}
											disabled={busy || !!cloudId}
											onChange={(e) =>
												setSlots(
													slots.map((s, i) =>
														i === index
															? { ...s, patterns: e.target.value.split(";") }
															: s,
													),
												)
											}
										/>
										<FormControlLabel
											label={t("cloudSaves.recursive")}
											control={
												<Checkbox
													disabled={busy || !!cloudId || slot.isFile}
													checked={slot.recursive}
													onChange={(_, checked) =>
														setSlots(
															slots.map((s, i) =>
																i === index ? { ...s, recursive: checked } : s,
															),
														)
													}
												/>
											}
										/>
										<Button
											disabled={busy || !!cloudId}
											onClick={() =>
												setSlots(slots.filter((_, i) => i !== index))
											}
										>
											{t("cloudSaves.remove")}
										</Button>
									</Stack>
								</Stack>
							))}
							<Button
								variant="contained"
								disabled={busy || slots.length === 0}
								onClick={() =>
									void run(async () =>
										setPreview(
											await cloudSaveService.request<Preview>(
												"preview",
												gameId,
												{ name, slots, cloudId: cloudId || undefined },
											),
										),
									)
								}
							>
								{t("cloudSaves.preview")}
							</Button>
						</>
					)}
					<Dialog
						open={preview !== null}
						onClose={() => !busy && setPreview(null)}
						fullWidth
						maxWidth="md"
					>
						<DialogTitle>{t("cloudSaves.preview")}</DialogTitle>
						<DialogContent>
							<Typography>
								{t("cloudSaves.previewCount", { count: preview?.total ?? 0 })}
							</Typography>
							{preview?.total === 0 && (
								<Alert severity="info">{t("cloudSaves.noSaves")}</Alert>
							)}
							{preview?.files.map((file) => (
								<Typography
									key={`${file.slotId}/${file.relativePath}`}
									variant="body2"
								>
									{file.relativePath} · {file.length} B
								</Typography>
							))}
						</DialogContent>
						<DialogActions>
							<Button disabled={busy} onClick={() => setPreview(null)}>
								{t("common.cancel")}
							</Button>
							<Button
								disabled={busy}
								onClick={() =>
									void run(async () => {
										await request("configure", {
											name,
											slots,
											cloudId: cloudId || undefined,
											confirmed: true,
										});
										setPreview(null);
									})
								}
							>
								{t("cloudSaves.enable")}
							</Button>
						</DialogActions>
					</Dialog>
					<Dialog
						open={history !== null || conflict !== null}
						onClose={() => {
							if (!busy) {
								setHistory(null);
								setConflict(null);
							}
						}}
						fullWidth
						maxWidth="md"
					>
						<DialogTitle>
							{conflict ? t("cloudSaves.conflict") : t("cloudSaves.history")}
						</DialogTitle>
						<DialogContent>
							<Stack spacing={2}>
								<Alert severity="warning">{t("cloudSaves.preserve")}</Alert>
								{(conflict ?? history ?? []).map((snapshot) => (
									<Button
										disabled={busy}
										key={snapshot.id}
										onClick={() => void choose(snapshot.id)}
									>
										{snapshot.device} ·{" "}
										{new Date(snapshot.createdUtc).toLocaleString()} ·{" "}
										{snapshot.id.slice(0, 8)} · {snapshot.files.length}
									</Button>
								))}
								{conflict && (
									<Button disabled={busy} onClick={() => void choose()}>
										{t("cloudSaves.keepLocal")}
									</Button>
								)}
								{error && <Alert severity="error">{error}</Alert>}
							</Stack>
						</DialogContent>
						<DialogActions>
							<Button
								disabled={busy}
								onClick={() => {
									setHistory(null);
									setConflict(null);
								}}
							>
								{t("common.cancel")}
							</Button>
						</DialogActions>
					</Dialog>
				</Stack>
			</CardContent>
		</Card>
	);
}
