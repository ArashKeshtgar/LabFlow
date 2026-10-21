import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, askWhy, isRestricted, torontoToday, type AppointmentRow, type ElectronicOrder, type Location, type PatientDetails, type PatientSummary, type Requisition } from '../api'
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

export function ReceptionPage({ canUse, connected = false }: { canUse: boolean; connected?: boolean }) {
  const [locations, setLocations] = useState<Location[] | null>(null)
  const [locationId, setLocationId] = useState<number | null>(null)
  const [date, setDate] = useState(torontoToday())
  const [rows, setRows] = useState<AppointmentRow[] | null>(null)
  const [panel, setPanel] = useState<Panel>({ kind: 'none' })
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    api.locations().then(ls => { setLocations(ls); if (ls.length) setLocationId(ls[0].locationId) }).catch(() => setLocations([]))
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
        {connected
          ? <p>Sign in with your clinic's ClinicHub account; a role with LabFlow front-desk access opens the reception.{' '}
              <a href={`/api/auth/sso/start?returnUrl=${encodeURIComponent('/reception')}`}>Staff sign-in with ClinicHub</a></p>
          : <p>Choose a staff user (Reception, Collector or Admin) in the top-right corner to open the front desk.</p>}
      </section>
    )
  }

  // A clinic's own LabFlow database starts without collection sites; the day sheet needs one.
  if (locations?.length === 0) {
    return (
      <section className="card narrow">
        <h1>Reception</h1>
        <p>This lab has no collection sites yet, so there is no day sheet to show. Add the clinic's collection site
          (address, hours, capacity) to <code>core.Location</code> and its opening hours, then reload.</p>
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
            {(locations ?? []).map(l => <option key={l.locationId} value={l.locationId}>{l.name}</option>)}
          </select>
          <input type="date" value={date} onChange={e => e.target.value && setDate(e.target.value)} aria-label="Date" />
          <button className="btn primary" onClick={() => setPanel({ kind: 'walkin' })}>+ Walk-in</button>
        </div>
      </header>

      {message && <div className="alert" role="alert">{message}</div>}

      {connected && locationId != null && (
        <ElectronicOrders locationId={locationId} onRequisition={r => { setPanel({ kind: 'view', requisition: r }); load() }} onError={setMessage} />
      )}

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

/**
 * Orders sent by clinics through ClinicHub. When the patient arrives, one click makes the order a requisition at this
 * collection site, with the same checks as a requisition typed at the desk.
 */
function ElectronicOrders({ locationId, onRequisition, onError }: {
  locationId: number; onRequisition: (r: Requisition) => void; onError: (m: string) => void
}) {
  const [orders, setOrders] = useState<ElectronicOrder[] | null>(null)
  const [q, setQ] = useState('')
  const load = useCallback(() => { api.electronicOrders().then(setOrders).catch(e => onError((e as Error).message)) }, [onError])
  useEffect(() => { load(); const t = setInterval(load, 30_000); return () => clearInterval(t) }, [load])

  if (!orders || orders.length === 0) return null
  const shown = orders.filter(o => !q || `${o.patient} ${o.mrn}`.toLowerCase().includes(q.toLowerCase()))
  return (
    <section className="card">
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <h2>Electronic orders <span className="muted">({orders.length} waiting)</span></h2>
        <input type="search" placeholder="Find patient" value={q} onChange={e => setQ(e.target.value)} aria-label="Find a patient's order" />
      </div>
      <table className="table">
        <thead><tr><th>Received</th><th>Patient</th><th>Tests</th><th>Ordered by</th><th>Notes</th><th></th></tr></thead>
        <tbody>
          {shown.map(o => (
            <tr key={o.electronicOrderId}>
              <td className="muted">{new Date(o.receivedAt).toLocaleString('en-CA', { dateStyle: 'medium', timeStyle: 'short' })}
                {o.priority === 'Stat' && <strong className="badge">STAT</strong>}</td>
              <td>{o.patient}{o.confidentiality && (
                <strong className="badge" title="The patient has a consent directive at the clinic: share these results only with those caring for them">
                  {o.confidentiality === 'V' ? 'Very restricted' : 'Restricted'}</strong>)}
                <div className="muted mono">{o.mrn} · born {o.dateOfBirth}</div></td>
              <td>{o.tests.join(', ')}</td>
              <td>{o.practitioner}<div className="muted">{o.sourceClinic}</div></td>
              <td className="muted">{o.clinicalNotes}</td>
              <td><button className="btn small primary" onClick={async () => {
                try {
                  try { onRequisition(await api.requisitionFromOrder(o.electronicOrderId, locationId)) }
                  catch (e) {
                    if (!isRestricted(e)) throw e
                    const why = askWhy()
                    if (!why) return
                    await api.overrideOrder(o.electronicOrderId, why)
                    onRequisition(await api.requisitionFromOrder(o.electronicOrderId, locationId))
                  }
                  load()
                } catch (e) { onError((e as Error).message) }
              }}>Patient is here: make requisition</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
