import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from './client';
import { webhooksApi } from './webhooks';

/** Every key in one place so a change can say exactly what it makes stale. */
export const keys = {
  overview: ['overview'] as const,
  endpoints: ['endpoints'] as const,
  endpoint: (id: string) => ['endpoint', id] as const,
  catalog: ['catalog'] as const,
  presets: ['retry-presets'] as const,
  egress: ['egress-policy'] as const,
  versions: ['api-versions'] as const,
};

const live = { refetchInterval: 10_000 };

export const useOverview = () => useQuery({ queryKey: keys.overview, queryFn: ({ signal }) => webhooksApi.overview(signal), ...live });
export const useEndpoints = () => useQuery({ queryKey: keys.endpoints, queryFn: ({ signal }) => webhooksApi.listEndpoints(signal), ...live });
export const useEndpoint = (id: string) => useQuery({ queryKey: keys.endpoint(id), queryFn: ({ signal }) => webhooksApi.getEndpoint(id, signal), refetchInterval: 15_000 });
export const useCatalog = () => useQuery({ queryKey: keys.catalog, queryFn: ({ signal }) => webhooksApi.catalog(signal), staleTime: Infinity });
export const useRetryPresets = () => useQuery({ queryKey: keys.presets, queryFn: ({ signal }) => webhooksApi.retryPresets(signal), staleTime: Infinity });
export const useEgressPolicy = () => useQuery({ queryKey: keys.egress, queryFn: ({ signal }) => webhooksApi.egressPolicy(signal), staleTime: 60_000 });
export const useApiVersions = () => useQuery({ queryKey: keys.versions, queryFn: ({ signal }) => webhooksApi.apiVersions(signal), staleTime: Infinity });

/** A change to an endpoint makes the lists, the overview numbers and that endpoint's own page stale. */
export function useInvalidateEndpoint() {
  const client = useQueryClient();
  return (id?: string) => Promise.all([
    client.invalidateQueries({ queryKey: keys.endpoints }),
    client.invalidateQueries({ queryKey: keys.overview }),
    id ? client.invalidateQueries({ queryKey: keys.endpoint(id) }) : Promise.resolve(),
  ]);
}

/** A mutation that refreshes what it touched when it succeeds. */
export function useEndpointMutation<TArgs, TResult>(id: string | undefined, run: (args: TArgs) => Promise<TResult>) {
  const invalidate = useInvalidateEndpoint();
  return useMutation<TResult, ApiError, TArgs>({
    mutationFn: run,
    onSuccess: () => invalidate(id),
  });
}
