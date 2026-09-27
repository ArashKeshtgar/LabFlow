import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router'
import { api, getDemoUser, setDemoUser, type DemoUser } from './api'
import { BookingPage } from './pages/BookingPage'
import { ReceptionPage } from './pages/ReceptionPage'

const FRONT_DESK = ['Reception', 'Collector', 'Admin']

export function App() {
  const [users, setUsers] = useState<DemoUser[]>([])
  const [current, setCurrent] = useState(getDemoUser())

  useEffect(() => { api.devUsers().then(setUsers).catch(() => setUsers([])) }, [])

  const role = users.find(u => u.externalId === current)?.role
  const isStaff = !!role && FRONT_DESK.includes(role)

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
        {users.length > 0 && (
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
        <Routes>
          <Route path="/" element={<BookingPage />} />
          <Route path="/reception" element={<ReceptionPage key={current} canUse={isStaff} />} />
        </Routes>
      </main>

      <footer className="footer">Portfolio demo · synthetic data only · not for clinical use</footer>
    </div>
  )
}
