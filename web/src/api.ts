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

/** A requisition on the resulting worklist, with each ordered test broken into the analytes that are resulted. */
export type LabRequisition = {
  requisitionId: number; accessionNumber: string; patient: string; mrn: string; sex: string; ageYears: number; priority: 'Routine' | 'Stat'
  status: string; receivedAt: string; clinicalNotes: string | null; practitioner: string; fromClinic: string | null; reportVersion: number
  /** The clinic's confidentiality label; the row is hidden (patient "Restricted") until the user has opened it. */
  confidentiality?: 'R' | 'V' | null
  items: {
    requisitionItemId: number; code: string; name: string; status: string
    analytes: { testId: number; code: string; name: string; resultType: string; unit: string | null; value: string | null; flag: string | null; range: string | null; status: string | null }[]
  }[]
}

/** An order sent by a clinic through ClinicHub, waiting for the patient to come in (connected mode). */
export type ElectronicOrder = {
  electronicOrderId: number; sourceClinic: string; patientId: number; patient: string; mrn: string; dateOfBirth: string
  practitioner: string; priority: 'Routine' | 'Stat'; clinicalNotes: string | null; receivedAt: string; tests: string[]
  /** The clinic's confidentiality label (a consent directive): R restricted, V very restricted. */
  confidentiality: 'R' | 'V' | null
}

export type Requisition = {
  requisitionId: number; accessionNumber: string; patientId: number; patientName: string; mrn: string
  practitioner: string; payerType: string; priority: string; status: string; receivedAt: string
  items: { testId: number; code: string; name: string; isInsured: boolean; price: number | null; fastingRequired: boolean }[]
  tubes: string[]; patientPays: number; invoiceId: number | null
}

export type DemoUser = { externalId: string; displayName: string; role: string }

/** "clinichub": staff sign in through ClinicHub; "demo": the development user menu. */
export type AuthSettings = { signIn: 'clinichub' | 'demo'; perClinicDatabase: boolean }
export type Me = { userId: number | null; name: string | null; role: string | null; permissions: string[]; clinic: string | null }

const CLINIC_KEY = 'labflow.clinic'

/**
 * With a database per clinic, public booking names the clinic: a link like /?clinic=harbourview, remembered
 * for the rest of the visit. Signed-in staff need none: their clinic comes from ClinicHub.
 */
export function bookingClinic(): string {
  const fromLink = new URLSearchParams(window.location.search).get('clinic')
  try {
    if (fromLink) sessionStorage.setItem(CLINIC_KEY, fromLink)
    return fromLink ?? sessionStorage.getItem(CLINIC_KEY) ?? ''
  } catch {
    return fromLink ?? ''
  }
}

/** Thrown for any non-2xx response. `fieldErrors` is filled for 400 validation problems. */
export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}, public data: Record<string, unknown> = {}) {
    super(message)
  }
}

/** The 403 a restricted item answers with until the user has said why they need it (a consent directive at the clinic). */
export const isRestricted = (e: unknown): e is ApiError => e instanceof ApiError && e.status === 403 && e.data.code === 'restricted'

const USER_KEY = 'labflow.demoUser'

export function getDemoUser(): string {
  try { return localStorage.getItem(USER_KEY) ?? '' } catch { return '' }
}

export function setDemoUser(externalId: string) {
  try { localStorage.setItem(USER_KEY, externalId) } catch { /* private mode: in-memory only */ }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  // X-LabFlow: required on changes when signed in with a cookie (connected to ClinicHub); harmless otherwise.
  const headers: Record<string, string> = { 'X-LabFlow': '1' }
  const user = getDemoUser()
  if (user) headers['X-Demo-User'] = user
  const clinic = bookingClinic()
  if (clinic) headers['X-LabFlow-Clinic'] = clinic
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const res = await fetch(`/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 204) return undefined as T

  const data = await res.json().catch(() => null)
  if (!res.ok) {
    const message = data?.error ?? data?.title ?? (res.status === 401 ? 'Please choose a staff user.' : res.status === 403 ? 'This user is not allowed to do that.' : `Request failed (${res.status})`)
    throw new ApiError(res.status, message, data?.errors ?? {}, data ?? {})
  }
  return data as T
}

const q = (params: Record<string, string | number>) => new URLSearchParams(Object.entries(params).map(([k, v]) => [k, String(v)])).toString()

export const api = {
  devUsers: () => request<DemoUser[]>('GET', '/dev/users'),
  authSettings: () => request<AuthSettings>('GET', '/auth/settings'),
  me: () => request<Me>('GET', '/me'),
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
  electronicOrders: () => request<ElectronicOrder[]>('GET', '/reception/electronic-orders'),
  labWorklist: () => request<LabRequisition[]>('GET', '/lab/worklist'),
  saveResults: (id: number, results: { requisitionItemId: number; testId: number; value: string | null }[]) =>
    request<LabRequisition>('PUT', `/lab/requisitions/${id}/results`, { results }),
  releaseResults: (id: number) => request<LabRequisition>('POST', `/lab/requisitions/${id}/release`),
  requisitionFromOrder: (id: number, locationId: number) =>
    request<Requisition>('POST', `/reception/electronic-orders/${id}/requisition`, { locationId }),
  requisition: (id: number) => request<Requisition>('GET', `/reception/requisitions/${id}`),
  labRequisition: (id: number) => request<LabRequisition>('GET', `/lab/requisitions/${id}`),
  /** Break the glass for a restricted order or requisition: a reason of at least 10 characters, logged. */
  overrideOrder: (id: number, reason: string) => request<{ open: boolean }>('POST', `/reception/electronic-orders/${id}/override`, { reason }),
  overrideRequisition: (id: number, reason: string) => request<{ open: boolean }>('POST', `/lab/requisitions/${id}/override`, { reason }),
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
