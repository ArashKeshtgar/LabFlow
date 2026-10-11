import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router'
import { api, getDemoUser, setDemoUser, type AuthSettings, type DemoUser, type Me } from './api'
import { BookingPage } from './pages/BookingPage'
import { ReceptionPage } from './pages/ReceptionPage'

const FRONT_DESK = ['Reception', 'Collector', 'Admin']

export function App() {
  const [users, setUsers] = useState<DemoUser[]>([])
  const [current, setCurrent] = useState(getDemoUser())
  const [auth, setAuth] = useState<AuthSettings | null>(null)
  const [me, setMe] = useState<Me | null>(null)

  useEffect(() => {
    api.authSettings().then(s => {
      setAuth(s)
      if (s.signIn === 'clinichub') api.me().then(setMe).catch(() => setMe(null))
      else api.devUsers().then(setUsers).catch(() => setUsers([]))
    }).catch(() => setAuth({ signIn: 'demo', perClinicDatabase: false }))
  }, [])

  const connected = auth?.signIn === 'clinichub'
  const role = users.find(u => u.externalId === current)?.role
  // Connected, ClinicHub's permissions decide; with the demo menu, the seeded user's role.
  const isStaff = connected
    ? !!me?.permissions.some(p => p === 'frontdesk' || p === 'admin')
    : !!role && FRONT_DESK.includes(role)
  const signInError = new URLSearchParams(window.location.search).get('signInError')

  function switchUser(id: string) {
    setDemoUser(id)
    setCurrent(id)
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <img src="/favicon.svg" alt="" width={26} height={26} />
          <span>LabFlow</span>
          <span className="brand-sub">Ontario community lab</span>
        </div>
        <nav>
          <NavLink to="/" end>Book a visit</NavLink>
          <NavLink to="/reception">Reception</NavLink>
        </nav>
        {connected && (me?.userId
          ? (
            <div className="user-switch">
              <span>{me.name} · {me.clinic}</span>
              <a href="/api/auth/sso/signout">Sign out</a>
            </div>
          )
          : <a className="user-switch" href={`/api/auth/sso/start?returnUrl=${encodeURIComponent('/reception')}`}>Staff sign-in with ClinicHub</a>)}
        {!connected && users.length > 0 && (
          <label className="user-switch" title="Development only: sign in as a seeded demo user">
            <span>Demo user</span>
            <select value={current} onChange={e => switchUser(e.target.value)}>
              <option value="">Public (not signed in)</option>
              {users.map(u => <option key={u.externalId} value={u.externalId}>{u.displayName} - {u.role}</option>)}
            </select>
          </label>
        )}
      </header>

      <main>
        {signInError && <p className="card narrow error" role="alert">{signInError}</p>}
        <Routes>
          <Route path="/" element={<BookingPage />} />
          <Route path="/reception" element={<ReceptionPage key={connected ? me?.userId ?? 0 : current} canUse={isStaff} connected={connected} />} />
        </Routes>
      </main>

      <footer className="footer">Portfolio demo · synthetic data only · not for clinical use</footer>
    </div>
  )
}
