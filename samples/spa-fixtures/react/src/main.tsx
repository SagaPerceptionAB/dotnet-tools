import React, { lazy } from 'react';
import ReactDOM from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router-dom';
import Layout from './Layout';
import RequireAuth from './RequireAuth';
import NotFound from './pages/NotFound';
import Home from './pages/Home';
import Products from './pages/Products';
import ProductDetail from './pages/ProductDetail';
import ProductNew from './pages/ProductNew';
import Login from './pages/Login';
import Legacy from './pages/Legacy';
import { LEGACY_PATH } from './paths';

// Rapporterna laddas först när någon öppnar dem.
const Reports = lazy(() => import('./pages/Reports'));

const router = createBrowserRouter([
  {
    path: '/',
    element: <Layout />,
    children: [
      { index: true, element: <Home /> },
      {
        path: 'products',
        children: [
          { index: true, element: <Products /> },
          // Bara inloggade får lägga till produkter.
          { path: 'new', element: <RequireAuth><ProductNew /></RequireAuth> },
          { path: ':id', element: <ProductDetail /> },
        ],
      },
      { path: 'reports', element: <Reports /> },
      { path: 'settings', lazy: () => import('./pages/Settings') },
      { path: 'login', element: <Login /> },
      // Sökvägen är en konstant i en annan fil och går inte att läsa här.
      { path: LEGACY_PATH, element: <Legacy /> },
    ],
  },
  { path: '*', element: <NotFound /> },
]);

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>,
);
