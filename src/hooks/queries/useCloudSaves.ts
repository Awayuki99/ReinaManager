import { useQuery } from "@tanstack/react-query";
import {
	type CloudStatus,
	cloudSaveService,
} from "@/services/invoke/cloudSaveService";

export function useCloudSaves(gameId: number) {
	return useQuery({
		queryKey: ["cloud-saves", gameId],
		queryFn: () => cloudSaveService.request<CloudStatus>("status", gameId),
		refetchInterval: 5000,
	});
}
