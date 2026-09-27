import { money, TUBES, type Requisition } from '../api'

export function RequisitionView({ requisition: r, onClose }: { requisition: Requisition; onClose: () => void }) {
  return (
    <section className="card">
      <div className="card-head">
        <div>
          <p className="eyebrow">Accession</p>
          <h2 className="accession">{r.accessionNumber}</h2>
          <p className="muted">{r.patientName} · {r.mrn} · ordered by {r.practitioner} · {r.priority}</p>
        </div>
        <button type="button" className="btn ghost" onClick={onClose}>Close</button>
      </div>

      <div className="grid-2">
        <div>
          <h3>Tubes to draw</h3>
          <ul className="tubes">
            {r.tubes.map(t => <li key={t}><span className={`tube tube-${t}`} aria-hidden />{TUBES[t] ?? t}</li>)}
          </ul>
        </div>
        <div>
          <h3>Billing</h3>
          <p>{r.payerType === 'OHIP' ? 'Billed to OHIP' : 'Self-pay patient'}</p>
          {r.patientPays > 0
            ? <p>Patient pays <strong>{money(r.patientPays)}</strong> · invoice #{r.invoiceId} (open)</p>
            : <p className="muted">Nothing to collect.</p>}
        </div>
      </div>

      <table className="table">
        <thead><tr><th>Test</th><th>Code</th><th>Coverage</th><th className="num">Price</th></tr></thead>
        <tbody>
          {r.items.map(i => (
            <tr key={i.testId}>
              <td>{i.name}</td>
              <td className="mono">{i.code}</td>
              <td>{i.isInsured ? <span className="badge ok">OHIP</span> : <span className="badge warn">Self-pay</span>}</td>
              <td className="num">{i.price != null ? money(i.price) : '-'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
