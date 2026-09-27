import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, torontoToday, type AppointmentRow, type Location, type PatientDetails, type PatientSummary, type Requisition } from '../api'
import { emptyPatient, PatientFields } from '../components/PatientFields'
import { RequisitionForm } from '../components/RequisitionForm'
import { RequisitionView } from '../components/RequisitionView'

type Panel =
  | { kind: 'none' }
  | { kind: 'walkin' }
  | { kind: 'form'; patient: PatientSummary; appointmentId: number | null }
  | { kind: 'view'; requisition: Requisition }

const STATUS_CLASS: Record<string, string> = { Booked: '', CheckedIn: 'info', Completed: 'ok', Cancelled: 'muted', NoShow: 'warn' }
const STATUS_LABEL: Record<string, string> = { CheckedIn: 'Checked in', NoShow: 'No-show' }

export function ReceptionPage({ canUse }: { canUse: boolean }) {
  const [locations, setLocations] = useState<Location[]>([])
  const [locationId, setLocationId] = useState<number | null>(null)
  const [date, setDate] = useState(torontoToday())
  const [rows, setRows] = useState<AppointmentRow[] | null>(null)
  const [panel, setPanel] = useState<Panel>({ kind: 'none' })
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    api.locations().then(ls => { setLocations(ls); if (ls.length) setLocationId(ls[0].locationId) })
  }, [])

  const load = useCallback(() => {
    if (locationId == null || !canUse) return
    setMessage(null)
    api.daySheet(locationId, date).then(setRows).catch(e => { setRows([]); setMessage(e.message) })
  }, [locationId, date, canUse])

  useEffect(load, [load])

  if (!canUse) {
    return (
      <section className="card narrow">
        <h1>Reception</h1>
        <p>Choose a staff user (Reception, Collector or Admin) in the top-right corner to open the front desk.</p>
      </section>
    )
  }

  async function act(fn: () => Promise<void>) {
    try { await fn(); load() } catch (e) { setMessage((e as Error).message) }
  }

  async function register(row: AppointmentRow) {
    try {
      const patient = await api.patient(row.patientId)
      setPanel({ kind: 'form', patient, appointmentId: row.appointmentId })
    } catch (e) { setMessage((e as Error).message) }
  }

  const counts = rows?.reduce<Record<string, number>>((c, r) => ({ ...c, [r.status]: (c[r.status] ?? 0) + 1 }), {}) ?? {}

  return (
    <div className="reception">
      <header className="page-head row">
        <div>
          <h1>Front desk</h1>
          <p className="muted">
            {rows ? `${rows.length} appointment${rows.length === 1 ? '' : 's'} · ${counts.CheckedIn ?? 0} waiting · ${counts.Completed ?? 0} done` : 'Loading…'}
          </p>
        </div>
        <div className="toolbar">
          <select value={locationId ?? ''} onChange={e => setLocationId(Number(e.target.value))} aria-label="Location">
            {locations.map(l => <option key={l.locationId} value={l.locationId}>{l.name}</option>)}
          </select>
          <input type="date" value={date} onChange={e => e.target.value && setDate(e.target.value)} aria-label="Date" />
          <button className="btn primary" onClick={() => setPanel({ kind: 'walkin' })}>+ Walk-in</button>
        </div>
      </header>

      {message && <div className="alert" role="alert">{message}</div>}

      {panel.kind === 'walkin' && (
        <WalkIn
          onPick={patient => setPanel({ kind: 'form', patient, appointmentId: null })}
          onCancel={() => setPanel({ kind: 'none' })}
        />
      )}
      {panel.kind === 'form' && locationId != null && (
        <RequisitionForm
          patient={panel.patient}
          locationId={locationId}
          appointmentId={panel.appointmentId}
          onCreated={r => { setPanel({ kind: 'view', requisition: r }); load() }}
          onCancel={() => setPanel({ kind: 'none' })}
        />
      )}
      {panel.kind === 'view' && <RequisitionView requisition={panel.requisition} onClose={() => setPanel({ kind: 'none' })} />}

      <section className="card flush">
        <table className="table daysheet">
          <thead>
            <tr><th>Time</th><th>Patient</th><th>MRN</th><th>DOB</th><th>Card</th><th>Status</th><th /></tr>
          </thead>
          <tbody>
            {rows?.length === 0 && <tr><td colSpan={7} className="empty">No appointments for this day.</td></tr>}
            {rows?.map(r => (
              <tr key={r.appointmentId} className={r.status === 'Cancelled' || r.status === 'NoShow' ? 'dim' : ''}>
                <td className="mono">{r.localTime}</td>
                <td><strong>{r.patientName}</strong></td>
                <td className="mono">{r.mrn}</td>
                <td>{r.dateOfBirth}</td>
                <td>{r.hasHealthCard ? <span className="badge ok">OHIP</span> : <span className="badge warn">Self-pay</span>}</td>
                <td><span className={`badge ${STATUS_CLASS[r.status] ?? ''}`}>{STATUS_LABEL[r.status] ?? r.status}</span></td>
                <td className="row-actions">
                  {r.status === 'Booked' && <>
                    <button className="btn small" aria-label={`Check in ${r.patientName}`} onClick={() => act(() => api.checkIn(r.appointmentId))}>Check in</button>
                    <button className="btn small ghost" aria-label={`Mark ${r.patientName} as no-show`} onClick={() => act(() => api.noShow(r.appointmentId))}>No-show</button>
                  </>}
                  {r.status === 'CheckedIn' && !r.requisitionId && (
                    <button className="btn small primary" aria-label={`Register requisition for ${r.patientName}`} onClick={() => register(r)}>Register requisition</button>
                  )}
                  {r.requisitionId && (
                    <button className="link mono" onClick={() => act(async () => setPanel({ kind: 'view', requisition: await api.requisition(r.requisitionId!) }))}>
                      {r.accessionNumber}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  )
}

function WalkIn({ onPick, onCancel }: { onPick: (p: PatientSummary) => void; onCancel: () => void }) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<PatientSummary[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [draft, setDraft] = useState<PatientDetails>(emptyPatient)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [message, setMessage] = useState<string | null>(null)

  async function search(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    try { setResults(await api.searchPatients(query)) } catch (err) { setMessage((err as Error).message) }
  }

  async function create(e: FormEvent) {
    e.preventDefault()
    setErrors({}); setMessage(null)
    try { onPick(await api.createPatient(draft)) } catch (err) {
      if (err instanceof ApiError) { setErrors(err.fieldErrors); setMessage(err.message) }
    }
  }

  return (
    <section className="card">
      <div className="card-head">
        <h2>Walk-in patient</h2>
        <button type="button" className="btn ghost" onClick={onCancel}>Close</button>
      </div>

      {!creating ? (
        <>
          <form className="search" onSubmit={search}>
            <input autoFocus value={query} onChange={e => setQuery(e.target.value)} placeholder="Last name (Last, First), health card number or MRN" />
            <button className="btn">Search</button>
          </form>
          {message && <div className="alert">{message}</div>}
          {results && (
            results.length === 0
              ? <p className="empty">No match. <button className="link" onClick={() => setCreating(true)}>Register a new patient</button></p>
              : (
                <ul className="results">
                  {results.map(p => (
                    <li key={p.patientId}>
                      <button onClick={() => onPick(p)}>
                        <strong>{p.lastName}, {p.firstName}</strong>
                        <span className="muted">{p.mrn} · DOB {p.dateOfBirth} · {p.healthCardNumber ? `HCN ${p.healthCardNumber}` : 'no health card'}</span>
                      </button>
                    </li>
                  ))}
                  <li><button className="link" onClick={() => setCreating(true)}>Not listed - register a new patient</button></li>
                </ul>
              )
          )}
        </>
      ) : (
        <form onSubmit={create}>
          <PatientFields value={draft} onChange={setDraft} errors={errors} />
          {message && <div className="alert">{message}</div>}
          <div className="actions">
            <button type="button" className="btn ghost" onClick={() => setCreating(false)}>Back to search</button>
            <button className="btn primary">Create patient</button>
          </div>
        </form>
      )}
    </section>
  )
}
