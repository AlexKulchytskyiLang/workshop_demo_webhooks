import { DeliveryLog } from '../../features/DeliveryLog';
import { useEndpointCtx } from './EndpointLayout';

export function LogsTab() {
  const { detail } = useEndpointCtx();
  return <DeliveryLog endpointId={detail.endpoint.id} />;
}
