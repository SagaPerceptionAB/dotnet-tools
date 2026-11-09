import { Link, Outlet } from 'react-router-dom';

export default function Layout() {
  return (
    <div>
      <nav>
        <Link to="/">Start</Link>
        <Link to="/products">Produkter</Link>
      </nav>
      <Outlet />
    </div>
  );
}
