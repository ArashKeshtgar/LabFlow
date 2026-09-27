import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError, money, torontoToday, type LabTest, type PatientSummary, type Practitioner, type Requisition } from '../api'
import { Field } from './PatientFields'

type Props = {
  patient: PatientSummary
  locationId: number
  appointmentId: number | null
  onCreated: (r: Requisition) => void
  onCancel: () => void
}

export function RequisitionForm({ patient, locationId, appointmentId, onCreated, onCancel }: Props) {
  const [tests, setTests] = useState<LabTest[]>([])
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [practitionerQuery, setPractitionerQuery] = useState('')
  const [practitioners, setPractitioners] = useState<Practitioner[]>([])
  const [practitioner, setPractitioner] = useState<Practitioner | null>(null)
  const [requisitionDate, setRequisitionDate] = useState(torontoToday())
  const [priority, setPriority] = useState<'Routine' | 'Stat'>('Routine')
  const [fastingHours, setFastingHours] = useState('')
  const [isPregnant, setIsPregnant] = useState(false)
  const [pregnancyWeek, setPregnancyWeek] = useState('')
  const [notes, setNotes] = useState('')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const hasOhip = !!patient.healthCardNumber

  useEffect(() => { api.tests().then(setTests).catch(e => setMessage(e.message)) }, [])

  useEffect(() => {
    if (practitioner || practitionerQuery.trim().length < 2) { setPractitioners([]); return }
    const t = setTimeout(() => api.searchPractitioners(practitionerQuery).then(setPractitioners).catch(() => setPractitioners([])), 250)
    return () => clearTimeout(t)
  }, [practitionerQuery, practitioner])

  const byDepartment = useMemo(() => {
    const groups = new Map<string, LabTest[]>()
    for (const t of tests) groups.set(t.department, [...(groups.get(t.department) ?? []), t])
    return [...groups.entries()]
  }, [tests])

  // Tests already covered by a selected panel, keyed by member name -> panel name
  const coveredBy = useMemo(() => {
    const m = new Map<string, string>()
    for (const t of tests) if (t.isPanel && selected.has(t.testId)) t.members.forEach(n => m.set(n, t.name))
    return m
  }, [tests, selected])

  const chosen = tests.filter(t => selected.has(t.testId))
  const selfPay = chosen.filter(t => !hasOhip || !t.isOhipInsured)
  const patientPays = selfPay.reduce((sum, t) => sum + (t.uninsuredPrice ?? 0), 0)
  const needsFasting = chosen.some(t => t.fastingRequired)

  function toggle(t: LabTest) {
    const next = new Set(selected)
    if (next.has(t.testId)) next.delete(t.testId)
    else {
      next.add(t.testId)
      // Selecting a panel drops members that were ticked on their own.
      if (t.isPanel) tests.filter(m => t.members.includes(m.name)).forEach(m => next.delete(m.testId))
    }
    setSelected(next)
  }

  function unavailableReason(t: LabTest): string | null {
    if (t.applicableSex && t.applicableSex !== patient.sex) return `Only for sex ${t.applicableSex}`
    if (!hasOhip && t.uninsuredPrice == null) return 'Needs an Ontario health card'
    const panel = coveredBy.get(t.name)
    if (panel && !selected.has(t.testId)) return `Included in ${panel}`
    return null
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!practitioner) { setErrors({ orderingPractitionerId: ['Choose the ordering practitioner.'] }); return }
    setBusy(true); setErrors({}); setMessage(null)
    try {
      onCreated(await api.createRequisition({
        patientId: patient.patientId,
        orderingPractitionerId: practitioner.practitionerId,
        locationId,
        appointmentId,
        requisitionDate,
        priority,
        clinicalNotes: notes.trim() || null,
        isPregnant,
        pregnancyWeek: isPregnant && pregnancyWeek ? Number(pregnancyWeek) : null,
        fastingHours: fastingHours === '' ? null : Number(fastingHours),
        testIds: [...selected],
      }))
    } catch (err) {
      if (err instanceof ApiError) { setErrors(err.fieldErrors); setMessage(err.message) }
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card requisition-form" onSubmit={submit}>
      <div className="card-head">
        <div>
          <h2>New requisition</h2>
          <p className="muted">
            {patient.lastName}, {patient.firstName} · {patient.mrn} · DOB {patient.dateOfBirth} ·{' '}
            {hasOhip ? <span className="badge ok">OHIP {patient.healthCardNumber} {patient.healthCardVersion}</span> : <span className="badge warn">No health card - self-pay</span>}
          </p>
        </div>
        <button type="button" className="btn ghost" onClick={onCancel}>Close</button>
      </div>

      <div className="grid-2">
        <Field label="Ordering practitioner" error={errors.orderingPractitionerId}>
          {practitioner ? (
            <div className="picked">
              <span>{practitioner.name} <span className="muted">· CPSO {practitioner.licenceNumber}{practitioner.clinicName ? ` · ${practitioner.clinicName}` : ''}</span></span>
              <button type="button" className="link" onClick={() => { setPractitioner(null); setPractitionerQuery('') }}>change</button>
            </div>
          ) : (
            <div className="typeahead">
              <input value={practitionerQuery} onChange={e => setPractitionerQuery(e.target.value)} placeholder="Name, CPSO or OHIP billing number" />
              {practitioners.length > 0 && (
                <ul className="menu">
                  {practitioners.map(p => (
                    <li key={p.practitionerId}>
                      <button type="button" onClick={() => setPractitioner(p)}>
                        <strong>{p.name}</strong> <span className="muted">CPSO {p.licenceNumber} · {p.clinicName ?? p.city}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </Field>
        <Field label="Date on requisition" error={errors.requisitionDate}>
          <input type="date" value={requisitionDate} max={torontoToday()} onChange={e => setRequisitionDate(e.target.value)} />
        </Field>
        <Field label="Priority" error={errors.priority}>
          <div className="segmented">
            {(['Routine', 'Stat'] as const).map(p => (
              <button type="button" key={p} className={priority === p ? 'on' : ''} onClick={() => setPriority(p)}>{p}</button>
            ))}
          </div>
        </Field>
        <Field label="Hours since last meal" error={errors.fastingHours} hint={needsFasting ? 'A selected test needs 8-12 hours of fasting.' : undefined}>
          <input type="number" min={0} max={72} value={fastingHours} onChange={e => setFastingHours(e.target.value)} />
        </Field>
        {patient.sex !== 'M' && (
          <Field label="Pregnant" error={errors.isPregnant ?? errors.pregnancyWeek}>
            <div className="inline">
              <label className="check"><input type="checkbox" checked={isPregnant} onChange={e => setIsPregnant(e.target.checked)} /> <span>Yes</span></label>
              {isPregnant && <input type="number" min={1} max={45} placeholder="week" value={pregnancyWeek} onChange={e => setPregnancyWeek(e.target.value)} className="short" />}
            </div>
          </Field>
        )}
        <Field label="Clinical information" error={errors.clinicalNotes}>
          <textarea rows={2} maxLength={1000} value={notes} onChange={e => setNotes(e.target.value)} />
        </Field>
      </div>

      <h3>Tests</h3>
      {errors.testIds && <div className="alert">{errors.testIds.map(m => <div key={m}>{m}</div>)}</div>}
      <div className="test-groups">
        {byDepartment.map(([dept, list]) => (
          <fieldset key={dept} className="test-group">
            <legend>{dept}</legend>
            {list.map(t => {
              const reason = unavailableReason(t)
              const checked = selected.has(t.testId)
              return (
                <label key={t.testId} className={`test${reason && !checked ? ' off' : ''}`} title={t.preparation ?? undefined}>
                  <input type="checkbox" checked={checked} disabled={!!reason && !checked} onChange={() => toggle(t)} />
                  <span className="test-name">
                    {t.name}
                    {t.isPanel && <span className="test-sub">{t.members.join(', ')}</span>}
                    {reason && !checked && <span className="test-sub">{reason}</span>}
                  </span>
                  <span className="test-tags">
                    {t.fastingRequired && <span className="badge">Fasting</span>}
                    {(!t.isOhipInsured || !hasOhip) && t.uninsuredPrice != null && <span className="badge warn">{money(t.uninsuredPrice)}</span>}
                  </span>
                </label>
              )
            })}
          </fieldset>
        ))}
      </div>

      {message && !errors.testIds && <div className="alert" role="alert">{message}</div>}

      <div className="actions sticky">
        <div className="total">
          {chosen.length} test{chosen.length === 1 ? '' : 's'} ·{' '}
          {patientPays > 0 ? <>Patient pays <strong>{money(patientPays)}</strong></> : <>No charge to patient</>}
        </div>
        <button className="btn primary" disabled={busy || chosen.length === 0}>{busy ? 'Saving…' : 'Register requisition'}</button>
      </div>
    </form>
  )
}
