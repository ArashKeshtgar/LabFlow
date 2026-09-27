import { useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, torontoToday, type BookingConfirmation, type Location, type PatientDetails, type Slot } from '../api'
import { emptyPatient, isValidHealthCard, PatientFields } from '../components/PatientFields'

function addDays(iso: string, days: number) {
  const d = new Date(`${iso}T12:00:00`)
  d.setDate(d.getDate() + days)
  return d.toISOString().slice(0, 10)
}

const niceDate = (iso: string) =>
  new Date(`${iso}T12:00:00`).toLocaleDateString('en-CA', { weekday: 'long', month: 'long', day: 'numeric' })

export function BookingPage() {
  const today = torontoToday()
  const [locations, setLocations] = useState<Location[]>([])
  const [locationId, setLocationId] = useState<number | null>(null)
  const [date, setDate] = useState(today)
  const [slots, setSlots] = useState<Slot[] | null>(null)
  const [slot, setSlot] = useState<Slot | null>(null)
  const [patient, setPatient] = useState<PatientDetails>(emptyPatient)
  const [notes, setNotes] = useState('')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<BookingConfirmation | null>(null)

  useEffect(() => {
    api.locations().then(ls => {
      setLocations(ls)
      if (ls.length) setLocationId(ls[0].locationId)
    }).catch(e => setMessage(e.message))
  }, [])

  useEffect(() => {
    if (locationId == null) return
    let live = true
    setSlots(null)
    setSlot(null)
    api.slots(locationId, date).then(s => live && setSlots(s)).catch(e => live && setMessage(e.message))
    return () => { live = false }
  }, [locationId, date])

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!slot || locationId == null) return
    if (patient.healthCardNumber.trim() && !isValidHealthCard(patient.healthCardNumber)) return
    setBusy(true)
    setErrors({})
    setMessage(null)
    try {
      setDone(await api.book({ locationId, startUtc: slot.startUtc, patient, notes: notes.trim() || null }))
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(err.fieldErrors)
        setMessage(err.message)
        if (err.status === 409) {
          // Slot was taken meanwhile: refresh the list so the user picks another.
          setSlot(null)
          setSlots(await api.slots(locationId, date))
        }
      }
    } finally {
      setBusy(false)
    }
  }

  if (done) {
    return (
      <section className="card narrow confirm">
        <div className="confirm-icon" aria-hidden>✓</div>
        <h1>You're booked</h1>
        <p className="lead">{done.localStart}</p>
        <p>{done.location}</p>
        <p className="muted">
          {done.confirmationSentTo
            ? <>A confirmation was sent to {done.confirmationSentTo}.</>
            : <>Keep this page - confirmation number <strong>#{done.appointmentId}</strong>.</>}
        </p>
        <p className="muted">Bring your health card and the paper requisition from your doctor.</p>
        <button className="btn" onClick={() => { setDone(null); setSlot(null); setPatient(emptyPatient); setNotes(''); setDate(today) }}>
          Book another visit
        </button>
      </section>
    )
  }

  return (
    <form className="booking" onSubmit={submit}>
      <header className="page-head">
        <h1>Book a lab visit</h1>
        <p className="muted">Pick a time at a patient service centre, then tell us who is coming.</p>
      </header>

      <section className="card">
        <h2><span className="step">1</span> Where and when</h2>
        <div className="grid-2">
          <label className="field">
            <span className="field-label">Location</span>
            <select value={locationId ?? ''} onChange={e => setLocationId(Number(e.target.value))}>
              {locations.map(l => <option key={l.locationId} value={l.locationId}>{l.name} - {l.address}, {l.city}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">Date</span>
            <div className="date-nav">
              <button type="button" className="btn ghost" disabled={date <= today} onClick={() => setDate(addDays(date, -1))} aria-label="Previous day">‹</button>
              <input type="date" value={date} min={today} onChange={e => e.target.value && setDate(e.target.value)} />
              <button type="button" className="btn ghost" onClick={() => setDate(addDays(date, 1))} aria-label="Next day">›</button>
            </div>
          </label>
        </div>

        <p className="slot-day">{niceDate(date)}</p>
        {slots == null ? <p className="muted">Loading times…</p>
          : slots.length === 0 ? <p className="empty">No times available on this day. Try the next day.</p>
          : (
            <div className="slots" role="radiogroup" aria-label="Available times">
              {slots.map(s => (
                <button
                  type="button" key={s.startUtc} role="radio" aria-checked={slot?.startUtc === s.startUtc}
                  className={`slot${slot?.startUtc === s.startUtc ? ' selected' : ''}`}
                  onClick={() => setSlot(s)}
                  aria-label={`${s.localTime}, ${s.remaining} place${s.remaining === 1 ? '' : 's'} left`}
                >
                  {s.localTime}
                  {s.remaining === 1 && <span className="slot-last">last spot</span>}
                </button>
              ))}
            </div>
          )}
      </section>

      <section className={`card${slot ? '' : ' disabled'}`} aria-disabled={!slot}>
        <h2><span className="step">2</span> Who is coming</h2>
        <fieldset disabled={!slot}>
          <PatientFields value={patient} onChange={setPatient} errors={errors} errorPrefix="patient." />
          <label className="field">
            <span className="field-label">Anything we should know? (optional)</span>
            <textarea rows={2} maxLength={500} value={notes} onChange={e => setNotes(e.target.value)} placeholder="e.g. needs a wheelchair-accessible room" />
          </label>
        </fieldset>
      </section>

      {message && <div className="alert" role="alert">{message}</div>}

      <div className="actions">
        <button className="btn primary" disabled={!slot || busy}>
          {busy ? 'Booking…' : slot ? `Book ${slot.localTime} on ${niceDate(date)}` : 'Choose a time first'}
        </button>
      </div>
    </form>
  )
}
