import { BaseService } from "./base";

export interface SaveSlot {
	id: string;
	label: string;
	path: string;
	isFile: boolean;
	recursive: boolean;
	patterns: string[];
}
export interface CloudGame {
	id: string;
	name: string;
	slots: SaveSlot[];
	status?: string;
}
export interface CloudSnapshot {
	id: string;
	device: string;
	createdUtc: string;
	files: { relativePath: string; length: number }[];
}
export interface CloudStatus {
	hasClient: boolean;
	signedIn: boolean;
	game: CloudGame | null;
	enabled: boolean;
	pending: number;
	active: boolean;
	activeCount: number;
	retryCount: number;
	last: CloudSnapshot | null;
}
export class CloudSaveError extends Error {
	constructor(
		message: string,
		public code: string,
		public heads: CloudSnapshot[] = [],
	) {
		super(message);
	}
}
class CloudSaveService extends BaseService {
	async request<T>(
		action: string,
		gameId?: number,
		fields: Record<string, unknown> = {},
	): Promise<T> {
		const result = await this.invoke<{
			ok: boolean;
			data: T;
			code: string;
			message: string;
			heads?: CloudSnapshot[];
		}>("cloud_saves", {
			request: { action, gameId, ...fields },
		});
		if (!result.ok)
			throw new CloudSaveError(result.message, result.code, result.heads ?? []);
		return result.data;
	}
}
export const cloudSaveService = new CloudSaveService();
