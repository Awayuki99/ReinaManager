import { ask } from "@tauri-apps/plugin-dialog";
import i18n from "i18next";
import { statsService } from "@/services/invoke";
import type { LaunchGameResult } from "@/services/invoke/statsService";
import type { StopGameResult, TimeTrackingMode } from "@/types";
import { toError } from "@/utils/errors";

export async function launchGameWithTracking(
	gameId: number,
	timeTrackingMode: TimeTrackingMode,
	args?: string[],
): Promise<LaunchGameResult> {
	try {
		const result = await statsService.launchGame(
			gameId,
			args || [],
			timeTrackingMode,
		);
		if (
			result.status === "failed" &&
			/^CLOUD_SAVE:(auth|network):/.test(result.message)
		) {
			if (await ask(i18n.t("cloudSaves.offlinePrompt"), { kind: "warning" })) {
				return await statsService.launchGame(
					gameId,
					args || [],
					timeTrackingMode,
					true,
				);
			}
		}
		if (
			result.status === "failed" &&
			result.message.startsWith("CLOUD_SAVE:conflict:")
		) {
			return { ...result, message: i18n.t("cloudSaves.launchConflict") };
		}
		return result;
	} catch (error) {
		throw toError(error, "Failed to launch game");
	}
}

export async function stopGameWithTracking(
	gameId: number,
): Promise<StopGameResult> {
	try {
		return await statsService.stopGame(gameId);
	} catch (error) {
		throw toError(error, "Failed to stop game");
	}
}
