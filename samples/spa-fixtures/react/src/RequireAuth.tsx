import { Navigate } from 'react-router-dom';
import { useAuth } from './useAuth';

export default function RequireAuth({ children }: { children: JSX.Element }) {
  const { user } = useAuth();
  return user ? children : <Navigate to="/login" replace />;
}
