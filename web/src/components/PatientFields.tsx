import type { ReactNode } from 'react'
import type { PatientDetails } from '../api'

export const emptyPatient: PatientDetails = {
  firstName: '', lastName: '', dateOfBirth: '', sex: 'U',
  healthCardNumber: '', healthCardVersion: '', email: '', mobilePhone: '',
  preferredLanguage: 'en', consentEmailNotification: false,
}

/** Same Luhn check the API runs (Domain/HealthCard.cs), so typos show up before submit. */
export function isValidHealthCard(raw: string): boolean {
  const n = raw.replace(/[\s-]/g, '')
  if (!/^\d{10}$/.test(n)) return false
  let sum = 0
  for (let i = 0; i < 10; i++) {
    let d = Number(n[9 - i])
    if (i % 2 === 1) { d *= 2; if (d > 9) d -= 9 }
    sum += d
  }
  return sum % 10 === 0
}

export function Field({ label, error, hint, children }: { label: string; error?: string[]; hint?: string; children: ReactNode }) {
  return (
    <label className={`field${error?.length ? ' has-error' : ''}`}>
      <span className="field-label">{label}</span>
      {children}
      {error?.length ? <span className="field-error">{error.join(' ')}</span> : hint ? <span className="field-hint">{hint}</span> : null}
    </label>
  )
}

type Props = {
  value: PatientDetails
  onChange: (v: PatientDetails) => void
  errors: Record<string, string[]>
  /** Server error keys are prefixed ("patient.email") on the booking endpoint. */
  errorPrefix?: string
}

export function PatientFields({ value, onChange, errors, errorPrefix = '' }: Props) {
  const set = <K extends keyof PatientDetails>(key: K, v: PatientDetails[K]) => onChange({ ...value, [key]: v })
  const err = (key: string) => errors[`${errorPrefix}${key}`]
  const hcnTyped = value.healthCardNumber.trim() !== ''
  const hcnLocalError = hcnTyped && !isValidHealthCard(value.healthCardNumber) ? ['Check the number - the last digit doesn\'t match.'] : undefined

  return (
    <div className="grid-2">
      <Field label="First name" error={err('firstName')}>
        <input value={value.firstName} onChange={e => set('firstName', e.target.value)} autoComplete="given-name" required />
      </Field>
      <Field label="Last name" error={err('lastName')}>
        <input value={value.lastName} onChange={e => set('lastName', e.target.value)} autoComplete="family-name" required />
      </Field>
      <Field label="Date of birth" error={err('dateOfBirth')}>
        <input type="date" value={value.dateOfBirth} onChange={e => set('dateOfBirth', e.target.value)} required />
      </Field>
      <Field label="Sex" error={err('sex')} hint="Used to pick the right reference ranges.">
        <select value={value.sex} onChange={e => set('sex', e.target.value as PatientDetails['sex'])}>
          <option value="F">Female</option>
          <option value="M">Male</option>
          <option value="X">X / another</option>
          <option value="U">Prefer not to say</option>
        </select>
      </Field>
      <Field label="Ontario health card number" error={err('healthCardNumber') ?? hcnLocalError} hint="Optional. Without it, tests are self-pay.">
        <input value={value.healthCardNumber} onChange={e => set('healthCardNumber', e.target.value)} inputMode="numeric" placeholder="1234 567 897" />
      </Field>
      <Field label="Version code" error={err('healthCardVersion')}>
        <input value={value.healthCardVersion} onChange={e => set('healthCardVersion', e.target.value.toUpperCase())} maxLength={2} placeholder="AB" />
      </Field>
      <Field label="Email" error={err('email')}>
        <input type="email" value={value.email} onChange={e => set('email', e.target.value)} autoComplete="email" />
      </Field>
      <Field label="Mobile phone" error={err('mobilePhone')}>
        <input type="tel" value={value.mobilePhone} onChange={e => set('mobilePhone', e.target.value)} autoComplete="tel" />
      </Field>
      <Field label="Preferred language" error={err('preferredLanguage')}>
        <select value={value.preferredLanguage} onChange={e => set('preferredLanguage', e.target.value as 'en' | 'fr')}>
          <option value="en">English</option>
          <option value="fr">Français</option>
        </select>
      </Field>
      <label className="check">
        <input type="checkbox" checked={value.consentEmailNotification} onChange={e => set('consentEmailNotification', e.target.checked)} />
        <span>Email me about this visit and when my results are ready. Emails never contain results.</span>
      </label>
    </div>
  )
}
