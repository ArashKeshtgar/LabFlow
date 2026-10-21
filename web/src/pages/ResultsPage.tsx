import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, isRestricted, type LabRequisition } from '../api'
import { RestrictedGate } from '../components/RestrictedGate'

/**
 * Resulting: pick a requisition, enter each analyte, save (flags come from the reference range for the patient's
 * sex and age), release. A requisition ordered from a clinic through ClinicHub sends its results back on release.
 */
export function ResultsPage({ canUse }: { canUse: boolean }) {
  const [list, setList] = useState<LabRequisition[] | null>(null)
  const [current, setCurrent] = useState<LabRequisition | null>(null)
  const [values, setValues] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  /** A restricted requisition the user tried to open: the gate asks why, logs it, and fetches the real sheet. */
  const [closed, setClosed] = useState<LabRequisition | null>(null)

  const load = useCallback(() => { if (canUse) api.labWorklist().then(setList).catch(e => setMessage({ ok: false, text: (e as Error).message })) }, [canUse])
  useEffect(load, [load])

  /** A restricted requisition is listed with its names hidden: opening it goes through the gate. */
  const openChecked = async (r: LabRequisition) => {
    setClosed(null)
    if (!r.confidentiality || r.patient !== 'Restricted') return open(r)
    try {
      open(await api.labRequisition(r.requisitionId))
      load()
    } catch (e) {
      if (isRestricted(e)) setClosed(r)
      else setMessage({ ok: false, text: (e as Error).message })
    }
  }

  const open = (r: LabRequisition) => {
    setCurrent(r)
    setErrors({})
    setMessage(null)
    const v: Record<string, string> = {}
    r.items.forEach(i => i.analytes.forEach(a => { v[`${i.requisitionItemId}:${a.testId}`] = a.value ?? '' }))
    setValues(v)
  }

  if (!canUse)
    return <section className="card narrow"><h1>Results</h1><p>Sign in as lab staff with result entry (technologist, pathologist or admin).</p></section>

  const save = async () => {
    if (!current) return
    setErrors({})
    try {
      const saved = await api.saveResults(current.requisitionId, Object.entries(values).map(([k, value]) => {
        const [item, test] = k.split(':').map(Number)
        return { requisitionItemId: item, testId: test, value: value.trim() || null }
      }))
      open(saved)
      setMessage({ ok: true, text: 'Saved. Release when every result is in.' })
      load()
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length) setErrors(e.fieldErrors)
      else setMessage({ ok: false, text: (e as Error).message })
    }
  }

  const release = async () => {
    if (!current) return
    try {
      const released = await api.releaseResults(current.requisitionId)
      open(released)
      setMessage({ ok: true, text: released.fromClinic
        ? `Released (version ${released.reportVersion}). The results are on their way back to ${released.fromClinic} through ClinicHub.`
        : `Released (version ${released.reportVersion}).` })
      load()
    } catch (e) {
      setMessage({ ok: false, text: (e as Error).message })
    }
  }

  return (
    <div className="results-page">
      <section className="card">
        <h2>Requisitions</h2>
        {list?.length === 0 && <p className="muted">Nothing to result.</p>}
        <ul className="req-list">
          {list?.map(r => (
            <li key={r.requisitionId}>
              <button className={current?.requisitionId === r.requisitionId ? 'on' : ''} onClick={() => openChecked(r)}>
                <span className="mono">{r.accessionNumber}</span> {r.priority === 'Stat' && <strong className="badge">STAT</strong>}
                <span>{r.patient}</span>
                <span className="muted">{r.status}{r.fromClinic ? ` · from ${r.fromClinic}` : ''}</span>
              </button>
            </li>
          ))}
        </ul>
      </section>

      {closed && (
        <RestrictedGate
          label={closed.confidentiality ?? 'R'}
          action="Open this requisition"
          onCancel={() => setClosed(null)}
          onOpen={async reason => {
            await api.overrideRequisition(closed.requisitionId, reason)
            open(await api.labRequisition(closed.requisitionId))
            setClosed(null)
            load()
          }}
        />
      )}

      {current && (
        <section className="card">
          <h2>{current.patient} <span className="muted mono">{current.mrn}</span></h2>
          <p className="muted">{current.sex} · {current.ageYears} y · {current.practitioner}{current.clinicalNotes ? ` · ${current.clinicalNotes}` : ''}</p>
          {current.items.map(item => (
            <fieldset key={item.requisitionItemId} className="result-group">
              <legend>{item.name} <span className="muted">({item.status})</span></legend>
              <table className="table">
                <thead><tr><th>Test</th><th>Result</th><th>Unit</th><th>Flag</th><th>Reference</th></tr></thead>
                <tbody>
                  {item.analytes.map(a => {
                    const key = `${item.requisitionItemId}:${a.testId}`
                    return (
                      <tr key={key} className={a.flag && a.flag !== 'N' ? 'abnormal' : ''}>
                        <td>{a.name}</td>
                        <td>
                          <input value={values[key] ?? ''} onChange={e => setValues({ ...values, [key]: e.target.value })}
                            inputMode={a.resultType === 'Numeric' ? 'decimal' : 'text'} aria-label={a.name} style={{ maxWidth: 120 }} />
                          {errors[key] && <div className="error">{errors[key][0]}</div>}
                        </td>
                        <td className="muted">{a.unit}</td>
                        <td><strong>{a.flag && a.flag !== 'N' ? a.flag : ''}</strong></td>
                        <td className="muted">{a.range}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </fieldset>
          ))}
          {message && <div className={message.ok ? 'notice' : 'alert'} role="status">{message.text}</div>}
          <div className="toolbar">
            <button className="btn" onClick={save}>Save</button>
            <button className="btn primary" onClick={release}>{current.reportVersion > 0 ? 'Release correction' : 'Release'}</button>
          </div>
        </section>
      )}
    </div>
  )
}
