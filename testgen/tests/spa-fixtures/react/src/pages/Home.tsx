import { useEffect } from 'react';
import { Link, NavLink } from 'react-router-dom';

export default function Home() {
  useEffect(() => {
    document.title = 'Butiken';
  }, []);

  return (
    <main>
      <h1>Välkommen till butiken</h1>
      <p>Vi är glada att se dig, det här är vår startsida.</p>
      <Link to="/products">Se alla produkter</Link>
      <NavLink to="/reports">Rapporter</NavLink>
      <a href="https://example.com/om-oss">Om oss</a>
      <a href="/about">Om butiken</a>
    </main>
  );
}
