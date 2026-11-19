import { DeliveryLog } from '../features/DeliveryLog';
import { usePageTitle } from '../lib/usePageTitle';

export function Logs() {
  usePageTitle('Delivery logs');
  return (
    <>
      <div className="page-h">
        <div>
          <h1>Delivery logs</h1>
          <p className="sub">Every event we sent, with every attempt. Click a row to see exactly what was sent and what your server answered.</p>
        </div>
      </div>
      <DeliveryLog />
    </>
  );
}
