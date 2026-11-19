import { useId } from 'react';
import { fieldSuggestions, MAX_CONDITIONS, OPERATORS, countConditions, rowError, type FilterRow, type FilterState } from '../lib/filters';
import { Icon } from '../ui/Icon';

interface Props {
  eventType: string;
  state: FilterState;
  onChange: (next: FilterState) => void;
  disabled?: boolean;
}

/**
 * Builds the subscription filter: conditions in a group must ALL hold (AND); a delivery happens if ANY group matches (OR).
 * Each row is validated as it is typed, so nothing invalid can be saved, and the server shows the compiled expression next to it.
 */
export function FilterBuilder({ eventType, state, onChange, disabled }: Props) {
  const listId = useId();
  const total = countConditions(state);
  const canAdd = !disabled && total < MAX_CONDITIONS;

  const update = (g: number, r: number, patch: Partial<FilterRow>) =>
    onChange({ groups: state.groups.map((group, gi) => (gi !== g ? group : group.map((row, ri) => (ri !== r ? row : { ...row, ...patch })))) });
  const remove = (g: number, r: number) =>
    onChange({ groups: state.groups.map((group, gi) => (gi !== g ? group : group.filter((_, ri) => ri !== r))).filter((group) => group.length > 0) });
  const addToLast = () => {
    const blank: FilterRow = { path: fieldSuggestions(eventType).find((f) => f.startsWith('data.')) ?? 'type', op: 'eq', value: '' };
    onChange({ groups: state.groups.length === 0 ? [[blank]] : state.groups.map((group, gi) => (gi === state.groups.length - 1 ? [...group, blank] : group)) });
  };
  const addGroup = () => onChange({ groups: [...state.groups, [{ path: 'data.status', op: 'eq', value: '' }]] });

  let number = 0;
  return (
    <div>
      <datalist id={listId}>{fieldSuggestions(eventType).map((f) => <option key={f} value={f} />)}</datalist>

      {state.groups.length === 0 && <p className="muted" style={{ marginTop: 0 }}>No conditions: every <span className="mono">{eventType}</span> event is delivered.</p>}

      {state.groups.map((group, g) => (
        <div key={g} role="group" aria-label={`Condition group ${g + 1}`}>
          {g > 0 && <div className="sm muted" style={{ margin: '6px 0' }}><b>OR</b> (any group matching is enough)</div>}
          {group.map((row, r) => {
            number += 1;
            const n = number;
            const error = rowError(row);
            return (
              <div key={`${g}-${r}`}>
                <div className="fr">
                  <span className="lg" aria-hidden="true">{r === 0 ? 'IF' : 'AND'}</span>
                  <input className={`input mono${error ? ' bad' : ''}`} list={listId} value={row.path} disabled={disabled} aria-label={`Field of condition ${n}`} aria-invalid={error !== null} onChange={(e) => update(g, r, { path: e.target.value })} />
                  <select className="select" value={row.op} disabled={disabled} aria-label={`Operator of condition ${n}`} onChange={(e) => update(g, r, { op: e.target.value })}>
                    {OPERATORS.map((o) => <option key={o.id} value={o.id} title={o.hint}>{o.label}</option>)}
                  </select>
                  {row.op === 'exists' ? (
                    <select className="select" value={row.value === 'false' ? 'false' : 'true'} disabled={disabled} aria-label={`Value of condition ${n}`} onChange={(e) => update(g, r, { value: e.target.value })}>
                      <option value="true">is present</option>
                      <option value="false">is absent</option>
                    </select>
                  ) : (
                    <input className={`input mono${error ? ' bad' : ''}`} value={row.value} disabled={disabled} aria-label={`Value of condition ${n}`} aria-invalid={error !== null} placeholder={row.op === 'in' || row.op === 'nin' ? 'USD, EUR' : ''} onChange={(e) => update(g, r, { value: e.target.value })} />
                  )}
                  <button type="button" className="btn icon gh" disabled={disabled} aria-label={`Remove condition ${n}`} onClick={() => remove(g, r)}><Icon name="x" /></button>
                </div>
                {error && <div className="error-text" role="alert">{error}</div>}
              </div>
            );
          })}
        </div>
      ))}

      <div className="row wrap" style={{ marginTop: 8 }}>
        <button type="button" className="btn sm" disabled={!canAdd} onClick={addToLast}><Icon name="plus" />Add condition</button>
        <button type="button" className="btn sm gh" disabled={!canAdd || state.groups.length === 0} onClick={addGroup}>+ Add OR group</button>
        <span className="muted sm">{total} of {MAX_CONDITIONS} conditions · AND within a group, OR between groups</span>
      </div>
    </div>
  );
}
