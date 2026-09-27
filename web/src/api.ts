// Typed client for LabFlow.Api. Shapes mirror the C# records in src/LabFlow.Api/Endpoints.

export type Location = { locationId: number; name: string; address: string; city: string; postalCode: string; phone: string | null }

export type LabTest = {
  testId: number; code: string; name: string; nameFr: string | null; department: string; isPanel: boolean
  members: string[]; isOhipInsured: boolean; uninsuredPrice: number | null; fastingRequired: boolean
  preparation: string | null; applicableSex: 'M' | 'F' | null; specimenTypeCode: string | null
}

export type Slot = { startUtc: string; localTime: string; remaining: number }

export type Sex = 'M' | 'F' | 'X' | 'U'

export type PatientDetails = {
  firstName: string; lastName: string; dateOfBirth: string; sex: Sex
  healthCardNumber: string; healthCardVersion: string; email: string; mobilePhone: string
  preferredLanguage: 'en' | 'fr'; consentEmailNotification: boolean
}

export type BookingConfirmation = { appointmentId: number; location: string; startUtc: string; localStart: string; confirmationSentTo: string | null }

export type AppointmentRow = {
  appointmentId: number; startUtc: string; localTime: string; status: string
  patientId: number; patientName: string; mrn: string; dateOfBirth: string; hasHealthCard: boolean
  requisitionId: number | null; accessionNumber: string | null
}

export type PatientSummary = {
  patientId: number; mrn: string; firstName: string; lastName: string; dateOfBirth: string; sex: Sex
  healthCardNumber: string | null; healthCardVersion: string | null; email: string | null; mobilePhone: string | null
}

export type Practitioner = { practitionerId: number; name: string; licenceNumber: string; ohipBillingNumber: string | null; clinicName: string | null; city: string | null }

export type CreateRequisition = {
  patientId: number; orderingPractitionerId: number; locationId: number; appointmentId: number | null
  requisitionDate: string; priority: 'Routine' | 'Stat'; clinicalNotes: string | null
  isPregnant: boolean; pregnancyWeek: number | null; fastingHours: number | null; testIds: number[]
}

export type Requisition = {
  requisitionId: number; accessionNumber: string; patientId: number; patientName: string; mrn: string
  practitioner: string; payerType: string; priority: string; status: string; receivedAt: string
  items: { testId: number; code: string; name: string; isInsured: boolean; price: number | null; fastingRequired: boolean }[]
  tubes: string[]; patientPays: number; invoiceId: number | null
}

export type DemoUser = { externalId: string; displayName: string; role: string }

/** Thrown for any non-2xx response. `fieldErrors` is filled for 400 validation problems. */
export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}) {
    super(message)
  }
}

const USER_KEY = 'labflow.demoUser'

export function getDemoUser(): string {
  try { return localStorage.getItem(USER_KEY) ?? '' } catch { return '' }
}

export function setDemoUser(externalId: string) {
  try { localStorage.setItem(USER_KEY, externalId) } catch { /* private mode: in-memory only */ }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  const user = getDemoUser()
  if (user) headers['X-Demo-User'] = user
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const res = await fetch(`/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 204) return undefined as T

  const data = await res.json().catch(() => null)
  if (!res.ok) {
    const message = data?.error ?? data?.title ?? (res.status === 401 ? 'Please choose a staff user.' : res.status === 403 ? 'This user is not allowed to do that.' : `Request failed (${res.status})`)
    throw new ApiError(res.status, message, data?.errors ?? {})
  }
  return data as T
}

const q = (params: Record<string, string | number>) => new URLSearchParams(Object.entries(params).map(([k, v]) => [k, String(v)])).toString()

export const api = {
  devUsers: () => request<DemoUser[]>('GET', '/dev/users'),
  locations: () => request<Location[]>('GET', '/locations'),
  tests: () => request<LabTest[]>('GET', '/tests'),
  slots: (locationId: number, date: string) => request<Slot[]>('GET', `/locations/${locationId}/slots?${q({ date })}`),
  book: (body: { locationId: number; startUtc: string; patient: PatientDetails; notes: string | null }) =>
    request<BookingConfirmation>('POST', '/appointments', body),

  daySheet: (locationId: number, date: string) => request<AppointmentRow[]>('GET', `/reception/appointments?${q({ locationId, date })}`),
  checkIn: (id: number) => request<void>('POST', `/reception/appointments/${id}/check-in`),
  noShow: (id: number) => request<void>('POST', `/reception/appointments/${id}/no-show`),
  searchPatients: (text: string) => request<PatientSummary[]>('GET', `/reception/patients?${q({ q: text })}`),
  patient: (id: number) => request<PatientSummary>('GET', `/reception/patients/${id}`),
  createPatient: (body: PatientDetails) => request<PatientSummary>('POST', '/reception/patients', body),
  searchPractitioners: (text: string) => request<Practitioner[]>('GET', `/reception/practitioners?${q({ q: text })}`),
  createRequisition: (body: CreateRequisition) => request<Requisition>('POST', '/reception/requisitions', body),
  requisition: (id: number) => request<Requisition>('GET', `/reception/requisitions/${id}`),
}

/** Today's date in Toronto as yyyy-mm-dd, whatever the browser's time zone. */
export function torontoToday(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Toronto' }).format(new Date())
}

export const money = (n: number) => new Intl.NumberFormat('en-CA', { style: 'currency', currency: 'CAD' }).format(n)

export const TUBES: Record<string, string> = {
  SER: 'Gold top (SST) - serum',
  PLAS: 'Light green top - plasma',
  BLD: 'Lavender top (EDTA) - whole blood',
  UR: 'Sterile urine container',
}
